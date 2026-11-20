using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Bad-network settings for one direction of a link. Milliseconds and percent.</summary>
    public sealed class LinkConditions
    {
        public double LatencyMs;   // one way
        public double JitterMs;    // +/- uniform, unreliable packets may reorder
        public double LossPercent; // unreliable packets only; reliable ones are delayed, never lost

        public bool IsPerfect => LatencyMs <= 0 && JitterMs <= 0 && LossPercent <= 0;
    }

    /// <summary>
    /// Deterministic fake network for one direction: delays, jitters and drops packets.
    /// Used by tests and by the game's dev settings (simulated ping and loss) on top of the real transport.
    /// </summary>
    public sealed class SimulatedLink
    {
        public readonly LinkConditions Conditions;
        public int Sent { get; private set; }
        public int Dropped { get; private set; }

        private struct Packet
        {
            public double DueMs;
            public long Seq;
            public byte[] Data;
            public bool Reliable;
        }

        private readonly List<Packet> _queue = new List<Packet>();
        private Random _rng;
        private long _seq;
        private double _lastReliableDue = double.MinValue;

        public SimulatedLink(LinkConditions conditions, int seed = 12345)
        {
            Conditions = conditions ?? new LinkConditions();
            _rng = new Random(seed);
        }

        public void Send(byte[] data, bool reliable, double nowMs)
        {
            Sent++;
            LinkConditions c = Conditions;
            if (!reliable && c.LossPercent > 0 && _rng.NextDouble() * 100.0 < c.LossPercent)
            {
                Dropped++;
                return;
            }
            double due = nowMs + Math.Max(0, c.LatencyMs);
            if (c.JitterMs > 0) due += (_rng.NextDouble() * 2 - 1) * c.JitterMs;
            if (due < nowMs) due = nowMs;
            if (reliable)
            {
                // Reliable traffic stays in order (as a reliable channel would deliver it).
                if (due < _lastReliableDue) due = _lastReliableDue;
                _lastReliableDue = due;
            }
            _queue.Add(new Packet { DueMs = due, Seq = _seq++, Data = data, Reliable = reliable });
        }

        /// <summary>Delivers every packet due by nowMs, in due-time order.</summary>
        public void Deliver(double nowMs, Action<byte[]> receive)
        {
            if (_queue.Count == 0) return;
            _queue.Sort((a, b) => a.DueMs != b.DueMs ? a.DueMs.CompareTo(b.DueMs) : a.Seq.CompareTo(b.Seq));
            int n = 0;
            while (n < _queue.Count && _queue[n].DueMs <= nowMs) n++;
            if (n == 0) return;
            var due = _queue.GetRange(0, n);
            _queue.RemoveRange(0, n);
            foreach (Packet p in due) receive(p.Data);
        }

        public int InFlight => _queue.Count;

        public void Clear() => _queue.Clear();
    }
}
