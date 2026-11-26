using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>
    /// Plays a replay file into a spectator client: a clock you can pause, speed up and drag, and jumps to kills. Going
    /// back (or far forward) starts a fresh client from the nearest key frame and runs silently up to the new time;
    /// going forward a little just runs the frames in between silently.
    /// </summary>
    public sealed class ReplayPlayer
    {
        public readonly ReplayFile File;
        private readonly Func<NetClient> _makeClient;

        /// <summary>The client being fed. Replaced on a backward seek (see <see cref="ClientChanged"/>).</summary>
        public NetClient Client { get; private set; }
        public event Action ClientChanged;

        /// <summary>Current replay time, in server ticks (fractional).</summary>
        public double Time { get; private set; }
        public double Speed = 1;
        public bool Paused;
        public const double MinSpeed = 0.125, MaxSpeed = 8;
        /// <summary>A forward jump longer than this starts again from a key frame instead of running every frame.</summary>
        public double FarJumpTicks = 60 * SimTime.TicksPerSecond;
        /// <summary>Seeks done (each one rebuilt the client or ran frames silently).</summary>
        public int Seeks { get; private set; }
        public int Rebuilds { get; private set; }

        public double StartTick => File.FirstTick - 1;
        public double EndTick => File.LastTick;
        public bool AtEnd => Time >= EndTick;

        private int _seg = -1;
        private byte[] _raw;
        private readonly List<ReplayFile.Record> _frames = new List<ReplayFile.Record>();
        private readonly List<int> _frameTicks = new List<int>();
        private int _next;          // next frame to feed in _frames
        private int _fedTick = int.MinValue;

        public ReplayPlayer(ReplayFile file, Func<NetClient> makeClient)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            _makeClient = makeClient ?? throw new ArgumentNullException(nameof(makeClient));
            if (file.Segments.Count == 0) throw new NetFormatException("The replay has no complete segment.");
            Rebuild(StartTick);
        }

        /// <summary>Moves the clock by a frame's real time (scaled by speed) and feeds what is now due.</summary>
        public void Advance(double dtSeconds)
        {
            if (!Paused && !AtEnd) Time = Math.Min(EndTick, Time + dtSeconds * SimTime.TicksPerSecond * Speed);
            FeedTo(Time);
            Client.SetSpectatorTime(Time - File.Header.SnapEveryTicks);
        }

        /// <summary>Jumps to a tick (clamped to the replay). Effects of the frames skipped over are not shown.</summary>
        public void Seek(double tick)
        {
            tick = Math.Max(StartTick, Math.Min(EndTick, tick));
            Seeks++;
            if (tick < _fedTick || tick - _fedTick > FarJumpTicks) Rebuild(tick);
            else
            {
                Client.Muted = true;
                try { FeedTo(tick); }
                finally { Client.Muted = false; }
                Client.Combat.Clear();
            }
            Time = tick;
            Client.SetSpectatorTime(Time - File.Header.SnapEveryTicks);
        }

        public void SeekSeconds(double delta) => Seek(Time + delta * SimTime.TicksPerSecond);

        /// <summary>The first kill after the current time (a little lead-in so you see it coming), or -1.</summary>
        public int NextKill(double leadSeconds = 3)
        {
            double lead = leadSeconds * SimTime.TicksPerSecond;
            for (int i = 0; i < File.Markers.Count; i++)
                if (File.Markers[i].Tick - lead > Time + 1) return i;
            return -1;
        }

        public int PreviousKill(double leadSeconds = 3)
        {
            double lead = leadSeconds * SimTime.TicksPerSecond;
            for (int i = File.Markers.Count - 1; i >= 0; i--)
                if (File.Markers[i].Tick - lead < Time - SimTime.TicksPerSecond) return i;
            return -1;
        }

        public void JumpToKill(int marker, double leadSeconds = 3)
        {
            if (marker < 0 || marker >= File.Markers.Count) return;
            Seek(File.Markers[marker].Tick - leadSeconds * SimTime.TicksPerSecond);
        }

        // ---------------------------------------------------------------- Feeding

        private void Rebuild(double tick)
        {
            Rebuilds++;
            Client = _makeClient();
            Client.ExternalClock = true;
            _fedTick = int.MinValue;
            LoadSegment(File.SegmentFor(tick));
            // The key frame gives the whole state at the end of FirstTick - 1; then run silently up to the tick.
            Client.Muted = true;
            try
            {
                FeedKey();
                FeedTo(tick);
            }
            finally { Client.Muted = false; }
            Client.Combat.Clear();
            Time = tick;
            Client.SetSpectatorTime(Time - File.Header.SnapEveryTicks);
            ClientChanged?.Invoke();
        }

        private void LoadSegment(int i)
        {
            _seg = i;
            _raw = File.SegmentBytes(i);
            _frames.Clear();
            _frameTicks.Clear();
            _next = 0;
            foreach (ReplayFile.Record rec in ReplayFile.Records(_raw))
            {
                if (rec.Kind != ReplayRecord.Key && rec.Kind != ReplayRecord.Frame) continue;
                var r = new ByteReader(_raw, rec.Offset, rec.Length);
                SpecProtocol.FrameHeader h = SpecProtocol.ReadFrameHeader(ref r);
                _frames.Add(rec);
                _frameTicks.Add(h.Tick);
            }
        }

        private void FeedKey()
        {
            // The first record of a segment is its key frame.
            if (_frames.Count == 0) return;
            Feed(0);
            _next = 1;
        }

        /// <summary>Feeds every frame with a tick at or before `tick`, moving on to later segments as needed.</summary>
        private void FeedTo(double tick)
        {
            while (true)
            {
                while (_next < _frames.Count && _frameTicks[_next] <= tick)
                {
                    Feed(_next);
                    _next++;
                }
                if (_next < _frames.Count || _seg + 1 >= File.Segments.Count) break;
                if (File.Segments[_seg + 1].FirstTick > tick) break;
                // Into the next segment: its key frame only carries the absolute snapshot for a running client.
                LoadSegment(_seg + 1);
            }
        }

        private void Feed(int i)
        {
            ReplayFile.Record rec = _frames[i];
            Client.Receive(_raw, rec.Offset, rec.Length);
            if (_frameTicks[i] > _fedTick) _fedTick = _frameTicks[i];
        }
    }
}
