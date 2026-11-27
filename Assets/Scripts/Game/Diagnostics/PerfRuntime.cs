using System;
using Sculpt.Core.Diagnostics;
using Sculpt.Core.Net;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Rendering;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// Measures what the performance overlay and the benchmark show: frame times, garbage made per frame, the
    /// processor and graphics card time per frame (when Unity reports it), and each click's path from the hardware to
    /// the screen and to the server and back (<see cref="LatencyProbe"/>).
    ///
    /// Runs first in the frame, so "now" here is the moment the previous frame was handed to the graphics card and
    /// this one starts. It only reads the game; the one thing it sets is the local server's clock, which the server
    /// uses to tell clients how long their commands waited.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class PerfRuntime : MonoBehaviour
    {
        public static PerfRuntime Instance { get; private set; }

        public readonly FrameStats Frames = new FrameStats(2048);
        public readonly LatencyProbe Latency = new LatencyProbe();
        public RenderProfile Profile { get; private set; }

        /// <summary>Managed memory allocated per frame on the main thread, averaged over the last second (bytes). Negative: not available.</summary>
        public double AllocBytesPerFrame { get; private set; } = -1;
        /// <summary>Garbage collections since the game started.</summary>
        public int Collections => GC.CollectionCount(0);
        /// <summary>Processor and graphics card time per frame from Unity's frame timing, averaged over about half a second. Zero when not reported.</summary>
        public double CpuFrameMs { get; private set; }
        public double GpuFrameMs { get; private set; }
        public bool GpuKnown { get; private set; }

        /// <summary>What the last frame drew (Unity's render counters; zero when a counter is not available).</summary>
        public long DrawCalls => Counter(_drawCalls);
        public long Batches => Counter(_batches);
        public long SetPassCalls => Counter(_setPass);
        public long Triangles => Counter(_triangles);
        private ProfilerRecorder _drawCalls, _batches, _setPass, _triangles;

        private static long Counter(ProfilerRecorder r) => r.Valid ? r.LastValue : 0;

        /// <summary>
        /// Frames in which the game camera was actually drawn. Stays at zero while the engine's splash screen is up or
        /// the window has not been shown yet, even though Update already runs.
        /// </summary>
        public int RenderedFrames { get; private set; }
        private void OnCameraDrawn(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType == CameraType.Game) RenderedFrames++;
        }

        /// <summary>Milliseconds on the clock every latency number uses (the same timeline as input event times).</summary>
        public static double NowMs => Time.realtimeSinceStartupAsDouble * 1000.0;

        private NetSession _session;
        /// <summary>The session the overlays read network figures from.</summary>
        public NetSession Session => _session;
        private LocalPlayer _player;
        private UI.SettingsMenu _menu;
        private UI.MainMenu _mainMenu;
        private GameInput _input;
        private Func<double> _clock;
        private double _frameStartMs = -1;
        /// <summary>Benchmark only: pretend a click was read every this many seconds (0 = off), so the latency path is measured unattended.</summary>
        public float StandInClickSeconds;
        private double _nextStandInMs;
        private int _tickAtFrameStart = int.MinValue;
        private bool _sentThisFrame;
        private double _fireMs = double.NaN, _jumpMs = double.NaN, _punchMs = double.NaN, _healMs = double.NaN;

        private long _heapLast = -1, _allocWindow;
        private int _allocFrames;
        private double _allocWindowStartMs;

        /// <summary>Managed memory allocated since the game started, estimated from heap growth between frames (bytes).</summary>
        public long AllocatedTotal { get; private set; }
        /// <summary>Where the last finished frame's main-thread time went: every script Update, every LateUpdate, and the rest (HUD, rendering, waiting for the graphics card).</summary>
        public double LastUpdateMs { get; private set; }
        public double LastLateUpdateMs { get; private set; }
        private double _updateEndMs, _lateStartMs, _lateEndMs;
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private double _cpuSum, _gpuSum;
        private int _timingFrames;

        public static PerfRuntime Install(GameObject host, NetSession session, LocalPlayer player, GameInput input, RenderProfile profile)
        {
            var p = host.AddComponent<PerfRuntime>();
            host.AddComponent<PerfRuntimeLate>().Owner = p;
            p._session = session;
            p._player = player;
            p._input = input;
            p.Profile = profile;
            p._clock = () => NowMs;
            p._menu = host.GetComponent<UI.SettingsMenu>();
            p._mainMenu = host.GetComponent<UI.MainMenu>();
            session.ClientChanged += p.Latency.Clear;
            p._drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            p._batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            p._setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            p._triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            // The time the operating system stamped the event with, taken while the event is being processed (the
            // device's last update is this event). Kept only if it makes sense: not in the future, not ancient.
            input.Fire.performed += c => p._fireMs = EventMs(c);
            input.SculptPush.performed += c => p._healMs = EventMs(c);
            input.Jump.performed += c => p._jumpMs = EventMs(c);
            input.Punch.performed += c => p._punchMs = EventMs(c);
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += p.OnCameraDrawn;
            Instance = p;
            return p;
        }

        private static double EventMs(InputAction.CallbackContext c)
        {
            InputDevice d = c.control != null ? c.control.device : null;
            if (d == null) return double.NaN;
            double ms = d.lastUpdateTime * 1000.0, now = NowMs;
            return ms <= now && now - ms < 250.0 ? ms : double.NaN;
        }

        private void Update()
        {
            double now = NowMs;
            if (_session == null) return;
            if (_session.Server != null && _session.Server.Clock == null) _session.Server.Clock = _clock;

            NetClient net = _session.Client;
            // The server states read at the start of the last frame answer clicks; that frame has now gone out.
            if (_frameStartMs >= 0)
            {
                while (Latency.WaitingFor(out int want) && net.LastSnapshotTick >= want)
                    Latency.Answered(want, net.ServerHoldMs(want), _frameStartMs);
            }
            if (!_sentThisFrame && _frameStartMs >= 0) MarkSent(_frameStartMs); // the frame hook is missing: best guess
            Latency.FrameOut(now);

            if (_frameStartMs >= 0)
            {
                Frames.Add(now - _frameStartMs);
                LastUpdateMs = Math.Max(0, _updateEndMs - _frameStartMs);
                LastLateUpdateMs = Math.Max(0, _lateEndMs - _lateStartMs);
            }
            _frameStartMs = now;
            _sentThisFrame = false;
            _tickAtFrameStart = net.Joined ? net.Tick : int.MinValue;

            SampleAllocations(now);
            SampleFrameTiming();
            ReadPresses(now);
        }

        /// <summary>Presses the game will act on this frame (the same conditions the local player uses).</summary>
        private void ReadPresses(double now)
        {
            if (StandInClickSeconds > 0 && now >= _nextStandInMs)
            {
                _nextStandInMs = now + StandInClickSeconds * 1000.0;
                Latency.Press(ClickKind.Other, double.NaN, now);
                return;
            }
            if (_player == null || _input == null || !_player.CursorLocked || _player.InputBlocked || !_player.Net.Playing || _player.State.Dead) return;
            bool sculpt = _player.InSculptMode;
            if (!sculpt && _input.Fire.WasPressedThisFrame()) Latency.Press(ClickKind.Fire, _fireMs, now);
            else if (sculpt && _player.Heals && _player.State.Hand.CanConfirmSculpt && _input.SculptPush.WasPressedThisFrame()) Latency.Press(ClickKind.Heal, _healMs, now);
            else if (_input.Punch.WasPressedThisFrame()) Latency.Press(ClickKind.Punch, _punchMs, now);
            else if (_input.Jump.WasPressedThisFrame()) Latency.Press(ClickKind.Jump, _jumpMs, now);
        }

        /// <summary>Call right after the frame's packets were handed to the network (GameBootstrap, after the ticks).</summary>
        public static void PacketsSent()
        {
            PerfRuntime p = Instance;
            if (p != null) p.MarkSent(NowMs);
        }

        private void MarkSent(double now)
        {
            _sentThisFrame = true;
            NetClient net = _session.Client;
            // A waiting press rides in the first tick that ran after it was read.
            if (_tickAtFrameStart != int.MinValue && net.Joined && net.Tick > _tickAtFrameStart)
            {
                Latency.Ticked(_tickAtFrameStart + 1, now);
                Latency.Sent(now);
            }
        }

        private void SampleAllocations(double now)
        {
            // Unity's Mono does not count allocated bytes per thread, so use heap growth: between collections the
            // managed heap only grows by what was allocated. A frame in which the collector ran shows a drop and is
            // skipped, so this reads slightly low, never high.
            long heap = GC.GetTotalMemory(false);
            if (_heapLast >= 0)
            {
                if (heap > _heapLast)
                {
                    _allocWindow += heap - _heapLast;
                    AllocatedTotal += heap - _heapLast;
                }
                _allocFrames++;
            }
            _heapLast = heap;
            if (now - _allocWindowStartMs < 1000.0) return;
            if (_allocFrames > 0) AllocBytesPerFrame = (double)_allocWindow / _allocFrames;
            _allocWindow = 0;
            _allocFrames = 0;
            _allocWindowStartMs = now;
        }

        private void SampleFrameTiming()
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) == 0) return;
            _cpuSum += _timing[0].cpuFrameTime;
            _gpuSum += _timing[0].gpuFrameTime;
            if (++_timingFrames < 60) return;
            CpuFrameMs = _cpuSum / _timingFrames;
            GpuFrameMs = _gpuSum / _timingFrames;
            GpuKnown = GpuFrameMs > 0.001;
            _cpuSum = _gpuSum = 0;
            _timingFrames = 0;
        }

        private void LateUpdate() => _lateStartMs = NowMs;
        internal void UpdateEnded() => _updateEndMs = NowMs;
        internal void LateUpdateEnded()
        {
            _lateEndMs = NowMs;
            // The menus need their layout pass only while they show. Decided here, after every script's Update (a menu
            // opens in its own) and before anything is drawn, so the pass is never missing on a menu's first frame.
            if (_menu != null && _menu.useGUILayout != _menu.IsOpen) _menu.useGUILayout = _menu.IsOpen;
            if (_mainMenu != null && _session != null)
            {
                // The main menu also draws its "connecting" box while a join is in progress.
                bool showing = _mainMenu.IsOpen || (_session.Mode == NetMode.Client && !_session.Client.Joined);
                if (_mainMenu.useGUILayout != showing) _mainMenu.useGUILayout = showing;
            }
        }

        private void OnDestroy()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnCameraDrawn;
            _drawCalls.Dispose();
            _batches.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
            if (Instance == this) Instance = null;
        }
    }

    /// <summary>Runs last in Update and LateUpdate, to time those two phases of the frame for <see cref="PerfRuntime"/>.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class PerfRuntimeLate : MonoBehaviour
    {
        public PerfRuntime Owner;
        private void Update() { if (Owner != null) Owner.UpdateEnded(); }
        private void LateUpdate() { if (Owner != null) Owner.LateUpdateEnded(); }
    }
}
