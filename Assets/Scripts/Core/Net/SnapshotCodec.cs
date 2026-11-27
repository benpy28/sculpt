using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// One other player as a snapshot carries them: exact integers, so the server's copy of what it sent and the
    /// client's copy of what it received are equal and a delta between them never drifts.
    /// Position 1/1024 m, velocity 1/64 m/s (only used to draw and extrapolate), yaw 1/65536 of a turn, pitch 0.01 degree.
    /// </summary>
    public struct SnapPose
    {
        public int Id, Team;
        public long Px, Py, Pz, Vx, Vy, Vz;
        public int Yaw, Pitch;
        /// <summary>Move mode, crouched, dead, held item and hand mode packed (see <see cref="SnapshotCodec"/>).</summary>
        public int Look;
    }

    /// <summary>
    /// The snapshots one side has sent (server, per client) or received (client), by tick, for delta coding.
    /// A ring: a tick's slot is reused <see cref="Size"/> ticks later. Server and client use the same size, so any
    /// tick the client acked is still in its ring when a delta against it arrives (see <see cref="SnapshotCodec"/>).
    /// </summary>
    public sealed class SnapshotBaselines
    {
        public readonly int Size;
        private readonly int[] _tick;
        private readonly bool[] _hasSelf;
        private readonly byte[][] _self;
        private readonly int[] _selfLen;
        private readonly SnapPose[][] _poses;
        private readonly int[] _count;

        public SnapshotBaselines(int size)
        {
            if (size < 2) throw new ArgumentException("Baseline window must be at least 2 ticks.");
            Size = size;
            _tick = new int[size];
            _hasSelf = new bool[size];
            _self = new byte[size][];
            _selfLen = new int[size];
            _poses = new SnapPose[size][];
            _count = new int[size];
            Clear();
        }

        public void Clear()
        {
            for (int i = 0; i < Size; i++) _tick[i] = int.MinValue;
        }

        private int Slot(int tick) => (int)((uint)tick % (uint)Size);

        public bool Has(int tick) => tick >= 0 && _tick[Slot(tick)] == tick;

        internal void Put(int tick, bool hasSelf, byte[] self, int selfLen, SnapPose[] poses, int count)
        {
            int s = Slot(tick);
            _tick[s] = tick;
            _hasSelf[s] = hasSelf;
            if (_self[s] == null || _self[s].Length < selfLen) _self[s] = new byte[Math.Max(selfLen, 512)];
            Buffer.BlockCopy(self, 0, _self[s], 0, selfLen);
            _selfLen[s] = hasSelf ? selfLen : 0;
            if (_poses[s] == null || _poses[s].Length < count) _poses[s] = new SnapPose[Math.Max(count, 8)];
            Array.Copy(poses, _poses[s], count);
            _count[s] = count;
        }

        internal void Get(int tick, out bool hasSelf, out byte[] self, out int selfLen, out SnapPose[] poses, out int count)
        {
            int s = Slot(tick);
            hasSelf = _hasSelf[s];
            self = _self[s];
            selfLen = _selfLen[s];
            poses = _poses[s];
            count = _count[s];
        }
    }

    /// <summary>
    /// Snapshot wire format (protocol 32): a full snapshot, or a delta against an older snapshot the client said it has
    /// (the ack in its input packets). Deltas only ever reference an acked snapshot, so a lost packet never breaks the
    /// chain: the next one is simply coded against the newest snapshot the client acked.
    /// <code>
    /// U8   MsgType.Snapshot
    /// VarU server tick
    /// VarU base age (0 = full; else the base is tick - age)
    /// U8   flags: 1 has self, 2 self is a delta
    /// self (when present): VarU length, then the bytes raw, or as runs XORed against the base's self bytes
    /// VarU player count, then each player sorted by id: VarU id gap, U8 field mask, the fields that changed
    /// </code>
    /// The receiver's own state (inputs real, lead, the full motor state, bar, hold) is the same byte layout as before
    /// and is XORed byte for byte against the base, so the decoded motor state is bit-exact by construction and any
    /// new MotorState field is delta-coded without touching this file. Other players are quantized integers with
    /// per-field deltas.
    /// </summary>
    public sealed class SnapshotCodec
    {
        public const double PosScale = 1024.0;
        public const double VelScale = 64.0;
        public const double PitchScale = 100.0;
        private const double YawScale = 65536.0 / (2 * Math.PI);
        public const int MaxSelfBytes = 4096;
        private const long MaxCoord = 1L << 40;

        private const byte FHasSelf = 1, FSelfDelta = 2;
        private const byte MPos = 1, MVel = 2, MYaw = 4, MLook = 8, MPitch = 16, MNew = 128;

        private readonly ByteWriter _selfW = new ByteWriter(512);
        private byte[] _selfBuf = new byte[512];
        private SnapPose[] _poses = new SnapPose[16];
        private static readonly IComparer<SnapPose> ById = Comparer<SnapPose>.Create((a, b) => a.Id.CompareTo(b.Id));

        /// <summary>Bytes of the last packet written, split for the stats: the self part and everyone else.</summary>
        public int LastSelfBytes { get; private set; }

        // ---------------------------------------------------------------- write (server)

        /// <summary>
        /// Writes <paramref name="m"/> into <paramref name="w"/>, as a delta against <paramref name="baseTick"/> when
        /// <paramref name="sent"/> still holds it (else full), and records it in <paramref name="sent"/> (when given)
        /// as a base for later snapshots. Returns whether it was a delta.
        /// </summary>
        public bool Write(ByteWriter w, in SnapshotMsg m, SnapshotBaselines sent, int baseTick)
        {
            if (m.ServerTick < 0) throw new ArgumentException("Bad tick.");
            int n = m.Others?.Count ?? 0;
            if (n > Protocol.MaxPlayersPerSnapshot) throw new ArgumentException("Too many players.");

            // This tick's content.
            if (_poses.Length < n) _poses = new SnapPose[Math.Max(n, _poses.Length * 2)];
            for (int i = 0; i < n; i++) _poses[i] = Quantize(m.Others[i]);
            int count = SortUnique(_poses, n);
            return WriteCore(w, m, count, sent, baseTick);
        }

        private SnapPose[] _everyone = new SnapPose[16];
        private int _everyoneCount, _everyoneTick = -1;

        /// <summary>
        /// Server fast path: quantize and sort every player once per tick (<see cref="SetEveryone"/>), then write each
        /// receiver's snapshot with everyone but themselves (<see cref="WriteForReceiver"/>). Same bytes as
        /// <see cref="Write"/> with that list.
        /// </summary>
        public void SetEveryone(int tick, List<PlayerSnap> all)
        {
            int n = all.Count;
            if (_everyone.Length < n) _everyone = new SnapPose[Math.Max(n, _everyone.Length * 2)];
            for (int i = 0; i < n; i++) _everyone[i] = Quantize(all[i]);
            _everyoneCount = SortUnique(_everyone, n);
            _everyoneTick = tick;
        }

        /// <summary>Writes <paramref name="m"/> (its Others ignored) with everyone from <see cref="SetEveryone"/> except <paramref name="receiverId"/>.</summary>
        /// <param name="includeById">Interest management: by player id, who goes in (null = everyone). A player left out
        /// is simply not in this snapshot; the client stops drawing them, and they come back as new (full) when included
        /// again, so leaving someone out never breaks the delta chain.</param>
        public bool WriteForReceiver(ByteWriter w, in SnapshotMsg m, int receiverId, SnapshotBaselines sent, int baseTick, bool[] includeById = null)
        {
            if (_everyoneTick != m.ServerTick) throw new InvalidOperationException("SetEveryone was not called for this tick.");
            if (_poses.Length < _everyoneCount) _poses = new SnapPose[Math.Max(_everyoneCount, _poses.Length * 2)];
            int count = 0;
            int skip = receiverId & 0xFFFF;
            for (int i = 0; i < _everyoneCount; i++)
            {
                int id = _everyone[i].Id;
                if (id == skip) continue;
                if (includeById != null && (id >= includeById.Length || !includeById[id])) continue;
                _poses[count++] = _everyone[i];
            }
            if (count > Protocol.MaxPlayersPerSnapshot) throw new ArgumentException("Too many players.");
            return WriteCore(w, m, count, sent, baseTick);
        }

        private static int SortUnique(SnapPose[] poses, int n)
        {
            Array.Sort(poses, 0, n, ById);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (count > 0 && poses[count - 1].Id == poses[i].Id) continue; // a duplicate id: first wins
                poses[count++] = poses[i];
            }
            return count;
        }

        private bool WriteCore(ByteWriter w, in SnapshotMsg m, int count, SnapshotBaselines sent, int baseTick)
        {
            _selfW.Clear();
            if (m.HasSelf) WriteSelf(_selfW, m);

            bool delta = sent != null && baseTick >= 0 && baseTick < m.ServerTick && m.ServerTick - baseTick < sent.Size && sent.Has(baseTick);
            bool baseHasSelf = false;
            byte[] baseSelf = null;
            int baseSelfLen = 0;
            SnapPose[] basePoses = null;
            int baseCount = 0;
            if (delta) sent.Get(baseTick, out baseHasSelf, out baseSelf, out baseSelfLen, out basePoses, out baseCount);

            w.U8((byte)MsgType.Snapshot);
            w.VarU((ulong)m.ServerTick);
            w.VarU(delta ? (ulong)(m.ServerTick - baseTick) : 0UL);
            bool selfDelta = delta && m.HasSelf && baseHasSelf;
            w.U8((byte)((m.HasSelf ? FHasSelf : 0) | (selfDelta ? FSelfDelta : 0)));
            int start = w.Length;
            if (m.HasSelf)
            {
                if (selfDelta) WriteXor(w, _selfW.Buffer, _selfW.Length, baseSelf, baseSelfLen);
                else { w.VarU((ulong)_selfW.Length); w.Bytes(_selfW.Buffer, 0, _selfW.Length); }
            }
            LastSelfBytes = w.Length - start;

            w.VarU((ulong)count);
            int prevId = -1, bi = 0;
            for (int i = 0; i < count; i++)
            {
                SnapPose q = _poses[i];
                w.VarU((ulong)(q.Id - prevId - 1));
                prevId = q.Id;
                bool found = false;
                if (delta)
                {
                    while (bi < baseCount && basePoses[bi].Id < q.Id) bi++;
                    found = bi < baseCount && basePoses[bi].Id == q.Id && basePoses[bi].Team == q.Team;
                }
                if (!found)
                {
                    w.U8(MNew);
                    w.U8((byte)q.Team);
                    w.VarS(q.Px); w.VarS(q.Py); w.VarS(q.Pz);
                    w.VarS(q.Vx); w.VarS(q.Vy); w.VarS(q.Vz);
                    w.VarU((ulong)q.Yaw); w.VarS(q.Pitch);
                    w.VarU((ulong)q.Look);
                    continue;
                }
                SnapPose b = Extrapolate(basePoses[bi], m.ServerTick - baseTick);
                byte mk = 0;
                if (q.Px != b.Px || q.Py != b.Py || q.Pz != b.Pz) mk |= MPos;
                if (q.Vx != b.Vx || q.Vy != b.Vy || q.Vz != b.Vz) mk |= MVel;
                if (q.Yaw != b.Yaw) mk |= MYaw;
                if (q.Pitch != b.Pitch) mk |= MPitch;
                if (q.Look != b.Look) mk |= MLook;
                w.U8(mk);
                if ((mk & MPos) != 0) { w.VarS(q.Px - b.Px); w.VarS(q.Py - b.Py); w.VarS(q.Pz - b.Pz); }
                if ((mk & MVel) != 0) { w.VarS(q.Vx - b.Vx); w.VarS(q.Vy - b.Vy); w.VarS(q.Vz - b.Vz); }
                if ((mk & MYaw) != 0) w.VarS(WrapYaw(q.Yaw - b.Yaw));
                if ((mk & MPitch) != 0) w.VarS(q.Pitch - b.Pitch);
                if ((mk & MLook) != 0) w.VarU((ulong)q.Look);
            }

            sent?.Put(m.ServerTick, m.HasSelf, _selfW.Buffer, _selfW.Length, _poses, count);
            return delta;
        }

        // ---------------------------------------------------------------- read (client)

        /// <summary>The server tick of a snapshot packet without decoding it (to drop late ones before touching baselines).</summary>
        public static int PeekTick(byte[] data, int offset, int count)
        {
            var r = new ByteReader(data, offset, count);
            if (r.U8() != (byte)MsgType.Snapshot) throw new NetFormatException("Not a snapshot.");
            ulong t = r.VarU();
            if (t > int.MaxValue) throw new NetFormatException("Bad tick.");
            return (int)t;
        }

        /// <summary>
        /// Decodes a snapshot. A delta needs its base in <paramref name="received"/>; returns false (nothing changed)
        /// when it is not there. On success the snapshot is recorded in <paramref name="received"/> (when given), so
        /// only call this for a snapshot you are going to accept. Malformed packets throw NetFormatException.
        /// </summary>
        public bool TryRead(ref ByteReader r, List<PlayerSnap> othersInto, SnapshotBaselines received, out SnapshotMsg m)
        {
            m = default;
            if (r.U8() != (byte)MsgType.Snapshot) throw new NetFormatException("Unexpected message type.");
            ulong t = r.VarU();
            if (t > int.MaxValue) throw new NetFormatException("Bad tick.");
            int tick = (int)t;
            ulong age = r.VarU();
            if (age > (ulong)tick) throw new NetFormatException("Bad base.");
            byte flags = r.U8();
            if ((flags & ~(FHasSelf | FSelfDelta)) != 0 || (flags & (FHasSelf | FSelfDelta)) == FSelfDelta) throw new NetFormatException("Bad snapshot flags.");

            bool delta = age > 0;
            bool baseHasSelf = false;
            byte[] baseSelf = null;
            int baseSelfLen = 0;
            SnapPose[] basePoses = null;
            int baseCount = 0;
            if (delta)
            {
                int baseTick = tick - (int)age;
                if (received == null || !received.Has(baseTick)) return false;
                received.Get(baseTick, out baseHasSelf, out baseSelf, out baseSelfLen, out basePoses, out baseCount);
            }

            m.ServerTick = tick;
            m.HasSelf = (flags & FHasSelf) != 0;
            m.Others = othersInto;
            othersInto.Clear();
            int selfLen = 0;
            if (m.HasSelf)
            {
                selfLen = r.VarCount(MaxSelfBytes);
                if (_selfBuf.Length < selfLen) _selfBuf = new byte[Math.Max(selfLen, _selfBuf.Length * 2)];
                if ((flags & FSelfDelta) != 0)
                {
                    if (!delta || !baseHasSelf) throw new NetFormatException("Self delta without a base.");
                    ReadXor(ref r, _selfBuf, selfLen, baseSelf, baseSelfLen);
                }
                else
                {
                    int at = r.Skip(selfLen);
                    Buffer.BlockCopy(r.BufferOf(), at, _selfBuf, 0, selfLen);
                }
                var sr = new ByteReader(_selfBuf, 0, selfLen);
                ReadSelf(ref sr, ref m);
                if (sr.Remaining != 0) throw new NetFormatException("Self state length mismatch.");
            }

            int count = r.VarCount(Protocol.MaxPlayersPerSnapshot);
            if (_poses.Length < count) _poses = new SnapPose[Math.Max(count, _poses.Length * 2)];
            long prevId = -1;
            int bi = 0;
            for (int i = 0; i < count; i++)
            {
                ulong gap = r.VarU();
                long idl = prevId + 1 + (long)Math.Min(gap, (ulong)int.MaxValue);
                if (idl > ushort.MaxValue) throw new NetFormatException("Bad id.");
                int id = (int)idl;
                prevId = id;
                byte mk = r.U8();
                SnapPose q;
                if ((mk & MNew) != 0)
                {
                    if (mk != MNew) throw new NetFormatException("Bad snapshot mask.");
                    q = new SnapPose { Id = id, Team = r.U8() };
                    q.Px = Coord(r.VarS()); q.Py = Coord(r.VarS()); q.Pz = Coord(r.VarS());
                    q.Vx = Coord(r.VarS()); q.Vy = Coord(r.VarS()); q.Vz = Coord(r.VarS());
                    ulong yaw = r.VarU();
                    if (yaw > 0xFFFF) throw new NetFormatException("Bad yaw.");
                    q.Yaw = (int)yaw;
                    q.Pitch = Pitch(r.VarS());
                    q.Look = LookOf(r.VarU());
                }
                else
                {
                    if ((mk & ~(MPos | MVel | MYaw | MPitch | MLook)) != 0) throw new NetFormatException("Bad snapshot mask.");
                    if (!delta) throw new NetFormatException("Delta player in a full snapshot.");
                    while (bi < baseCount && basePoses[bi].Id < id) bi++;
                    if (bi >= baseCount || basePoses[bi].Id != id) throw new NetFormatException("Delta player missing from the base.");
                    q = Extrapolate(basePoses[bi], (int)age);
                    if ((mk & MPos) != 0) { q.Px = Coord(q.Px + r.VarS()); q.Py = Coord(q.Py + r.VarS()); q.Pz = Coord(q.Pz + r.VarS()); }
                    if ((mk & MVel) != 0) { q.Vx = Coord(q.Vx + r.VarS()); q.Vy = Coord(q.Vy + r.VarS()); q.Vz = Coord(q.Vz + r.VarS()); }
                    if ((mk & MYaw) != 0)
                    {
                        long dy = r.VarS();
                        if (dy < -0x8000 || dy > 0x8000) throw new NetFormatException("Bad yaw.");
                        q.Yaw = (int)((q.Yaw + dy) & 0xFFFF);
                    }
                    if ((mk & MPitch) != 0) q.Pitch = Pitch(q.Pitch + r.VarS());
                    if ((mk & MLook) != 0) q.Look = LookOf(r.VarU());
                }
                _poses[i] = q;
                othersInto.Add(Dequantize(q));
            }

            received?.Put(tick, m.HasSelf, _selfBuf, selfLen, _poses, count);
            return true;
        }

        // ---------------------------------------------------------------- the receiver's own state

        private static void WriteSelf(ByteWriter w, in SnapshotMsg m)
        {
            w.Bool(m.SelfInputWasReal);
            int lead = m.SelfLead < sbyte.MinValue ? sbyte.MinValue : (m.SelfLead > sbyte.MaxValue ? sbyte.MaxValue : m.SelfLead);
            w.I8((sbyte)lead);
            Protocol.WriteMotor(w, m.Self);
            w.U16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, m.SelfBar)));
            w.U16((ushort)(m.SelfHoldTenthMs < 0 || m.SelfHoldTenthMs >= Protocol.NoHold ? Protocol.NoHold : m.SelfHoldTenthMs));
        }

        private static void ReadSelf(ref ByteReader r, ref SnapshotMsg m)
        {
            m.SelfInputWasReal = r.Bool();
            m.SelfLead = r.I8();
            m.Self = Protocol.ReadMotor(ref r);
            m.SelfBar = r.U16();
            m.SelfHoldTenthMs = r.U16();
        }

        /// <summary>
        /// XOR of <paramref name="cur"/> against <paramref name="bas"/> (zeros past its end), as alternating runs:
        /// VarU unchanged bytes, VarU changed bytes followed by those bytes XORed. A changed run only ends at two
        /// unchanged bytes in a row (one costs less inside the run than a new pair of counts).
        /// </summary>
        internal static void WriteXor(ByteWriter w, byte[] cur, int curLen, byte[] bas, int baseLen)
        {
            w.VarU((ulong)curLen);
            int i = 0;
            while (i < curLen)
            {
                int z = i;
                while (z < curLen && X(cur, bas, baseLen, z) == 0) z++;
                w.VarU((ulong)(z - i));
                i = z;
                if (i >= curLen) break;
                int e = i;
                while (e < curLen && !(X(cur, bas, baseLen, e) == 0 && (e + 1 >= curLen || X(cur, bas, baseLen, e + 1) == 0))) e++;
                w.VarU((ulong)(e - i));
                for (int k = i; k < e; k++) w.U8(X(cur, bas, baseLen, k));
                i = e;
            }
        }

        /// <summary>Reverses <see cref="WriteXor"/> after its length (already read) into <paramref name="into"/>.</summary>
        internal static void ReadXor(ref ByteReader r, byte[] into, int len, byte[] bas, int baseLen)
        {
            int i = 0;
            while (i < len)
            {
                int z = r.VarCount(len - i);
                for (int k = i; k < i + z; k++) into[k] = k < baseLen ? bas[k] : (byte)0;
                i += z;
                if (i >= len) break;
                int n = r.VarCount(len - i);
                if (n < 1) throw new NetFormatException("Empty changed run.");
                for (int k = i; k < i + n; k++) into[k] = (byte)(r.U8() ^ (k < baseLen ? bas[k] : 0));
                i += n;
            }
        }

        private static byte X(byte[] cur, byte[] bas, int baseLen, int k) => (byte)(cur[k] ^ (k < baseLen ? bas[k] : 0));

        // ---------------------------------------------------------------- other players

        /// <summary>
        /// The base pose moved on by its own velocity over <paramref name="ageTicks"/>: deltas are against this guess,
        /// so a player running in a straight line costs about a byte per axis however old the ack is. Integer maths
        /// (truncating), so encoder and decoder agree exactly. Only the position is guessed.
        /// </summary>
        internal static SnapPose Extrapolate(SnapPose b, int ageTicks)
        {
            long num = (long)ageTicks * (long)(PosScale / VelScale);
            long den = SimTime.TicksPerSecond;
            b.Px = Clamp(b.Px + b.Vx * num / den);
            b.Py = Clamp(b.Py + b.Vy * num / den);
            b.Pz = Clamp(b.Pz + b.Vz * num / den);
            return b;
        }

        private static long Clamp(long v) => v < -MaxCoord ? -MaxCoord : (v > MaxCoord ? MaxCoord : v);

        public static SnapPose Quantize(in PlayerSnap p)
        {
            double yaw = Math.IEEERemainder(p.Yaw, 2 * Math.PI);
            if (double.IsNaN(yaw)) yaw = 0;
            if (yaw < 0) yaw += 2 * Math.PI;
            return new SnapPose
            {
                Id = p.Id & 0xFFFF,
                Team = p.Team & 0xFF,
                Px = Q(p.Position.X, PosScale), Py = Q(p.Position.Y, PosScale), Pz = Q(p.Position.Z, PosScale),
                Vx = Q(p.Velocity.X, VelScale), Vy = Q(p.Velocity.Y, VelScale), Vz = Q(p.Velocity.Z, VelScale),
                Yaw = (int)Math.Round(yaw * YawScale) & 0xFFFF,
                Pitch = Pitch((long)Math.Round(Clamp(p.Pitch, -180, 180) * PitchScale)),
                Look = ((int)p.Mode & 7) | (p.Crouched ? 8 : 0) | (p.Dead ? 16 : 0) | (((int)p.Held & 15) << 5) | (((int)p.Hand & 7) << 9),
            };
        }

        public static PlayerSnap Dequantize(in SnapPose q) => new PlayerSnap
        {
            Id = q.Id,
            Team = q.Team,
            Position = new Vec3d(q.Px / PosScale, q.Py / PosScale, q.Pz / PosScale),
            Velocity = new Vec3d(q.Vx / VelScale, q.Vy / VelScale, q.Vz / VelScale),
            Yaw = (float)(q.Yaw / YawScale),
            Pitch = (float)(q.Pitch / PitchScale),
            Mode = (MoveMode)(q.Look & 7),
            Crouched = (q.Look & 8) != 0,
            Dead = (q.Look & 16) != 0,
            Held = (Item)((q.Look >> 5) & 15),
            Hand = (HandMode)((q.Look >> 9) & 7),
        };

        private static int LookOf(ulong v)
        {
            if (v >= 1 << 12) throw new NetFormatException("Bad look.");
            int look = (int)v;
            if ((look & 7) > (int)MoveMode.Mantle) throw new NetFormatException("Bad move mode.");
            int held = (look >> 5) & 15;
            if (held > (int)Items.Last || held == (int)Item.Chest) throw new NetFormatException("Bad item.");
            if (((look >> 9) & 7) > (int)HandMode.Disarmed) throw new NetFormatException("Bad hand mode.");
            return look;
        }

        private static long Q(double v, double scale)
        {
            if (double.IsNaN(v)) return 0;
            double s = Math.Round(v * scale);
            return (long)Clamp(s, -MaxCoord, MaxCoord);
        }

        private static long Coord(long v)
        {
            if (v < -MaxCoord || v > MaxCoord) throw new NetFormatException("Coordinate out of range.");
            return v;
        }

        private static int Pitch(long v)
        {
            if (v < -18000 || v > 18000) throw new NetFormatException("Bad pitch.");
            return (int)v;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static long WrapYaw(int d)
        {
            d &= 0xFFFF;
            return d >= 0x8000 ? d - 0x10000 : d;
        }
    }
}
