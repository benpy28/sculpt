using System;
using System.Collections.Generic;
using System.Text;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>What a spectator (live or in a replay) knows about one player: enough to draw them and to show their HUD in POV.</summary>
    public struct SpecPlayer
    {
        public int Id;
        public int Team;
        /// <summary>A server-side bot or practice dummy (no connection).</summary>
        public bool Bot;
        public Vec3d Position;
        public Vec3d Velocity;
        /// <summary>Radians.</summary>
        public float Yaw;
        /// <summary>Degrees, positive looks down.</summary>
        public float Pitch;
        public MoveMode Mode;
        public bool Crouched, Dead, Sprinting, Reloading;
        public Item Held;
        public HandMode Hand;
        public byte Slot;
        public int Health;
        /// <summary>Sculpt/heal bar, basis points.</summary>
        public int Bar;
        /// <summary>Rounds in the held gun's magazine (0 when the held item is not a gun).</summary>
        public int Ammo;
        public Bag Bag;

        public PlayerSnap ToSnap() => new PlayerSnap
        {
            Id = Id, Team = Team, Position = Position, Velocity = Velocity, Yaw = Yaw, Pitch = Pitch,
            Mode = Mode, Crouched = Crouched, Dead = Dead, Held = Held, Hand = Hand,
        };
    }

    /// <summary>
    /// Spectator and replay wire formats (protocol 30). Three messages:
    /// <list type="bullet">
    /// <item>Hello (client to server, reliable, first packet after connecting): protocol version and whether to play or watch.</item>
    /// <item>Frame (server to spectator, reliable, and the unit a replay file stores): one server tick's packets for
    /// spectators, bundled so they arrive together and in order. A key frame carries the whole state a late joiner needs.</item>
    /// <item>Spec snapshot (only inside frames): every player, delta-coded against the previous one in the same run of
    /// frames (key frames are absolute).</item>
    /// </list>
    /// </summary>
    public static class SpecProtocol
    {
        public const byte FrameKey = 1;
        public const int MaxPacketsPerFrame = 255;
        public const int MaxPacketBytes = 1 << 22;

        // ---------------------------------------------------------------- Hello

        public static byte[] Hello(bool spectate)
        {
            var w = new ByteWriter(8);
            w.U8((byte)MsgType.Hello);
            w.U8(Protocol.Version);
            w.U8((byte)(spectate ? 1 : 0));
            return w.ToArray();
        }

        /// <summary>False if this is not a hello. A hello from another build still reads (version tells).</summary>
        public static bool TryReadHello(byte[] data, int offset, int count, out byte version, out bool spectate)
        {
            version = 0;
            spectate = false;
            if (data == null || count < 3 || data[offset] != (byte)MsgType.Hello) return false;
            version = data[offset + 1];
            spectate = (data[offset + 2] & 1) != 0;
            return true;
        }

        // ---------------------------------------------------------------- Frame

        public struct FrameHeader
        {
            public int Tick;
            public bool Key;
            public int Count;
        }

        /// <summary>Writes a frame: one tick's packets (key frames: the late-join state, ending in an absolute snapshot).</summary>
        public static byte[] WriteFrame(int tick, bool key, List<byte[]> packets)
        {
            if (packets.Count > MaxPacketsPerFrame) throw new ArgumentException("Too many packets in one frame.");
            int size = 8;
            foreach (byte[] p in packets) size += p.Length + 4;
            var w = new ByteWriter(size);
            w.U8((byte)MsgType.SpecFrame);
            w.VarU((ulong)tick);
            w.U8(key ? FrameKey : (byte)0);
            w.U8((byte)packets.Count);
            foreach (byte[] p in packets)
            {
                w.VarU((ulong)p.Length);
                w.Bytes(p, 0, p.Length);
            }
            return w.ToArray();
        }

        /// <summary>Reads a frame header; the reader is left at the first packet (read each with <see cref="NextPacket"/>).</summary>
        public static FrameHeader ReadFrameHeader(ref ByteReader r)
        {
            if (r.U8() != (byte)MsgType.SpecFrame) throw new NetFormatException("Not a spectator frame.");
            ulong t = r.VarU();
            if (t > int.MaxValue) throw new NetFormatException("Bad tick.");
            byte flags = r.U8();
            if ((flags & ~FrameKey) != 0) throw new NetFormatException("Bad frame flags.");
            return new FrameHeader { Tick = (int)t, Key = (flags & FrameKey) != 0, Count = r.U8() };
        }

        /// <summary>The next packet of a frame: its offset and length in the frame's buffer.</summary>
        public static void NextPacket(ref ByteReader r, out int offset, out int count)
        {
            count = r.VarCount(MaxPacketBytes);
            if (count < 1) throw new NetFormatException("Empty packet in frame.");
            offset = r.Skip(count);
        }

        // ---------------------------------------------------------------- Strings (replay header)

        public static void Str(ByteWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            w.VarU((ulong)b.Length);
            w.Bytes(b, 0, b.Length);
        }

        public static string Str(ref ByteReader r, byte[] buf)
        {
            int n = r.VarCount(4096);
            int at = r.Skip(n);
            return Encoding.UTF8.GetString(buf, at, n);
        }
    }

    /// <summary>
    /// Delta coder for spectator snapshots. Positions are kept to 1/1024 m, velocities to 1/256 m/s, yaw to 1/65536 of a
    /// turn, pitch to 0.01 degree: plenty to draw from, and exact integers, so encoder and decoder never drift apart.
    /// The encoder and every decoder must see the same run of snapshots from the same key (frames are reliable and ordered).
    /// </summary>
    public sealed class SpecSnapCodec
    {
        public const double PosScale = 1024.0;
        public const double VelScale = 256.0;
        public const double PitchScale = 100.0;
        private const double YawScale = 65536.0 / (2 * Math.PI);

        private const byte MPos = 1, MVel = 2, MAim = 4, MLook = 8, MHud = 16, MBag = 32, MNew = 128;

        /// <summary>Exact quantized state of one player.</summary>
        private struct Q
        {
            public int Team;
            public long Px, Py, Pz, Vx, Vy, Vz;
            public int Yaw, Pitch;
            public ulong Look, Hud, Bag;
        }

        private readonly Dictionary<int, Q> _last = new Dictionary<int, Q>();
        private readonly List<int> _ids = new List<int>();
        private readonly Dictionary<int, Q> _next = new Dictionary<int, Q>();

        public void Reset() => _last.Clear();

        /// <summary>Encodes every player (sorted by id inside). Key: absolute, and the start of a new run.</summary>
        public byte[] Encode(int tick, bool key, List<SpecPlayer> players)
        {
            if (key) _last.Clear();
            var w = new ByteWriter(16 + players.Count * 16);
            w.U8((byte)MsgType.SpecSnap);
            w.VarU((ulong)tick);
            w.U8(key ? (byte)1 : (byte)0);
            _ids.Clear();
            _next.Clear();
            foreach (SpecPlayer p in players)
            {
                if (_next.ContainsKey(p.Id)) continue;
                _ids.Add(p.Id);
                _next[p.Id] = Quantize(p);
            }
            _ids.Sort();
            w.VarU((ulong)_ids.Count);
            foreach (int id in _ids)
            {
                Q q = _next[id];
                w.VarU((ulong)id);
                if (!_last.TryGetValue(id, out Q b) || b.Team != q.Team)
                {
                    w.U8(MNew);
                    w.U8((byte)q.Team);
                    w.VarS(q.Px); w.VarS(q.Py); w.VarS(q.Pz);
                    w.VarS(q.Vx); w.VarS(q.Vy); w.VarS(q.Vz);
                    w.VarU((ulong)q.Yaw); w.VarS(q.Pitch);
                    w.VarU(q.Look); w.VarU(q.Hud); w.VarU(q.Bag);
                    continue;
                }
                byte m = 0;
                if (q.Px != b.Px || q.Py != b.Py || q.Pz != b.Pz) m |= MPos;
                if (q.Vx != b.Vx || q.Vy != b.Vy || q.Vz != b.Vz) m |= MVel;
                if (q.Yaw != b.Yaw || q.Pitch != b.Pitch) m |= MAim;
                if (q.Look != b.Look) m |= MLook;
                if (q.Hud != b.Hud) m |= MHud;
                if (q.Bag != b.Bag) m |= MBag;
                w.U8(m);
                if ((m & MPos) != 0) { w.VarS(q.Px - b.Px); w.VarS(q.Py - b.Py); w.VarS(q.Pz - b.Pz); }
                if ((m & MVel) != 0) { w.VarS(q.Vx - b.Vx); w.VarS(q.Vy - b.Vy); w.VarS(q.Vz - b.Vz); }
                if ((m & MAim) != 0) { w.VarS(WrapYaw(q.Yaw - b.Yaw)); w.VarS(q.Pitch - b.Pitch); }
                if ((m & MLook) != 0) w.VarU(q.Look);
                if ((m & MHud) != 0) w.VarU(q.Hud);
                if ((m & MBag) != 0) w.VarU(q.Bag);
            }
            _last.Clear();
            foreach (var kv in _next) _last[kv.Key] = kv.Value;
            return w.ToArray();
        }

        /// <summary>
        /// Decodes one snapshot into players (cleared first; sorted by id). A delta whose base this decoder never saw
        /// (it joined mid-run) throws: start from a key frame.
        /// </summary>
        public int Decode(ref ByteReader r, List<SpecPlayer> into, out bool key)
        {
            if (r.U8() != (byte)MsgType.SpecSnap) throw new NetFormatException("Not a spectator snapshot.");
            ulong t = r.VarU();
            if (t > int.MaxValue) throw new NetFormatException("Bad tick.");
            byte flags = r.U8();
            if (flags > 1) throw new NetFormatException("Bad snapshot flags.");
            key = flags == 1;
            if (key) _last.Clear();
            int n = r.VarCount(Protocol.MaxPlayersPerSnapshot * 4);
            into.Clear();
            _next.Clear();
            for (int i = 0; i < n; i++)
            {
                ulong idv = r.VarU();
                if (idv > int.MaxValue) throw new NetFormatException("Bad id.");
                int id = (int)idv;
                byte m = r.U8();
                Q q;
                if ((m & MNew) != 0)
                {
                    if (m != MNew) throw new NetFormatException("Bad snapshot mask.");
                    q = new Q { Team = r.U8() };
                    q.Px = r.VarS(); q.Py = r.VarS(); q.Pz = r.VarS();
                    q.Vx = r.VarS(); q.Vy = r.VarS(); q.Vz = r.VarS();
                    ulong yaw = r.VarU();
                    if (yaw > 0xFFFF) throw new NetFormatException("Bad yaw.");
                    q.Yaw = (int)yaw;
                    q.Pitch = (int)r.VarS();
                    q.Look = r.VarU(); q.Hud = r.VarU(); q.Bag = r.VarU();
                }
                else
                {
                    if ((m & 0xC0) != 0) throw new NetFormatException("Bad snapshot mask.");
                    if (!_last.TryGetValue(id, out q)) throw new NetFormatException("Delta without a base (start from a key frame).");
                    if ((m & MPos) != 0) { q.Px += r.VarS(); q.Py += r.VarS(); q.Pz += r.VarS(); }
                    if ((m & MVel) != 0) { q.Vx += r.VarS(); q.Vy += r.VarS(); q.Vz += r.VarS(); }
                    if ((m & MAim) != 0) { q.Yaw = (int)((q.Yaw + r.VarS()) & 0xFFFF); q.Pitch += (int)r.VarS(); }
                    if ((m & MLook) != 0) q.Look = r.VarU();
                    if ((m & MHud) != 0) q.Hud = r.VarU();
                    if ((m & MBag) != 0) q.Bag = r.VarU();
                }
                if (_next.ContainsKey(id)) throw new NetFormatException("Player twice in one snapshot.");
                _next[id] = q;
                into.Add(Expand(id, q));
            }
            _last.Clear();
            foreach (var kv in _next) _last[kv.Key] = kv.Value;
            return (int)t;
        }

        private static long WrapYaw(int d) => ((d + 32768) & 0xFFFF) - 32768;

        private static long R(double v, double scale)
        {
            double x = Math.Round(v * scale, MidpointRounding.AwayFromZero);
            if (double.IsNaN(x)) return 0;
            return (long)Math.Max(-1e15, Math.Min(1e15, x));
        }

        private static Q Quantize(in SpecPlayer p)
        {
            var q = new Q
            {
                Team = Math.Max(0, Math.Min(255, p.Team)),
                Px = R(p.Position.X, PosScale), Py = R(p.Position.Y, PosScale), Pz = R(p.Position.Z, PosScale),
                Vx = R(p.Velocity.X, VelScale), Vy = R(p.Velocity.Y, VelScale), Vz = R(p.Velocity.Z, VelScale),
                Yaw = (int)(R(Math.IEEERemainder(p.Yaw, 2 * Math.PI), YawScale) & 0xFFFF),
                Pitch = (int)R(Math.Max(-90, Math.Min(90, (double)p.Pitch)), PitchScale),
            };
            ulong look = (ulong)((int)p.Mode & 7);
            if (p.Crouched) look |= 1 << 3;
            if (p.Dead) look |= 1 << 4;
            if (p.Sprinting) look |= 1 << 5;
            if (p.Reloading) look |= 1 << 6;
            if (p.Bot) look |= 1 << 7;
            look |= (ulong)((int)p.Held & 15) << 8;
            look |= (ulong)((int)p.Hand & 7) << 12;
            look |= (ulong)(p.Slot & 7) << 15;
            q.Look = look;
            ulong health = (uint)Math.Max(0, Math.Min(1023, p.Health));
            ulong ammo = (uint)Math.Max(0, Math.Min(255, p.Ammo));
            ulong bar = (uint)Math.Max(0, Math.Min(0xFFFF, p.Bar));
            q.Hud = health | (ammo << 10) | (bar << 18);
            ulong bag = 0;
            for (int k = 0; k < Items.Slots; k++)
            {
                ulong item = (uint)((int)p.Bag[k] & 15);
                ulong count = p.Bag.Count(k);
                bag |= (item | (count << 4)) << (12 * k);
            }
            q.Bag = bag;
            return q;
        }

        private static SpecPlayer Expand(int id, in Q q)
        {
            var p = new SpecPlayer
            {
                Id = id,
                Team = q.Team,
                Position = new Vec3d(q.Px / PosScale, q.Py / PosScale, q.Pz / PosScale),
                Velocity = new Vec3d(q.Vx / VelScale, q.Vy / VelScale, q.Vz / VelScale),
                Yaw = (float)((q.Yaw >= 32768 ? q.Yaw - 65536 : q.Yaw) / YawScale),
                Pitch = (float)(q.Pitch / PitchScale),
            };
            int mode = (int)(q.Look & 7);
            if (mode > (int)MoveMode.Mantle) throw new NetFormatException("Bad move mode.");
            p.Mode = (MoveMode)mode;
            p.Crouched = (q.Look & (1 << 3)) != 0;
            p.Dead = (q.Look & (1 << 4)) != 0;
            p.Sprinting = (q.Look & (1 << 5)) != 0;
            p.Reloading = (q.Look & (1 << 6)) != 0;
            p.Bot = (q.Look & (1 << 7)) != 0;
            int held = (int)((q.Look >> 8) & 15);
            if (held > (int)Items.Last || held == (int)Item.Chest) throw new NetFormatException("Bad item.");
            p.Held = (Item)held;
            int hand = (int)((q.Look >> 12) & 7);
            if (hand > (int)HandMode.Disarmed) throw new NetFormatException("Bad hand mode.");
            p.Hand = (HandMode)hand;
            p.Slot = (byte)((q.Look >> 15) & 7);
            if (p.Slot >= Items.Slots) throw new NetFormatException("Bad slot.");
            p.Health = (int)(q.Hud & 1023);
            p.Ammo = (int)((q.Hud >> 10) & 255);
            p.Bar = (int)((q.Hud >> 18) & 0xFFFF);
            for (int k = 0; k < Items.Slots; k++)
            {
                ulong s = q.Bag >> (12 * k);
                int it = (int)(s & 15);
                if (it > (int)Items.Last || it == (int)Item.Chest) throw new NetFormatException("Bad item.");
                p.Bag[k] = (Item)it;
                p.Bag.SetCount(k, (int)((s >> 4) & 255));
            }
            return p;
        }
    }
}
