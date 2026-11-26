using System;
using System.Collections.Generic;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Spectator side of the client: a welcome with player id 0 makes this client a watcher. It predicts nothing and
    /// sends nothing; it gets spectator frames (live a delay behind, or fed from a replay file) and draws every player
    /// as a remote. Everything else (tiles, items, match, combat, sculpted pieces) goes through the usual paths, so the
    /// Game layer draws a watched match exactly like a played one.
    /// </summary>
    public sealed partial class NetClient
    {
        /// <summary>This client watches (live or a replay) instead of playing.</summary>
        public bool IsSpectator { get; private set; }
        /// <summary>Joined as a player (has a predicted local player). Views of the local player check this.</summary>
        public bool Playing => Joined && !IsSpectator;
        /// <summary>Live spectating: how far behind the match this client watches (from the server's welcome).</summary>
        public int SpectatorDelayTicks { get; private set; }
        /// <summary>
        /// Replay seeks: while set, packets update the state but make no effects (combat events are dropped and tile
        /// changes arrive as quiet server changes), so a jump doesn't replay every shot in between.
        /// </summary>
        public bool Muted;
        /// <summary>Replays: the caller sets the time (<see cref="SetSpectatorTime"/>); frames don't move the clock.</summary>
        public bool ExternalClock;
        /// <summary>Tick of the newest spectator frame applied (int.MinValue before any).</summary>
        public int SpectatorFrameTick { get; private set; } = int.MinValue;
        /// <summary>True once a key frame has been applied (the client has the whole state).</summary>
        public bool SpectatorSynced { get; private set; }
        public int SpectatorFrames { get; private set; }

        private readonly SpecSnapCodec _specCodec = new SpecSnapCodec();
        private readonly List<SpecPlayer> _specScratch = new List<SpecPlayer>();
        private readonly Dictionary<int, SpecPlayer> _specInfo = new Dictionary<int, SpecPlayer>();
        private readonly List<int> _specIds = new List<int>();

        private void ResetSpectator()
        {
            _specCodec.Reset();
            _specInfo.Clear();
            _specIds.Clear();
            SpectatorSynced = false;
            SpectatorFrameTick = int.MinValue;
        }

        /// <summary>Replays: draw remote players at this server tick (fractional).</summary>
        public void SetSpectatorTime(double tick)
        {
            _serverTime = tick + Config.InterpDelayTicks;
            _serverTimeValid = true;
            Tick = (int)Math.Floor(tick);
        }

        /// <summary>Every player in the newest spectator snapshot, sorted by id.</summary>
        public IReadOnlyList<int> SpectatedIds => _specIds;

        /// <summary>The newest spectator snapshot of a player (HUD in POV: health, ammo, bar, bag).</summary>
        public bool TryGetSpectated(int id, out SpecPlayer p) => _specInfo.TryGetValue(id, out p);

        private void OnSpecFrame(byte[] data, int offset, int count)
        {
            var r = new ByteReader(data, offset, count);
            SpecProtocol.FrameHeader h = SpecProtocol.ReadFrameHeader(ref r);
            if (!h.Key && !SpectatorSynced) return; // joined mid-run: wait for a key frame
            // A running spectator already has everything a key frame carries except its absolute snapshot.
            bool full = !h.Key || !SpectatorSynced;
            for (int i = 0; i < h.Count; i++)
            {
                SpecProtocol.NextPacket(ref r, out int po, out int pl);
                MsgType t = Protocol.PeekType(data, po, pl);
                if (t == MsgType.SpecFrame) throw new NetFormatException("Frame inside a frame.");
                if (!full && t != MsgType.SpecSnap) continue;
                Receive(data, po, pl);
                if (t == MsgType.Welcome && !Joined) return; // another build's replay or server
            }
            if (h.Key) SpectatorSynced = true;
            if (h.Tick > SpectatorFrameTick) SpectatorFrameTick = h.Tick;
            SpectatorFrames++;
            if (!ExternalClock)
            {
                // Live: the clock follows the frames (they come a delay behind the match, at the server's pace).
                double err = h.Tick - _serverTime;
                if (!_serverTimeValid || Math.Abs(err) > 10) _serverTime = h.Tick;
                else _serverTime += err * (err > 0 ? 0.1 : 0.02);
                _serverTimeValid = true;
                Tick = (int)Math.Floor(_serverTime);
            }
        }

        private void OnSpecSnap(ref ByteReader r)
        {
            int tick = _specCodec.Decode(ref r, _specScratch, out _);
            LastSnapshotTick = tick;
            SnapshotsReceived++;
            foreach (var kv in _remotes) kv.Value.SeenThisSnapshot = false;
            _specInfo.Clear();
            _specIds.Clear();
            foreach (SpecPlayer p in _specScratch)
            {
                _specInfo[p.Id] = p;
                _specIds.Add(p.Id);
                if (!_remotes.TryGetValue(p.Id, out var track))
                {
                    track = new RemoteTrack();
                    _remotes[p.Id] = track;
                }
                track.Add(tick, p.ToSnap());
                track.SeenThisSnapshot = true;
            }
            _removeScratch.Clear();
            foreach (var kv in _remotes) if (!kv.Value.SeenThisSnapshot) _removeScratch.Add(kv.Key);
            foreach (int id in _removeScratch) _remotes.Remove(id);
        }

        /// <summary>Tells the server whether this connection plays or watches (first packet after connecting).</summary>
        public void SendHello(bool spectate) => _send(SpecProtocol.Hello(spectate), true);
    }
}
