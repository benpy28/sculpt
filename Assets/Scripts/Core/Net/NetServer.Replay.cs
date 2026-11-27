using System.Collections.Generic;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// The server side of replays and spectating: the match stream (a tap on what everyone is told, plus spectator
    /// snapshots) and live spectators, who watch it a delay behind so they can't call positions to a player.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>The spectator stream, also the replay being recorded. Null until started (then everything costs nothing extra).</summary>
        public MatchStream Stream { get; private set; }

        /// <summary>
        /// How far behind live spectators watch, in ticks. Default 2 minutes (Ben's home screen roadmap: spectate friends
        /// live on a 2-minute delay). Set before the first spectator joins.
        /// </summary>
        public int SpectatorDelayTicks = 120 * SimTime.TicksPerSecond;

        /// <summary>
        /// The smallest live spectator delay a non-<see cref="Dedicated"/> (listen/home) host may use, in ticks. The live
        /// spectator stream is the full state of every player with no interest masking, so a near-zero delay on a listen
        /// server would let a friend call out live positions (a ghosting wallhack). A modified build could skip the
        /// command-line clamp, so the floor is enforced here, where the delay is actually consumed. Default 30 s.
        /// </summary>
        public int MinSpectatorDelayTicks = 30 * SimTime.TicksPerSecond;

        /// <summary>A trusted dedicated server (ranked/official) sets its own spectator policy; the floor does not apply.</summary>
        public bool Dedicated = false;

        /// <summary>The delay actually used for spectators: the host's value, floored on a non-dedicated host.</summary>
        public int EffectiveSpectatorDelayTicks =>
            Dedicated ? SpectatorDelayTicks : (SpectatorDelayTicks > MinSpectatorDelayTicks ? SpectatorDelayTicks : MinSpectatorDelayTicks);

        private readonly List<Spectator> _spectators = new List<Spectator>();
        private readonly List<CombatEvent> _combatAll = new List<CombatEvent>();

        private sealed class Spectator
        {
            public int ConnectionId;
            /// <summary>Absolute index of the segment being sent, and the next frame in it (-1: its key frame is next).</summary>
            public int Segment = -1;
            public int Frame = -1;
        }

        public IReadOnlyList<int> SpectatorConnections
        {
            get
            {
                var l = new List<int>(_spectators.Count);
                foreach (Spectator s in _spectators) l.Add(s.ConnectionId);
                return l;
            }
        }

        /// <summary>Starts the stream now (between ticks). Start it before players join to record the whole match.</summary>
        public MatchStream StartStream(ReplayConfig config = null)
        {
            if (Stream != null && !Stream.Finished) return Stream;
            Stream = new MatchStream(config);
            Stream.Attach(this);
            return Stream;
        }

        /// <summary>Ends the stream (the last segment closes, so a writer can finish the file). Spectators are dropped from it.</summary>
        public void StopStream()
        {
            Stream?.Finish();
            Stream = null;
            _spectators.Clear();
        }

        public bool IsSpectator(int connectionId)
        {
            foreach (Spectator s in _spectators) if (s.ConnectionId == connectionId) return true;
            return false;
        }

        /// <summary>
        /// A connection that watches instead of playing. Gets a welcome now (player id 0) and the match from
        /// <see cref="SpectatorDelayTicks"/> ago once that much has been recorded. Starts the stream if needed.
        /// </summary>
        public void AddSpectator(int connectionId)
        {
            if (_byConn.ContainsKey(connectionId) || IsSpectator(connectionId)) return;
            if (Stream == null) StartStream();
            _spectators.Add(new Spectator { ConnectionId = connectionId });
            Send(connectionId, SpectatorWelcome(EffectiveSpectatorDelayTicks), true);
        }

        public void RemoveSpectator(int connectionId)
        {
            for (int i = 0; i < _spectators.Count; i++)
                if (_spectators[i].ConnectionId == connectionId) { _spectators.RemoveAt(i); return; }
        }

        /// <summary>Tells a connection from another build which version this server runs (its client shows the mismatch).</summary>
        public void SendVersion(int connectionId)
        {
            _w.Clear();
            Protocol.Write(_w, new WelcomeMsg { ProtocolVersion = Protocol.Version, PlayerId = 0, ServerTick = Tick });
            Send(connectionId, _w.ToArray(), true);
        }

        private byte[] SpectatorWelcome(int delay)
        {
            var w = new ByteWriter(64);
            Protocol.Write(w, new WelcomeMsg { ProtocolVersion = Protocol.Version, PlayerId = 0, Team = 0, ServerTick = Tick, SpectatorDelayTicks = delay });
            return w.ToArray();
        }

        /// <summary>Everything a spectator joining now needs, as packets (a key frame): who they are, match, sculpted pieces, tiles, items.</summary>
        internal void WriteSpectatorJoin(List<byte[]> into)
        {
            into.Add(SpectatorWelcome(EffectiveSpectatorDelayTicks));
            into.Add(MatchBytes());
            if (Sculpt != null) into.Add(JoinStateBytes());
            byte[] tiles = TileJoinBytes();
            if (tiles != null) into.Add(tiles);
            var w = new ByteWriter(16 + Items.Count * 24);
            ItemsProtocol.Write(w, new ItemsMsg { Tick = Tick, Full = true, Items = new List<GroundItem>(Items.All) });
            into.Add(w.ToArray());
        }

        /// <summary>A message for every player, also tapped into the stream.</summary>
        private void Broadcast(byte[] data)
        {
            foreach (ServerPlayer to in _players) Send(to.ConnectionId, data, true);
            Stream?.AddPacket(data);
        }

        /// <summary>Spectators see every shot (players skip their own) and everyone's health after a hit; kills become markers.</summary>
        private void StreamCombat()
        {
            _combatAll.Clear();
            foreach (CombatEvent e in _combat)
            {
                if (_combatAll.Count < CombatProtocol.MaxEvents) _combatAll.Add(e);
                if (e.Type == CombatEventType.Kill)
                    Stream.AddMarker(new ReplayMarker { Tick = Tick, Killer = e.Attacker, Victim = e.Victim, Source = e.Source, Headshot = e.HeadPellets > 0 });
            }
            var w = new ByteWriter(64 + _combatAll.Count * 32);
            CombatProtocol.Write(w, new CombatMsg { Tick = Tick, Events = _combatAll });
            Stream.AddPacket(w.ToArray());
        }

        /// <summary>End of tick: each spectator gets every frame up to now minus the delay (a key frame first).</summary>
        private void StepSpectators()
        {
            MatchStream m = Stream;
            int upTo = Tick - EffectiveSpectatorDelayTicks;
            List<StreamSegment> segs = m.Segments;
            int keepFrom = int.MaxValue;
            foreach (Spectator s in _spectators)
            {
                if (s.Segment < 0)
                {
                    // Start at the newest key frame at or before the delayed tick (nothing yet if the match is younger).
                    for (int k = segs.Count - 1; k >= 0; k--)
                        if (segs[k].FirstTick - 1 <= upTo) { s.Segment = segs[k].Index; s.Frame = -1; break; }
                    if (s.Segment < 0) { keepFrom = 0; continue; }
                }
                while (true)
                {
                    StreamSegment seg = FindSegment(segs, s.Segment);
                    if (seg == null) { s.Segment = -1; break; } // fell behind what is kept (should not happen): rejoin at a key
                    if (s.Frame < 0)
                    {
                        if (seg.FirstTick - 1 > upTo) break;
                        Send(s.ConnectionId, seg.Key, true);
                        s.Frame = 0;
                    }
                    while (s.Frame < seg.Frames.Count && seg.Frames[s.Frame].Key <= upTo)
                    {
                        Send(s.ConnectionId, seg.Frames[s.Frame].Value, true);
                        s.Frame++;
                    }
                    if (s.Frame < seg.Frames.Count || !seg.Closed) break;
                    // Next segment: its key frame only carries the absolute snapshot a running spectator needs.
                    StreamSegment next = FindSegment(segs, s.Segment + 1);
                    if (next == null || next.FirstTick - 1 > upTo) break;
                    s.Segment++;
                    s.Frame = -1;
                }
                if (s.Segment >= 0)
                {
                    StreamSegment at = FindSegment(segs, s.Segment);
                    if (at != null && at.FirstTick < keepFrom) keepFrom = at.FirstTick;
                }
            }
            // Keep what a spectator joining now would need, and whatever someone is still reading. The writer has
            // already saved closed segments.
            int newestKeyNeeded = upTo;
            for (int k = segs.Count - 1; k >= 0; k--)
                if (segs[k].FirstTick - 1 <= upTo) { newestKeyNeeded = segs[k].FirstTick; break; }
            m.TrimBefore(System.Math.Min(keepFrom, newestKeyNeeded));
        }

        private static StreamSegment FindSegment(List<StreamSegment> segs, int index)
        {
            if (segs.Count == 0) return null;
            int k = index - segs[0].Index;
            return k >= 0 && k < segs.Count ? segs[k] : null;
        }
    }
}
