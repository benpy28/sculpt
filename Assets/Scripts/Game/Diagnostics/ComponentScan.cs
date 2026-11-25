using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// Finds out what each script costs per frame without a profiler window: with the camera held still, it switches
    /// one kind of component off at a time for a moment and measures how much the frame, the Update and LateUpdate
    /// phases, HUD drawing and garbage drop compared with everything on. Used by the benchmark (-benchmarkscan).
    /// Switching a script off only stops its own per-frame callbacks, so the game keeps running; the numbers are
    /// differences against the average of the "everything on" windows measured before, between and after.
    /// </summary>
    public sealed class ComponentScan
    {
        public const float WindowSeconds = 1.2f, SkipSeconds = 0.25f;

        private struct Window
        {
            public string Name;
            public double FrameMs, UpdateMs, LateMs, GuiMs, GarbageBytes;
            public int Frames;
        }

        private readonly PerfRuntime _perf;
        private readonly List<List<MonoBehaviour>> _groups = new List<List<MonoBehaviour>>();
        private readonly List<string> _groupNames = new List<string>();
        private readonly List<Window> _results = new List<Window>();
        private readonly List<Window> _baselines = new List<Window>();
        private ProfilerRecorder _gui;
        private bool _guiValid;

        private int _step = -1;          // -1 = not started; even = everything on; odd = group (_step / 2) off
        private float _stepTime;
        private Window _acc;
        private long _allocAtStart;

        public bool Finished { get; private set; }

        public ComponentScan(PerfRuntime perf) => _perf = perf;

        public void Begin(HashSet<Type> never)
        {
            var byType = new Dictionary<Type, List<MonoBehaviour>>();
            foreach (MonoBehaviour mb in All())
            {
                if (mb == null || !mb.enabled) continue;
                Type t = mb.GetType();
                if (never.Contains(t) || t.Namespace == null || !t.Namespace.StartsWith("Sculpt.", StringComparison.Ordinal)) continue;
                if (!byType.TryGetValue(t, out var list)) byType[t] = list = new List<MonoBehaviour>();
                list.Add(mb);
            }
            foreach (var kv in byType)
            {
                _groups.Add(kv.Value);
                _groupNames.Add(kv.Key.Name);
            }
            _gui = ProfilerRecorder.StartNew(ProfilerCategory.Gui, "GUI.Repaint", 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
            _guiValid = _gui.Valid;
            StartStep(0);
        }

        internal static MonoBehaviour[] All()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
        }

        private void StartStep(int step)
        {
            _step = step;
            _stepTime = 0;
            _acc = new Window { Name = step % 2 == 0 ? "(all on)" : _groupNames[step / 2] };
            _allocAtStart = -1;
            if (step % 2 == 1) foreach (MonoBehaviour mb in _groups[step / 2]) if (mb != null) mb.enabled = false;
        }

        /// <summary>Call once per frame at its start (after the perf runtime has closed the last frame).</summary>
        public void Frame(float dt)
        {
            if (Finished || _step < 0) return;
            _stepTime += dt;
            if (_stepTime >= SkipSeconds)
            {
                if (_allocAtStart < 0) _allocAtStart = _perf.AllocatedTotal;
                _acc.FrameMs += _perf.Frames.Recent(0);
                _acc.UpdateMs += _perf.LastUpdateMs;
                _acc.LateMs += _perf.LastLateUpdateMs;
                if (_guiValid) _acc.GuiMs += _gui.LastValue / 1e6;
                _acc.Frames++;
            }
            if (_stepTime < SkipSeconds + WindowSeconds) return;

            _acc.GarbageBytes = _allocAtStart >= 0 ? _perf.AllocatedTotal - _allocAtStart : 0;
            if (_step % 2 == 1)
            {
                foreach (MonoBehaviour mb in _groups[_step / 2]) if (mb != null) mb.enabled = true;
                _results.Add(_acc);
            }
            else _baselines.Add(_acc);

            int next = _step + 1;
            if (next / 2 >= _groups.Count && next % 2 == 1) { Finished = true; _gui.Dispose(); return; }
            StartStep(next);
        }

        public string Report()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            Window b = default;
            foreach (Window w in _baselines)
            {
                b.FrameMs += w.FrameMs; b.UpdateMs += w.UpdateMs; b.LateMs += w.LateMs; b.GuiMs += w.GuiMs; b.GarbageBytes += w.GarbageBytes; b.Frames += w.Frames;
            }
            int n = Math.Max(1, b.Frames);
            double frame = b.FrameMs / n, update = b.UpdateMs / n, late = b.LateMs / n, gui = b.GuiMs / n, garbage = b.GarbageBytes / n;
            sb.Append(string.Format(ci, "  component scan, everything on: frame {0:0.000} ms, Update {1:0.000}, LateUpdate {2:0.000}, HUD draw {3}, garbage {4:0.00} KB/frame",
                frame, update, late, _guiValid ? gui.ToString("0.000", ci) : "n/a", garbage / 1024.0)).AppendLine();
            sb.Append("  saved per frame with one script type off (ms: frame / Update / LateUpdate / HUD draw; garbage KB):").AppendLine();
            var order = new List<Window>(_results);
            order.Sort((x, y) => (y.FrameMs / Math.Max(1, y.Frames)).CompareTo(x.FrameMs / Math.Max(1, x.Frames)));
            order.Reverse(); // biggest saving (lowest remaining frame time) first
            foreach (Window w in order)
            {
                int f = Math.Max(1, w.Frames);
                sb.Append(string.Format(ci, "    {0,-20} {1,7:0.000} / {2,6:0.000} / {3,6:0.000} / {4,6}   {5,6:0.00}",
                    w.Name, frame - w.FrameMs / f, update - w.UpdateMs / f, late - w.LateMs / f,
                    _guiValid ? (gui - w.GuiMs / f).ToString("0.000", ci) : "n/a", (garbage - w.GarbageBytes / f) / 1024.0)).AppendLine();
            }
            return sb.ToString();
        }
    }
}
