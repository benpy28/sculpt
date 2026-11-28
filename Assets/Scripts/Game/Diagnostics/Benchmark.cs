using System;
using System.Globalization;
using System.IO;
using System.Text;
using Sculpt.Core;
using Sculpt.Core.Diagnostics;
using Sculpt.Game.Cameras;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// Repeatable performance run, started with the command line option -benchmark [seconds] (default 20), together
    /// with -offline, -host or -connect so the game starts in play instead of at the main menu: the camera
    /// flies a fixed route (an orbit that keeps the whole map in view, then a run at eye height down the middle of the
    /// buildings), the same every time, while frame times are recorded. At the end it writes a short report (average,
    /// 1% and 0.1% low, percentiles, garbage per frame, the latency probe's numbers), optionally saves a screenshot,
    /// and quits. Use it to compare settings or builds: same route, same machine, different numbers.
    ///
    /// Other options: -benchmarkout file (report path; default benchmark.txt beside the player log),
    /// -benchmarkshot file.png, -benchmarklabel text, -benchmarkwarmlap seconds (fly the whole route once, unmeasured,
    /// before the measured lap: separates first-time costs from the steady state), -benchmarkmarkers (development
    /// builds: adds a flat profile of Unity's profiler markers over the whole run and what was biggest in the slowest
    /// frames), -benchmarkscan (instead of the route: hold the camera still and measure what each script type costs per
    /// frame by switching it off for a moment), -benchmarkdisable TypeA,TypeB (run with those scripts switched off).
    /// -benchmarkshotmenu takes the picture with the settings menu open (to check the menu without a hand on the
    /// keyboard). Video options such as -lean 0 or -vsync 1 apply as usual.
    /// The mouse is not captured during a run.
    /// </summary>
    public sealed class Benchmark : MonoBehaviour, ICameraMode
    {
        public const float SettleSeconds = 3f;
        /// <summary>Seconds before measuring starts: settling, plus the optional unmeasured lap.</summary>
        public float WarmupSeconds => SettleSeconds + _warmLap;
        private float _warmLap;
        // Every stand-in click of the run, for medians (the probe itself only keeps the newest 32).
        private readonly FrameStats _tickWait = new FrameStats(4096), _clickToFrame = new FrameStats(4096), _server = new FrameStats(4096), _total = new FrameStats(4096);
        private int _localSeen, _doneSeen;
        private MarkerProfile _markers;
        private bool _wantMarkers, _wantScan, _shotMenu;
        private int _waitedFrames;
        private float _firstDrawnAt = -1f;
        private ComponentScan _scan;
        private string[] _disable;

        private PerfRuntime _perf;
        private LocalPlayer _player;
        private float _seconds = 20f;
        private string _outPath, _shotPath, _label = "";
        private Vector3 _centre;
        private float _size;
        private float _t;
        private bool _measuring, _done;
        private int _quitInFrames = -1;
        private readonly FrameStats _frames = new FrameStats(262144);
        private long _allocStart;
        private int _gcStart, _allocFrames, _gcLast;
        private bool _shotPose;

        private struct Hitch { public float At; public double Ms, UpdateMs, LateMs; public int Collections; }
        private readonly Hitch[] _hitches = new Hitch[10];
        private int _hitchCount;
        private double _cpuSum, _gpuSum;
        private int _timingSamples;
        private double _drawCalls, _batches, _setPass, _triangles;
        private long _trianglesMax;

        /// <summary>Starts a run if the command line asks for one. Returns null otherwise.</summary>
        public static Benchmark InstallIfRequested(GameObject host, PerfRuntime perf, LocalPlayer player, CameraRig rig, Vector3 mapCentre, float mapSize)
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => string.Equals(a, "-benchmark", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            var b = host.AddComponent<Benchmark>();
            b._perf = perf;
            b._player = player;
            b._centre = mapCentre;
            b._size = mapSize;
            if (at + 1 < args.Length && float.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s >= 1f) b._seconds = Mathf.Min(s, 600f);
            b._outPath = Arg(args, "-benchmarkout") ?? Path.Combine(Application.persistentDataPath, "benchmark.txt");
            b._shotPath = Arg(args, "-benchmarkshot");
            b._label = Arg(args, "-benchmarklabel") ?? "";
            if (float.TryParse(Arg(args, "-benchmarkwarmlap") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out float lap) && lap > 0) b._warmLap = Mathf.Min(lap, 120f);
            b._wantMarkers = Array.Exists(args, a => string.Equals(a, "-benchmarkmarkers", StringComparison.OrdinalIgnoreCase)) && Debug.isDebugBuild;
            b._wantScan = Array.Exists(args, a => string.Equals(a, "-benchmarkscan", StringComparison.OrdinalIgnoreCase));
            b._shotMenu = Array.Exists(args, a => string.Equals(a, "-benchmarkshotmenu", StringComparison.OrdinalIgnoreCase));
            string off = Arg(args, "-benchmarkdisable");
            if (!string.IsNullOrEmpty(off)) b._disable = off.Split(',');
            // -benchmarkplay: keep the game's own camera (first person, or the battle royale's watch camera once out),
            // for a run where -autoplay plays the match. Otherwise the fixed route.
            if (!Array.Exists(args, a => string.Equals(a, "-benchmarkplay", StringComparison.OrdinalIgnoreCase))) rig.Mode = b;
            player.InputBlocked = true;
            LocalPlayer.LockCursor(false);
            Debug.Log($"[SCULPT] Benchmark: {b._seconds:0} s after a {b.WarmupSeconds:0} s warm-up. Report: {b._outPath}");
            return b;
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        public void UpdatePose(float dt, out Vector3 position, out Quaternion rotation, out float horizontalFov)
        {
            horizontalFov = 95f;
            // The route is a function of run time only, so every run shows the same views for the same share of time.
            float u = _shotPose || _wantScan ? 0.06f : Mathf.Clamp01((_t - WarmupSeconds) / _seconds);
            if (!_shotPose && !_wantScan && _warmLap > 0 && _t < WarmupSeconds) u = Mathf.Clamp01((_t - SettleSeconds) / _warmLap); // the unmeasured lap
            if (u < 0.45f)
            {
                // Orbit: the whole map in view, the heaviest thing the game draws.
                float a = u / 0.45f * Mathf.PI * 2f;
                float r = _size * 0.42f;
                position = _centre + new Vector3(Mathf.Sin(a) * r, 16f, -Mathf.Cos(a) * r);
                rotation = Quaternion.LookRotation(_centre + new Vector3(0, 4f, 0) - position, Vector3.up);
            }
            else
            {
                // Eye height straight through the buildings, spawn to spawn, looking around as a player would.
                float k = (u - 0.45f) / 0.55f;
                float x = _centre.x + _size * 0.17f;
                position = new Vector3(x, 1.7f, _centre.z + _size * Mathf.Lerp(-0.4f, 0.4f, k));
                rotation = Quaternion.Euler(Mathf.Sin(k * 31f) * 8f, Mathf.Sin(k * 19f) * 55f, 0f);
            }
        }

        private void Update()
        {
            if (_done)
            {
                if (_shotMenu && _player != null) _player.InputBlocked = true;
                // The capture happens at the end of a frame; give it a few frames to reach the disk before quitting.
                if (_shotPose && _quitInFrames == 10) ScreenCapture.CaptureScreenshot(_shotPath);
                if (_quitInFrames > 0 && --_quitInFrames == 0) Quit();
                return;
            }
            // The run's clock starts once the game is really being drawn. While the engine's splash screen is up (or the
            // window is not shown yet) Update runs but nothing is drawn, and the first drawn frame then pays for every
            // shader, font and first-time code path at once (about half a second, at load).
            if (_perf.RenderedFrames < 3)
            {
                _waitedFrames++;
                return;
            }
            if (_firstDrawnAt < 0) _firstDrawnAt = Time.realtimeSinceStartup;
            if (_disable != null)
            {
                foreach (MonoBehaviour mb in ComponentScan.All())
                    if (mb != null && Array.IndexOf(_disable, mb.GetType().Name) >= 0) mb.enabled = false;
                _disable = null;
            }
            _t += Time.unscaledDeltaTime;
            if (_player != null) _player.InputBlocked = true;
            if (_markers != null) _markers.Frame(_perf.Frames.Recent(0), _t - WarmupSeconds, 6.0);
            if (!_measuring)
            {
                if (_t < WarmupSeconds) return;
                _measuring = true;
                if (_wantMarkers)
                {
                    // Started here, not at load: Unity only lists a marker once it has been hit.
                    _markers = new MarkerProfile();
                    _markers.Start();
                }
                // Stand-in clicks so the latency path (tick wait, packet, server hold, answer) is measured without a hand on the mouse.
                _perf.StandInClickSeconds = 0.113f;
                _frames.Clear();
                _perf.Latency.Clear();
                _allocStart = _perf.AllocatedTotal;
                _gcStart = _gcLast = GC.CollectionCount(0);
                _othersCpuStart = OthersCpuSeconds();
                _othersClock = System.Diagnostics.Stopwatch.StartNew();
                _localSeen = _doneSeen = 0;
                if (_wantScan)
                {
                    _scan = new ComponentScan(_perf);
                    _scan.Begin(new System.Collections.Generic.HashSet<Type>
                    {
                        typeof(Benchmark), typeof(PerfRuntime), typeof(PerfRuntimeLate), typeof(Sculpt.Game.Bootstrap.GameBootstrap),
                        typeof(CameraRig), typeof(Sculpt.Game.Rendering.RenderProfile),
                    });
                }
                return;
            }
            if (_scan != null)
            {
                _scan.Frame(Time.unscaledDeltaTime);
                _frames.Add(_perf.Frames.Recent(0));
                _allocFrames++;
                if (_scan.Finished) Finish();
                return;
            }

            LatencyProbe probe = _perf.Latency;
            if (probe.LocalCompleted != _localSeen)
            {
                _localSeen = probe.LocalCompleted;
                _tickWait.Add(probe.LastLocal.TickWaitMs);
                _clickToFrame.Add(probe.LastLocal.ClickToFrameMs);
            }
            if (probe.Completed != _doneSeen)
            {
                _doneSeen = probe.Completed;
                if (probe.Last.ServerKnown) _server.Add(probe.Last.ServerMs);
                _total.Add(probe.Last.TotalMs);
            }

            double ms = _perf.Frames.Recent(0); // the frame that just went out
            _frames.Add(ms);
            _allocFrames++;
            _drawCalls += _perf.DrawCalls;
            _batches += _perf.Batches;
            _setPass += _perf.SetPassCalls;
            long tris = _perf.Triangles;
            _triangles += tris;
            if (tris > _trianglesMax) _trianglesMax = tris;
            int gc = GC.CollectionCount(0);
            NoteHitch(new Hitch { At = _t - WarmupSeconds, Ms = ms, UpdateMs = _perf.LastUpdateMs, LateMs = _perf.LastLateUpdateMs, Collections = gc - _gcLast });
            _gcLast = gc;
            if (_perf.CpuFrameMs > 0)
            {
                _cpuSum += _perf.CpuFrameMs;
                _gpuSum += _perf.GpuFrameMs;
                _timingSamples++;
            }
            if (_t >= WarmupSeconds + _seconds) Finish();
        }

        /// <summary>Keeps the ten slowest frames of the run.</summary>
        private void NoteHitch(in Hitch h)
        {
            if (_hitchCount < _hitches.Length) { _hitches[_hitchCount++] = h; return; }
            int least = 0;
            for (int i = 1; i < _hitchCount; i++) if (_hitches[i].Ms < _hitches[least].Ms) least = i;
            if (h.Ms > _hitches[least].Ms) _hitches[least] = h;
        }

        private double _othersCpuStart, _othersCores = -1;
        private System.Diagnostics.Stopwatch _othersClock;

        /// <summary>Processor time used so far by every other process we can read (other games, tests, builds on this PC
        /// make a run's 1% lows meaningless: the report says how busy the machine was).</summary>
        private static double OthersCpuSeconds()
        {
            double sum = 0;
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (p.Id != me) sum += p.TotalProcessorTime.TotalSeconds; } catch { }
                p.Dispose();
            }
            return sum;
        }

        private void Finish()
        {
            _done = true;
            _othersCores = _othersClock != null ? (OthersCpuSeconds() - _othersCpuStart) / Math.Max(0.001, _othersClock.Elapsed.TotalSeconds) : -1;
            string report = Report((_perf.AllocatedTotal - _allocStart) / (double)Math.Max(1, _allocFrames));
            Debug.Log("[SCULPT] " + report);
            try
            {
                string dir = Path.GetDirectoryName(_outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_outPath, report + Environment.NewLine);
            }
            catch (Exception e) { Debug.LogWarning("[SCULPT] Benchmark report not written: " + e.Message); }
            if (!string.IsNullOrEmpty(_shotPath))
            {
                _shotPose = true; // back to a view of the buildings for the picture
                _quitInFrames = 14;
                if (_shotMenu)
                {
                    var menu = GetComponent<Sculpt.Game.UI.SettingsMenu>();
                    if (menu != null && !menu.IsOpen)
                    {
                        if (_player != null) _player.InputBlocked = false; // the menu only opens when nothing else holds the keyboard
                        typeof(Sculpt.Game.UI.SettingsMenu).GetMethod("Open", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(menu, null);
                    }
                }
            }
            else _quitInFrames = 2;
        }

        private string Report(double allocPerFrame)
        {
            var ci = CultureInfo.InvariantCulture;
            FrameStats f = _frames;
            LatencyProbe p = _perf.Latency;
            var sb = new StringBuilder(512);
            sb.Append("Benchmark");
            if (_label.Length > 0) sb.Append(" [").Append(_label).Append(']');
            sb.Append(": ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci)).AppendLine();
            sb.Append("  ").Append(Screen.width).Append('x').Append(Screen.height).Append(", ").Append(SystemInfo.graphicsDeviceName).Append(", ")
              .Append(SystemInfo.graphicsDeviceType).Append(", ").Append(SystemInfo.processorType).Append(", tick ").Append(SimTime.TicksPerSecond).Append(" Hz").AppendLine();
            sb.Append("  ").Append(_perf.Profile != null ? _perf.Profile.Summary : "").AppendLine();
            sb.Append(string.Format(ci, "  other processes used {0:0.00} cores on average during the run ({1} logical cores)", _othersCores, Environment.ProcessorCount)).AppendLine();
            sb.Append(string.Format(ci, "  first drawn frame {0:0.0} s after launch ({1} undrawn frames before it)", _firstDrawnAt, _waitedFrames)).AppendLine();
            sb.Append(string.Format(ci, "  frames {0} in {1:0.0} s: avg {2:0.0} fps ({3:0.000} ms), 1% low {4:0.0}, 0.1% low {5:0.0}", f.Count, _seconds, f.AverageFps, f.AverageMs, f.LowFps(0.01), f.LowFps(0.001))).AppendLine();
            sb.Append(string.Format(ci, "  frame ms: median {0:0.000}, p95 {1:0.000}, p99 {2:0.000}, p99.9 {3:0.000}, worst {4:0.000}, over 2x avg: {5}",
                f.PercentileMs(0.5), f.PercentileMs(0.95), f.PercentileMs(0.99), f.PercentileMs(0.999), f.MaxMs, f.CountOver(f.AverageMs * 2))).AppendLine();
            sb.Append(string.Format(ci, "  per frame: CPU {0:0.000} ms, GPU {1}, garbage {2}, collections {3}",
                _timingSamples > 0 ? _cpuSum / _timingSamples : 0, _timingSamples > 0 && _gpuSum > 0 ? (_gpuSum / _timingSamples).ToString("0.000", ci) + " ms" : "n/a",
                allocPerFrame >= 0 ? (allocPerFrame / 1024.0).ToString("0.000", ci) + " KB" : "n/a", GC.CollectionCount(0) - _gcStart)).AppendLine();
            int nf = Math.Max(1, _allocFrames);
            sb.Append(string.Format(ci, "  drawn per frame: {0:0} draw calls in {1:0} batches, {2:0} shader passes, {3:0} thousand triangles (most {4:0} thousand)",
                _drawCalls / nf, _batches / nf, _setPass / nf, _triangles / nf / 1000.0, _trianglesMax / 1000.0)).AppendLine();
            int renderers = 0, enabled = 0;
            var byOwner = new System.Collections.Generic.Dictionary<string, int[]>();
            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                renderers++;
                bool on = r.enabled && r.gameObject.activeInHierarchy;
                if (on) enabled++;
                // Grouped by the object under the root that owns it, numbers and spaces after the first word dropped.
                Transform t = r.transform;
                while (t.parent != null && t.parent.parent != null) t = t.parent;
                string key = t.name;
                int cut = key.IndexOfAny(new[] { ' ', '(' });
                if (cut > 0) key = key.Substring(0, cut);
                if (t.parent != null) key = t.parent.name + "/" + key;
                if (!byOwner.TryGetValue(key, out int[] n)) byOwner[key] = n = new int[2];
                n[0]++;
                if (on) n[1]++;
            }
            sb.Append(string.Format(ci, "  renderers in the scene at the end: {0} ({1} on)", renderers, enabled)).AppendLine();
            var owners = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int[]>>(byOwner);
            owners.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
            sb.Append("   by owner:");
            for (int i = 0; i < owners.Count && i < 12; i++) sb.Append(string.Format(ci, "  {0} {1} ({2} on)", owners[i].Key, owners[i].Value[0], owners[i].Value[1]));
            sb.AppendLine();
            sb.Append(string.Format(ci, "  latency, {0} stand-in clicks ({1} lost), median / average ms: tick wait {2:0.0} / {3:0.0}, read to frame sent {4:0.0} / {5:0.0}, server hold {6:0.0} / {7:0.0}, read to server result on a frame {8:0.0} / {9:0.0} (worst {10:0.0})",
                _total.Count, p.TimedOut, _tickWait.PercentileMs(0.5), _tickWait.AverageMs, _clickToFrame.PercentileMs(0.5), _clickToFrame.AverageMs,
                _server.PercentileMs(0.5), _server.AverageMs, _total.PercentileMs(0.5), _total.AverageMs, _total.MaxMs)).AppendLine();
            Array.Sort(_hitches, 0, _hitchCount, System.Collections.Generic.Comparer<Hitch>.Create((a, b) => b.Ms.CompareTo(a.Ms)));
            sb.Append("  slowest frames (ms @ seconds into the run; scripts Update / LateUpdate; collections):");
            for (int i = 0; i < _hitchCount; i++)
                sb.Append(string.Format(ci, "  {0:0.0}@{1:0.00} [{2:0.0}/{3:0.0}{4}]", _hitches[i].Ms, _hitches[i].At, _hitches[i].UpdateMs, _hitches[i].LateMs, _hitches[i].Collections > 0 ? " gc" : ""));
            if (_scan != null) sb.AppendLine().Append(_scan.Report());
            if (_markers != null)
            {
                sb.AppendLine().Append(_markers.Report(70));
                _markers.Dispose();
                _markers = null;
            }
            return sb.ToString();
        }

        private void OnDestroy() => _markers?.Dispose();

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
