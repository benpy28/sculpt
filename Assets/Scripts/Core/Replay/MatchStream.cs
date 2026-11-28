using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>Replay and spectator stream settings. Every "ticks" figure here is in real server ticks (120 Hz).</summary>
    public sealed class ReplayConfig
    {
        /// <summary>A key frame (everything a late joiner needs) starts every segment; seeking jumps to one. 10 s.</summary>
        public int KeyEveryTicks = 10 * SimTime.TicksPerSecond;
        /// <summary>Spectator snapshots every N ticks (2 = 60 a second, interpolated in between).</summary>
        public int SnapEveryTicks = 2;
        /// <summary>A hash of every player's exact state every N ticks, so a re-run of the inputs can prove the match.</summary>
        public int HashEveryTicks = SimTime.TicksPerSecond / 4;
        /// <summary>Keep every player's simulated command for every tick (for review and the re-run check).</summary>
        public bool RecordInputs = true;
    }

    /// <summary>A moment worth jumping to (kills for now).</summary>
    public struct ReplayMarker
    {
        public int Tick;
        public int Killer;
        public int Victim;
        public DamageSource Source;
        public bool Headshot;
    }

    /// <summary>How a player came into the server (the re-run adds them back the same way).</summary>
    public enum JoinKind : byte { Human = 0, Bot = 1, Dummy = 2 }

    /// <summary>
    /// Ten seconds (by default) of the stream: a key frame with the full state at the end of tick FirstTick - 1, then one
    /// frame per tick that had anything for spectators. Raw holds the same thing in the file's record layout, plus the
    /// inputs, state hashes and joins/leaves of these ticks.
    /// </summary>
    public sealed class StreamSegment
    {
        /// <summary>Absolute number of this segment in the match (0 = the first).</summary>
        public int Index;
        public int FirstTick;
        public int LastTick;
        public byte[] Key;
        public readonly List<KeyValuePair<int, byte[]>> Frames = new List<KeyValuePair<int, byte[]>>();
        public readonly ByteWriter Raw = new ByteWriter(4096);
        public bool Closed;
    }

    /// <summary>Record kinds inside a segment's raw bytes (each is: kind byte, varint length, body). Unknown kinds are skipped.</summary>
    public static class ReplayRecord
    {
        public const byte Key = 1, Frame = 2, Inputs = 3, Hash = 4, Join = 5, Leave = 6, Flag = 7, Locker = 8, Prefs = 9, BrSeed = 10;
    }

    /// <summary>
    /// The server's spectator stream of one match, which is also its replay: a tap on everything the server tells
    /// everyone, plus a spectator snapshot of every player. Live spectators read it a delay behind; the replay writer
    /// saves each segment as it closes. Costs nothing while the server has none (NetServer.Stream is null).
    /// The stream only reads the simulation; it never changes it.
    /// </summary>
    public sealed class MatchStream
    {
        public readonly ReplayConfig Config;
        /// <summary>Segments still in memory, oldest first (live spectators and the writer trim them).</summary>
        public readonly List<StreamSegment> Segments = new List<StreamSegment>();
        public readonly List<ReplayMarker> Markers = new List<ReplayMarker>();
        /// <summary>Anti-cheat flags the server logged while recording (also written as records, for a reviewer to jump to).</summary>
        public readonly List<AntiCheat.Flag> Flags = new List<AntiCheat.Flag>();
        /// <summary>Raised when a segment is complete (the writer saves it).</summary>
        public event Action<StreamSegment> SegmentClosed;
        public int FirstTick { get; private set; } = int.MinValue;
        public int LastTick { get; private set; } = int.MinValue;

        private readonly SpecSnapCodec _codec = new SpecSnapCodec();
        private readonly InputLogCodec _inputLog = new InputLogCodec();
        private readonly List<byte[]> _packets = new List<byte[]>();
        private readonly List<SpecPlayer> _players = new List<SpecPlayer>();
        private readonly ByteWriter _inputs = new ByteWriter(1024);
        private int _inputCount;
        private int _tick = int.MinValue;
        private StreamSegment _open;
        private int _nextIndex;
        private readonly ByteWriter _rec = new ByteWriter(256);

        public MatchStream(ReplayConfig config = null)
        {
            Config = config ?? new ReplayConfig();
            if (Config.KeyEveryTicks < 1 || Config.SnapEveryTicks < 1 || Config.HashEveryTicks < 1) throw new ArgumentException("Replay intervals must be positive.");
        }

        public StreamSegment Open => _open;

        // ---------------------------------------------------------------- Server hooks

        /// <summary>Starts the first segment: its key frame is the server as it is now, between ticks.</summary>
        internal void Attach(NetServer s)
        {
            if (_open != null || _finished) throw new InvalidOperationException("Stream already started.");
            StartSegment(s, s.Tick + 1);
            if (s.Br != null) OnBrSeed(s.Tick, s.Br.Seed);
        }

        internal void BeginTick(int tick)
        {
            _tick = tick;
            _packets.Clear();
            _inputs.Clear();
            _inputCount = 0;
        }

        internal bool InTick => _tick != int.MinValue;

        /// <summary>A packet the server sent everyone this tick (or the spectators-only combat message).</summary>
        internal void AddPacket(byte[] data)
        {
            if (!InTick) return; // outside Step (a join): the key frame covers it
            _packets.Add(data);
        }

        internal void AddInput(int playerId, in InputCmd cmd)
        {
            if (!Config.RecordInputs || !InTick) return;
            _inputLog.Write(_inputs, playerId, cmd);
            _inputCount++;
        }

        internal void AddMarker(in ReplayMarker m) => Markers.Add(m);

        /// <summary>A flag the server logged (during a tick, or between ticks for packet checks): recorded with the next tick.</summary>
        internal void AddFlag(in AntiCheat.Flag f)
        {
            if (_finished) return;
            Flags.Add(f);
            _rec.Clear();
            AntiCheat.FlagLog.Write(_rec, f);
            _pending.Add(Bytes(ReplayRecord.Flag, _rec));
        }

        /// <summary>A player came in between ticks (after tick `tick`).</summary>
        internal void OnJoin(int tick, ServerPlayer p, JoinKind kind, bool countsAsPlayer)
        {
            _rec.Clear();
            _rec.VarU((ulong)Math.Max(0, tick));
            _rec.VarU((ulong)p.Id);
            _rec.U8((byte)kind);
            _rec.Bool(countsAsPlayer);
            _rec.U8((byte)p.Team);
            _rec.Vec(p.Spawn);
            _rec.F64(p.SpawnYaw);
            _pending.Add(Bytes(ReplayRecord.Join, _rec));
        }

        /// <summary>A player's locker gun was set between ticks (after tick `tick`): the re-run gives them the same loadout.</summary>
        internal void OnLocker(int tick, int playerId, Item gun)
        {
            _rec.Clear();
            _rec.VarU((ulong)Math.Max(0, tick));
            _rec.VarU((ulong)playerId);
            _rec.U8((byte)gun);
            _pending.Add(Bytes(ReplayRecord.Locker, _rec));
        }

        /// <summary>A player's slot auto-sort changed between ticks (after tick `tick`): the re-run picks up into the same slots.</summary>
        internal void OnPrefs(int tick, int playerId, in SlotPrefs prefs)
        {
            _rec.Clear();
            _rec.VarU((ulong)Math.Max(0, tick));
            _rec.VarU((ulong)playerId);
            for (int k = 0; k < Items.Slots; k++) _rec.U8((byte)prefs[k]);
            _pending.Add(Bytes(ReplayRecord.Prefs, _rec));
        }

        /// <summary>The battle royale's seed (team deal, chest rolls, circles): a re-run of the inputs needs the same one.</summary>
        internal void OnBrSeed(int tick, int seed)
        {
            _rec.Clear();
            _rec.VarU((ulong)Math.Max(0, tick));
            _rec.I32(seed);
            _pending.Add(Bytes(ReplayRecord.BrSeed, _rec));
        }

        internal void OnLeave(int tick, int playerId)
        {
            _rec.Clear();
            _rec.VarU((ulong)Math.Max(0, tick));
            _rec.VarU((ulong)playerId);
            _pending.Add(Bytes(ReplayRecord.Leave, _rec));
        }

        /// <summary>Join/leave records waiting for the next segment write (they happen between ticks).</summary>
        private readonly List<byte[]> _pending = new List<byte[]>();

        private static byte[] Bytes(byte kind, ByteWriter body)
        {
            var w = new ByteWriter(body.Length + 6);
            w.U8(kind);
            w.VarU((ulong)body.Length);
            w.Bytes(body.Buffer, 0, body.Length);
            return w.ToArray();
        }

        /// <summary>End of server tick: this tick's frame, hash and inputs; a new segment (with a key frame) when due.</summary>
        internal void EndTick(NetServer s)
        {
            int tick = _tick;
            _tick = int.MinValue;
            StreamSegment seg = _open;
            if (seg == null) return; // finished
            foreach (byte[] j in _pending) seg.Raw.Bytes(j, 0, j.Length);
            _pending.Clear();

            if (_inputCount > 0)
            {
                _rec.Clear();
                _rec.VarU((ulong)tick);
                _rec.VarU((ulong)_inputCount);
                _rec.Bytes(_inputs.Buffer, 0, _inputs.Length);
                Append(seg, ReplayRecord.Inputs, _rec.Buffer, _rec.Length);
            }
            if (tick % Config.SnapEveryTicks == 0) _packets.Add(_codec.Encode(tick, false, Collect(s)));
            if (_packets.Count > 0)
            {
                byte[] frame = SpecProtocol.WriteFrame(tick, false, _packets);
                seg.Frames.Add(new KeyValuePair<int, byte[]>(tick, frame));
                Append(seg, ReplayRecord.Frame, frame, frame.Length);
            }
            if (tick % Config.HashEveryTicks == 0)
            {
                _rec.Clear();
                _rec.VarU((ulong)tick);
                _rec.I64(unchecked((long)StateHash(s)));
                Append(seg, ReplayRecord.Hash, _rec.Buffer, _rec.Length);
            }
            seg.LastTick = tick;
            LastTick = tick;
            if (tick + 1 - seg.FirstTick >= Config.KeyEveryTicks)
            {
                Close(seg);
                StartSegment(s, tick + 1);
            }
        }

        private static void Append(StreamSegment seg, byte kind, byte[] body, int count)
        {
            seg.Raw.U8(kind);
            seg.Raw.VarU((ulong)count);
            seg.Raw.Bytes(body, 0, count);
        }

        /// <summary>A segment starting at `first`: its key frame is the server's state now (end of tick first - 1).</summary>
        private void StartSegment(NetServer s, int first)
        {
            var seg = new StreamSegment { Index = _nextIndex++, FirstTick = first, LastTick = first - 1 };
            _inputLog.Reset(); // each segment's inputs decode on their own
            var packets = new List<byte[]>();
            s.WriteSpectatorJoin(packets);
            packets.Add(_codec.Encode(first - 1, true, Collect(s)));
            seg.Key = SpecProtocol.WriteFrame(first - 1, true, packets);
            Append(seg, ReplayRecord.Key, seg.Key, seg.Key.Length);
            if (FirstTick == int.MinValue) FirstTick = first;
            Segments.Add(seg);
            _open = seg;
        }

        private void Close(StreamSegment seg)
        {
            if (seg.Closed) return;
            seg.Closed = true;
            SegmentClosed?.Invoke(seg);
        }

        /// <summary>Closes the open segment (end of match or recording). The stream can't continue after this.</summary>
        public void Finish()
        {
            if (_open == null) return;
            foreach (byte[] j in _pending) _open.Raw.Bytes(j, 0, j.Length);
            _pending.Clear();
            Close(_open);
            _open = null;
            _finished = true;
        }

        private bool _finished;
        public bool Finished => _finished;

        /// <summary>Forgets closed segments that end before `tick` (always keeps the open one).</summary>
        public void TrimBefore(int tick)
        {
            int n = 0;
            while (n < Segments.Count && Segments[n].Closed && Segments[n].LastTick < tick && Segments[n] != _open) n++;
            if (n > 0) Segments.RemoveRange(0, n);
        }

        // ---------------------------------------------------------------- State

        private List<SpecPlayer> Collect(NetServer s)
        {
            _players.Clear();
            WeaponsConfig wc = s.Movement.Weapons;
            foreach (ServerPlayer p in s.Players)
            {
                MotorState st = p.State;
                bool gun = st.Hand.HasGun;
                _players.Add(new SpecPlayer
                {
                    Id = p.Id,
                    Team = p.Team,
                    Bot = NetServer.IsBotConnection(p.ConnectionId),
                    Position = st.Position,
                    Velocity = st.Velocity,
                    Yaw = (float)p.LastCmd.Move.Yaw,
                    Pitch = p.LastCmd.Pitch,
                    Mode = st.Mode,
                    Crouched = st.Crouched,
                    Dead = st.Dead,
                    Sprinting = st.Sprinting,
                    Reloading = st.Weapon.Reloading,
                    Held = st.Hand.HeldItem,
                    Hand = st.Hand.Mode,
                    Slot = st.Hand.Slot,
                    Health = Math.Max(0, st.Health(wc)),
                    Bar = s.BarOf(p.Id).Value,
                    Ammo = gun ? st.Weapon.AmmoOf(Items.GunOf(st.Hand.HeldItem), wc) : 0,
                    Bag = st.Hand.Bag,
                });
            }
            return _players;
        }

        [ThreadStatic] private static ByteWriter _hashW;

        /// <summary>
        /// FNV-1a over every player's exact simulated state (id order as the server keeps them: motor state, scores,
        /// kill credit, sculpt bar), the match (phase, winner, storm), every ground item and every wall tile (with each
        /// cell's health once it has changed; untouched tiles are skipped). The same inputs on the same build must give the same hash on every tick.
        /// </summary>
        public static ulong StateHash(NetServer s)
        {
            var w = _hashW ??= new ByteWriter(4096);
            w.Clear();
            w.I32(s.Tick);
            foreach (ServerPlayer p in s.Players)
            {
                w.I32(p.Id);
                w.I32(p.Team);
                Protocol.WriteMotor(w, p.State);
                w.I32(p.Kills); w.I32(p.Deaths); w.I32(p.MatchKills); w.I32(p.MatchDeaths);
                w.I32(p.LastHurtBy); w.I32(p.LastHurtTick);
                SculptBar bar = s.BarOf(p.Id);
                w.I32(bar.Value); w.I32(bar.LastSpendTick);
            }
            MatchState m = s.Match;
            w.U8((byte)m.Phase); w.I32(m.PhaseStartTick); w.I32(m.PhaseEndTick);
            w.I32(m.WinnerTeam); w.I32(m.AliveTeams); w.I32(m.AlivePlayers);
            StormState st = m.Storm;
            w.I32(st.Phase); w.I32(st.ShrinkStart); w.I32(st.ShrinkEnd); w.I32(st.Dps);
            w.F64(st.FromX); w.F64(st.FromZ); w.F64(st.FromRadius); w.F64(st.ToX); w.F64(st.ToZ); w.F64(st.ToRadius);
            w.I32(s.Items.NextId);
            foreach (GroundItem g in s.Items.All)
            {
                w.I32(g.Id); w.U8((byte)g.Kind); w.I32(g.Count); w.I32(g.AmmoUsed); w.U8((byte)g.Rarity);
                w.Vec(g.Position); w.I32(g.DroppedBy); w.I32(g.DroppedTick);
            }
            ulong h = 14695981039346656037UL;
            byte[] b = w.Buffer;
            for (int i = 0; i < w.Length; i++) { h ^= b[i]; h *= 1099511628211UL; }
            // Wall tiles: only those play has ever changed (the rest are exactly as built, the same in every run of the
            // same map), each hashed with its index and added up, so the order they first changed in doesn't matter.
            // The 1,306 m map has about 260 thousand tiles: walking them all 4 times a second cost ~8 ms in Mono.
            TileWalls walls = s.Walls;
            if (walls != null)
            {
                ulong sum = 0;
                foreach (int i in walls.Touched)
                {
                    WallTile t = walls[i];
                    ulong th = 14695981039346656037UL ^ (ulong)(uint)i;
                    th *= 1099511628211UL;
                    th ^= (ulong)(uint)((int)t.State | (t.Health << 4)); th *= 1099511628211UL;
                    th ^= t.Open; th *= 1099511628211UL;
                    th ^= t.Patch ^ (t.Steel << 1); th *= 1099511628211UL;
                    if (t.State != TileState.Broken && walls.HasCells(i))
                        for (int c = 0; c < Brush.CellCount; c++) { th ^= (ulong)walls.CellHealth(i, c); th *= 1099511628211UL; }
                    th ^= th >> 29; th *= 0xBF58476D1CE4E5B9UL; th ^= th >> 32;
                    sum += th;
                }
                h ^= sum; h *= 1099511628211UL;
                h ^= (ulong)walls.Touched.Count; h *= 1099511628211UL;
            }
            return h;
        }
    }
}
