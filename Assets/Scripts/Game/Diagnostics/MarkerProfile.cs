using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// A flat profile without the Editor: records every timed profiler marker Unity exposes, sums each over a run and
    /// lists where the time went, plus what was biggest inside the slowest frames. Development builds only (release
    /// players expose almost no markers). Times are inclusive, so a parent marker contains its children: read the list
    /// as "how much of the frame passed inside this", not as parts of a whole. Used by the benchmark (-benchmarkmarkers).
    /// </summary>
    public sealed class MarkerProfile : IDisposable
    {
        private readonly List<ProfilerRecorder> _recorders = new List<ProfilerRecorder>();
        private readonly List<string> _names = new List<string>();
        private double[] _totalNs;
        private long[] _calls;
        private int _frames;

        private struct Slow { public double FrameMs; public float At; public string What; }
        private readonly Slow[] _slow = new Slow[6];
        private int _slowCount;
        private readonly int[] _topScratch = new int[28];

        public int MarkerCount => _recorders.Count;

        public void Start()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (ProfilerRecorderHandle h in handles)
            {
                ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(h);
                if (d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;
                var r = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
                r.Start();
                _recorders.Add(r);
                _names.Add(d.Name);
            }
            _totalNs = new double[_recorders.Count];
            _calls = new long[_recorders.Count];
        }

        /// <summary>Call once per frame, at its start: adds the frame that just ended. Pass that frame's length to note slow ones.</summary>
        public void Frame(double lastFrameMs, float secondsIntoRun, double slowOverMs)
        {
            _frames++;
            bool slow = lastFrameMs > slowOverMs && (_slowCount < _slow.Length || lastFrameMs > Least());
            int tops = 0;
            for (int i = 0; i < _recorders.Count; i++)
            {
                ProfilerRecorder r = _recorders[i];
                long v = r.LastValue;
                if (v <= 0) continue;
                _totalNs[i] += v;
                _calls[i]++;
                if (!slow || Umbrella(_names[i])) continue;
                // Keep the few biggest markers of this slow frame.
                if (tops < _topScratch.Length) _topScratch[tops++] = i;
                else
                {
                    int min = 0;
                    for (int k = 1; k < tops; k++) if (_recorders[_topScratch[k]].LastValue < _recorders[_topScratch[min]].LastValue) min = k;
                    if (v > _recorders[_topScratch[min]].LastValue) _topScratch[min] = i;
                }
            }
            if (!slow) return;
            Array.Sort(_topScratch, 0, tops, Comparer<int>.Create((a, b) => _recorders[b].LastValue.CompareTo(_recorders[a].LastValue)));
            var sb = new StringBuilder();
            for (int k = 0; k < tops; k++)
                sb.Append(k > 0 ? ", " : "").Append(_names[_topScratch[k]]).Append(' ').Append((_recorders[_topScratch[k]].LastValue / 1e6).ToString("0.0", CultureInfo.InvariantCulture));
            var s = new Slow { FrameMs = lastFrameMs, At = secondsIntoRun, What = sb.ToString() };
            if (_slowCount < _slow.Length) { _slow[_slowCount++] = s; return; }
            int least = 0;
            for (int i = 1; i < _slowCount; i++) if (_slow[i].FrameMs < _slow[least].FrameMs) least = i;
            _slow[least] = s;
        }

        private double Least()
        {
            double m = double.MaxValue;
            for (int i = 0; i < _slowCount; i++) if (_slow[i].FrameMs < m) m = _slow[i].FrameMs;
            return m;
        }

        /// <summary>Markers that just wrap the whole frame or a whole phase; they would crowd out the informative ones.</summary>
        private static bool Umbrella(string n) =>
            n == "PlayerLoop" || n == "Main Thread" || n == "Render Thread" || n == "Idle" || n.StartsWith("Semaphore.", StringComparison.Ordinal) ||
            n.StartsWith("Gfx.WaitFor", StringComparison.Ordinal) || n == "GfxTask_ReadValue" || n.StartsWith("CPU ", StringComparison.Ordinal) ||
            n.StartsWith("Update.", StringComparison.Ordinal) ||
            n.StartsWith("PreLateUpdate.", StringComparison.Ordinal) || n.StartsWith("PostLateUpdate.", StringComparison.Ordinal) ||
            n.StartsWith("EarlyUpdate.", StringComparison.Ordinal) || n.StartsWith("PreUpdate.", StringComparison.Ordinal) ||
            n.StartsWith("Initialization.", StringComparison.Ordinal) || n.StartsWith("TimeUpdate.", StringComparison.Ordinal) ||
            n == "BehaviourUpdate" || n == "LateBehaviourUpdate";

        public string Report(int top)
        {
            var ci = CultureInfo.InvariantCulture;
            var order = new List<int>();
            for (int i = 0; i < _recorders.Count; i++) if (_totalNs[i] > 0) order.Add(i);
            order.Sort((a, b) => _totalNs[b].CompareTo(_totalNs[a]));
            var sb = new StringBuilder();
            sb.Append("  markers (").Append(_recorders.Count).Append(" recorded, ").Append(_frames).Append(" frames; ms per frame, inclusive):").AppendLine();
            for (int k = 0; k < Math.Min(top, order.Count); k++)
            {
                int i = order[k];
                sb.Append("    ").Append((_totalNs[i] / 1e6 / Math.Max(1, _frames)).ToString("0.0000", ci)).Append("  ").Append(_names[i])
                  .Append(_calls[i] < _frames * 0.98 ? "  (in " + _calls[i] + " frames)" : "").AppendLine();
            }
            Array.Sort(_slow, 0, _slowCount, Comparer<Slow>.Create((a, b) => b.FrameMs.CompareTo(a.FrameMs)));
            for (int i = 0; i < _slowCount; i++)
                sb.Append("  slow frame ").Append(_slow[i].FrameMs.ToString("0.0", ci)).Append(" ms @").Append(_slow[i].At.ToString("0.00", ci)).Append(" s: ").Append(_slow[i].What).AppendLine();
            return sb.ToString();
        }

        public void Dispose()
        {
            foreach (ProfilerRecorder r in _recorders) r.Dispose();
            _recorders.Clear();
        }
    }
}
