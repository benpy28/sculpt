using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    public enum WorldEventType : byte
    {
        /// <summary>A windup started (Piece already carved by the ops before it). StartTick..StartTick+WindupTicks.</summary>
        Began = 0,
        /// <summary>Only for the sender: the confirm never started (Result says why). Free.</summary>
        Rejected = 1,
        Completed = 2,
        /// <summary>Cancelled at resolution and refunded (Result says why).</summary>
        Cancelled = 3,
        /// <summary>A falling piece came to rest (thunk, dust).</summary>
        Landed = 4,
        /// <summary>PlayerId was shoved or lifted by a sculpt.</summary>
        Shoved = 5,
        /// <summary>PlayerId got a friction kick from a piece moving under them (or a throw tearing it away).</summary>
        Dragged = 6,
        /// <summary>A piece became a projectile. The flight state is the one after the server's tick AtTick.</summary>
        Thrown = 7,
        ThrowImpact = 8,
        /// <summary>PlayerId was hit by a thrown piece for Value damage, Disarm ticks without weapons or sculpting.</summary>
        ThrowHit = 9,
    }

    /// <summary>One sculpt event as it travels to clients. Fields used depend on Type.</summary>
    public struct WorldEvent
    {
        public WorldEventType Type;
        public int PlayerId;
        public int Team;
        public byte Seq;
        public ushort Piece;
        public SculptActionType Action;
        public Dir Direction;
        public bool Reinforce;
        public SculptResult Result;
        // Began
        public int StartTick;
        public int WindupTicks;
        public int Cost;
        public bool LocksGun;
        public bool EnemyReinforced;
        public Vec3d Look;
        public int[] Warned;
        // Thrown / ThrowImpact / ThrowHit
        public int ProjectileId;
        public int AtTick;
        public int SubCells;
        public long X, Y, Z, VX, VY, VZ, HX, HY, HZ;
        public int LaunchTick;
        public int Value;
        public int Disarm;
        public Vec3d Point;

        public ThrownPiece ToProjectile() => new ThrownPiece
        {
            Id = ProjectileId,
            Owner = PlayerId,
            Team = Team,
            PieceId = Piece,
            SubCells = SubCells,
            X = X, Y = Y, Z = Z, VX = VX, VY = VY, VZ = VZ, HX = HX, HY = HY, HZ = HZ,
            LaunchTick = LaunchTick,
        };

        public static WorldEvent FromProjectile(ThrownPiece t, int atTick)
        {
            return new WorldEvent
            {
                Type = WorldEventType.Thrown,
                PlayerId = t.Owner,
                Team = t.Team,
                Piece = t.PieceId,
                ProjectileId = t.Id,
                AtTick = atTick,
                SubCells = t.SubCells,
                X = t.X, Y = t.Y, Z = t.Z, VX = t.VX, VY = t.VY, VZ = t.VZ, HX = t.HX, HY = t.HY, HZ = t.HZ,
                LaunchTick = t.LaunchTick,
            };
        }
    }

    /// <summary>
    /// Server to client, reliable and ordered: every piece op and sculpt event of one server tick (MsgType.World).
    /// With Join set it is the late-join state instead: the whole op log since the map was generated, the windups in
    /// progress (Began with their real StartTick) and the pieces in flight (Thrown with their current state).
    /// </summary>
    public struct WorldMsg
    {
        public int Tick;
        public bool Join;
        public List<PieceOp> Ops;
        public List<WorldEvent> Events;
    }

    public static class WorldProtocol
    {
        public const int MaxCellsPerOp = 1 << 16;

        public static void Write(ByteWriter w, in WorldMsg m)
        {
            w.U8((byte)MsgType.World);
            w.I32(m.Tick);
            w.Bool(m.Join);
            int nOps = m.Ops?.Count ?? 0;
            w.I32(nOps);
            for (int i = 0; i < nOps; i++) WriteOp(w, m.Ops[i]);
            int nEv = m.Events?.Count ?? 0;
            w.U16(checked((ushort)nEv));
            for (int i = 0; i < nEv; i++) WriteEvent(w, m.Events[i]);
        }

        public static WorldMsg Read(ref ByteReader r, List<PieceOp> ops, List<WorldEvent> events)
        {
            if (r.U8() != (byte)MsgType.World) throw new NetFormatException("Unexpected message type.");
            var m = new WorldMsg { Tick = r.I32(), Join = r.Bool(), Ops = ops, Events = events };
            ops.Clear();
            events.Clear();
            int nOps = r.I32();
            if (nOps < 0 || nOps > r.Remaining) throw new NetFormatException("Bad op count.");
            for (int i = 0; i < nOps; i++) ops.Add(ReadOp(ref r));
            int nEv = r.U16();
            for (int i = 0; i < nEv; i++) events.Add(ReadEvent(ref r));
            return m;
        }

        // ---------------------------------------------------------------- Ops

        public static void WriteOp(ByteWriter w, in PieceOp op)
        {
            w.U8((byte)op.Type);
            w.U16(op.Id);
            switch (op.Type)
            {
                case PieceOpType.Carve: WriteCells(w, op.Cells); break;
                case PieceOpType.Translate:
                    w.I8(checked((sbyte)op.Delta.X));
                    w.I8(checked((sbyte)op.Delta.Y));
                    w.I8(checked((sbyte)op.Delta.Z));
                    w.Bool(op.FillVacated);
                    break;
                case PieceOpType.Stamp:
                    w.U8(op.Team);
                    w.I32(op.TintTick);
                    w.U8(op.ReinforceTeam);
                    w.I32(op.ReinforceUntilTick);
                    break;
                case PieceOpType.Remove: break;
                default: throw new ArgumentException("Bad op type.");
            }
        }

        public static PieceOp ReadOp(ref ByteReader r)
        {
            byte t = r.U8();
            if (t > (byte)PieceOpType.Remove) throw new NetFormatException("Bad op type.");
            var op = new PieceOp { Type = (PieceOpType)t, Id = r.U16() };
            if (op.Id < SubId.FirstPiece) throw new NetFormatException("Bad piece id.");
            switch (op.Type)
            {
                case PieceOpType.Carve: op.Cells = ReadCells(ref r); break;
                case PieceOpType.Translate:
                    op.Delta = new Int3(r.I8(), r.I8(), r.I8());
                    op.FillVacated = r.Bool();
                    break;
                case PieceOpType.Stamp:
                    op.Team = r.U8();
                    op.TintTick = r.I32();
                    op.ReinforceTeam = r.U8();
                    op.ReinforceUntilTick = r.I32();
                    break;
            }
            return op;
        }

        /// <summary>Cells as a bounding box plus an occupancy bitmask in Y, Z, X order (a carve is a compact clump).</summary>
        public static void WriteCells(ByteWriter w, Int3[] cells)
        {
            int n = cells?.Length ?? 0;
            if (n == 0) { w.U16(0); w.U16(0); w.U16(0); return; }
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c in cells)
            {
                x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); z0 = Math.Min(z0, c.Z);
                x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y); z1 = Math.Max(z1, c.Z);
            }
            int sx = x1 - x0 + 1, sy = y1 - y0 + 1, sz = z1 - z0 + 1;
            w.U16(checked((ushort)sx));
            w.U16(checked((ushort)sy));
            w.U16(checked((ushort)sz));
            w.Cell(new Int3(x0, y0, z0));
            long bits = (long)sx * sy * sz;
            var mask = new byte[(bits + 7) / 8];
            foreach (var c in cells)
            {
                long i = ((long)(c.Y - y0) * sz + (c.Z - z0)) * sx + (c.X - x0);
                mask[i >> 3] |= (byte)(1 << (int)(i & 7));
            }
            foreach (byte b in mask) w.U8(b);
        }

        public static Int3[] ReadCells(ref ByteReader r)
        {
            int sx = r.U16(), sy = r.U16(), sz = r.U16();
            if (sx == 0 || sy == 0 || sz == 0)
            {
                if (sx != 0 || sy != 0 || sz != 0) throw new NetFormatException("Bad cell box.");
                return Array.Empty<Int3>();
            }
            Int3 o = r.Cell();
            long bits = (long)sx * sy * sz;
            long bytes = (bits + 7) / 8;
            if (bytes > r.Remaining) throw new NetFormatException("Cell mask truncated.");
            var list = new List<Int3>();
            for (long i = 0; i < bits; i += 8)
            {
                byte b = r.U8();
                if (b == 0) continue;
                for (int k = 0; k < 8; k++)
                {
                    if ((b & (1 << k)) == 0) continue;
                    long j = i + k;
                    if (j >= bits) throw new NetFormatException("Bad cell mask.");
                    int x = (int)(j % sx), z = (int)(j / sx % sz), y = (int)(j / sx / sz);
                    list.Add(new Int3(o.X + x, o.Y + y, o.Z + z));
                    if (list.Count > MaxCellsPerOp) throw new NetFormatException("Too many cells.");
                }
            }
            return list.ToArray(); // already Y, Z, X sorted
        }

        // ---------------------------------------------------------------- Events

        public static void WriteEvent(ByteWriter w, in WorldEvent e)
        {
            w.U8((byte)e.Type);
            w.U16(checked((ushort)e.PlayerId));
            switch (e.Type)
            {
                case WorldEventType.Began:
                    w.U8(e.Seq);
                    w.U8((byte)e.Team);
                    w.U16(e.Piece);
                    w.U8((byte)((byte)e.Action | ((byte)e.Direction << 4)));
                    byte f = 0;
                    if (e.Reinforce) f |= 1;
                    if (e.LocksGun) f |= 2;
                    if (e.EnemyReinforced) f |= 4;
                    w.U8(f);
                    w.I32(e.StartTick);
                    w.U8(checked((byte)e.WindupTicks));
                    w.U16(checked((ushort)e.Cost));
                    if (e.Action == SculptActionType.Throw) w.VecF(e.Look);
                    int nw = e.Warned?.Length ?? 0;
                    w.U8(checked((byte)nw));
                    for (int i = 0; i < nw; i++) w.U16(checked((ushort)e.Warned[i]));
                    break;
                case WorldEventType.Rejected:
                    w.U8(e.Seq);
                    w.U8((byte)e.Result);
                    break;
                case WorldEventType.Completed:
                    w.U8(e.Seq);
                    w.U16(e.Piece);
                    break;
                case WorldEventType.Cancelled:
                    w.U8(e.Seq);
                    w.U8((byte)e.Result);
                    w.U16(e.Piece);
                    break;
                case WorldEventType.Landed:
                case WorldEventType.Shoved:
                case WorldEventType.Dragged:
                    w.U16(e.Piece);
                    break;
                case WorldEventType.Thrown:
                    w.U8((byte)e.Team);
                    w.U16(e.Piece);
                    w.U16(checked((ushort)e.ProjectileId));
                    w.I32(e.AtTick);
                    w.I32(e.LaunchTick);
                    w.U16(checked((ushort)e.SubCells));
                    w.I64(e.X); w.I64(e.Y); w.I64(e.Z);
                    w.I64(e.VX); w.I64(e.VY); w.I64(e.VZ);
                    w.I64(e.HX); w.I64(e.HY); w.I64(e.HZ);
                    break;
                case WorldEventType.ThrowImpact:
                    w.U16(checked((ushort)e.ProjectileId));
                    w.VecF(e.Point);
                    break;
                case WorldEventType.ThrowHit:
                    w.U16(checked((ushort)e.ProjectileId));
                    w.U16(checked((ushort)e.Value));
                    w.U8(checked((byte)e.Disarm));
                    w.VecF(e.Point);
                    break;
                default: throw new ArgumentException("Bad event type.");
            }
        }

        public static WorldEvent ReadEvent(ref ByteReader r)
        {
            byte t = r.U8();
            if (t > (byte)WorldEventType.ThrowHit) throw new NetFormatException("Bad event type.");
            var e = new WorldEvent { Type = (WorldEventType)t, PlayerId = r.U16() };
            switch (e.Type)
            {
                case WorldEventType.Began:
                {
                    e.Seq = r.U8();
                    e.Team = r.U8();
                    e.Piece = r.U16();
                    byte td = r.U8();
                    e.Action = (SculptActionType)(td & 15);
                    e.Direction = (Dir)(td >> 4);
                    if (e.Action > SculptActionType.Throw || e.Direction > Dir.South) throw new NetFormatException("Bad action.");
                    byte f = r.U8();
                    e.Reinforce = (f & 1) != 0;
                    e.LocksGun = (f & 2) != 0;
                    e.EnemyReinforced = (f & 4) != 0;
                    e.StartTick = r.I32();
                    e.WindupTicks = r.U8();
                    e.Cost = r.U16();
                    if (e.Action == SculptActionType.Throw) e.Look = r.VecFinite();
                    int nw = r.U8();
                    e.Warned = new int[nw];
                    for (int i = 0; i < nw; i++) e.Warned[i] = r.U16();
                    break;
                }
                case WorldEventType.Rejected:
                    e.Seq = r.U8();
                    e.Result = ReadResult(ref r);
                    break;
                case WorldEventType.Completed:
                    e.Seq = r.U8();
                    e.Piece = r.U16();
                    break;
                case WorldEventType.Cancelled:
                    e.Seq = r.U8();
                    e.Result = ReadResult(ref r);
                    e.Piece = r.U16();
                    break;
                case WorldEventType.Landed:
                case WorldEventType.Shoved:
                case WorldEventType.Dragged:
                    e.Piece = r.U16();
                    break;
                case WorldEventType.Thrown:
                    e.Team = r.U8();
                    e.Piece = r.U16();
                    e.ProjectileId = r.U16();
                    e.AtTick = r.I32();
                    e.LaunchTick = r.I32();
                    e.SubCells = r.U16();
                    e.X = r.I64(); e.Y = r.I64(); e.Z = r.I64();
                    e.VX = r.I64(); e.VY = r.I64(); e.VZ = r.I64();
                    e.HX = r.I64(); e.HY = r.I64(); e.HZ = r.I64();
                    break;
                case WorldEventType.ThrowImpact:
                    e.ProjectileId = r.U16();
                    e.Point = r.VecFinite();
                    break;
                case WorldEventType.ThrowHit:
                    e.ProjectileId = r.U16();
                    e.Value = r.U16();
                    e.Disarm = r.U8();
                    e.Point = r.VecFinite();
                    break;
            }
            return e;
        }

        private static SculptResult ReadResult(ref ByteReader r)
        {
            byte b = r.U8();
            if (b > (byte)SculptResult.TooLate) throw new NetFormatException("Bad result.");
            return (SculptResult)b;
        }
    }
}
