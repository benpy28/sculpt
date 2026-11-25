using System;

namespace Sculpt.Core.Diagnostics
{
    /// <summary>
    /// Rolling frame-time statistics over the newest N frames: average, percentiles and the "1% low" figure
    /// competitive players quote (the average frame rate of the slowest 1% of frames). Pure data, no clock of its own:
    /// the caller feeds one frame time per rendered frame. Nothing here allocates after construction.
    /// </summary>
    public sealed class FrameStats
    {
        private readonly float[] _ms;
        private readonly float[] _sorted;
        private int _count, _next;
        private bool _sortedValid;
        private double _sum;

        public FrameStats(int capacity = 2048)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _ms = new float[capacity];
            _sorted = new float[capacity];
        }

        public int Capacity => _ms.Length;
        /// <summary>Frames currently held (at most <see cref="Capacity"/>).</summary>
        public int Count => _count;
        /// <summary>Every frame ever added, including ones that have since rolled out of the window.</summary>
        public long TotalFrames { get; private set; }

        public void Clear()
        {
            _count = 0;
            _next = 0;
            _sum = 0;
            _sortedValid = false;
            TotalFrames = 0;
        }

        /// <summary>Adds one frame time in milliseconds. Negative, infinite or NaN values are ignored.</summary>
        public void Add(double milliseconds)
        {
            if (!(milliseconds >= 0) || double.IsInfinity(milliseconds)) return;
            float v = (float)milliseconds;
            if (_count == _ms.Length) _sum -= _ms[_next];
            else _count++;
            _ms[_next] = v;
            _sum += v;
            _next = (_next + 1) % _ms.Length;
            _sortedValid = false;
            TotalFrames++;
        }

        /// <summary>The i-th newest frame time (0 = the newest), for drawing a trace. 0 when there is no such frame.</summary>
        public double Recent(int i)
        {
            if (i < 0 || i >= _count) return 0;
            int at = _next - 1 - i;
            if (at < 0) at += _ms.Length;
            return _ms[at];
        }

        public double AverageMs => _count == 0 ? 0 : Math.Max(0, _sum) / _count;
        /// <summary>Frames per second over the window: frames divided by the time they took (not the mean of per-frame rates).</summary>
        public double AverageFps => AverageMs <= 0 ? 0 : 1000.0 / AverageMs;
        public double MaxMs => _count == 0 ? 0 : Sorted()[_count - 1];
        public double MinMs => _count == 0 ? 0 : Sorted()[0];

        /// <summary>Frame time at this percentile, 0..1 (0.5 = median, 0.99 = only 1% of frames were slower). Nearest rank.</summary>
        public double PercentileMs(double p)
        {
            if (_count == 0) return 0;
            if (p <= 0) return Sorted()[0];
            if (p >= 1) return Sorted()[_count - 1];
            int rank = (int)Math.Ceiling(p * _count) - 1;
            return Sorted()[Math.Max(0, Math.Min(_count - 1, rank))];
        }

        /// <summary>
        /// Average frame rate of the slowest <paramref name="fraction"/> of frames (0.01 = "1% low", 0.001 = "0.1% low").
        /// At least one frame is always counted.
        /// </summary>
        public double LowFps(double fraction)
        {
            if (_count == 0) return 0;
            float[] s = Sorted();
            int n = Math.Max(1, Math.Min(_count, (int)Math.Round(_count * fraction)));
            double sum = 0;
            for (int i = _count - n; i < _count; i++) sum += s[i];
            return sum <= 0 ? 0 : 1000.0 * n / sum;
        }

        /// <summary>How many of the held frames took longer than this.</summary>
        public int CountOver(double milliseconds)
        {
            int n = 0;
            for (int i = 0; i < _count; i++) if (_ms[i] > milliseconds) n++;
            return n;
        }

        private float[] Sorted()
        {
            if (_sortedValid) return _sorted;
            // Before the buffer wraps, the live frames are slots 0.._count-1; after, every slot is live.
            Array.Copy(_ms, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            _sortedValid = true;
            return _sorted;
        }
    }
}
