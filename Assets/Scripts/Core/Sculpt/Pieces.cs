using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>A carved piece: a rigid set of sub-cells with one id, plus its per-piece state.</summary>
    public sealed class PieceInfo
    {
        public ushort Id;
        /// <summary>Team that last sculpted it (owner for gun-lock and tint), 0 = none.</summary>
        public byte Team;
        /// <summary>Tick the tint was applied (cosmetic, fades over SculptConfig.TintFadeTicks).</summary>
        public int TintTick;
        public byte ReinforceTeam;
        /// <summary>Reinforcement is active while tick &lt; ReinforceUntilTick.</summary>
        public int ReinforceUntilTick;
        /// <summary>Sub-cells, sorted Y then Z then X.</summary>
        public readonly List<Int3> Cells = new List<Int3>();

        public bool IsReinforced(int tick) => ReinforceTeam != 0 && tick < ReinforceUntilTick;
        public bool IsEnemyReinforced(int team, int tick) => IsReinforced(tick) && ReinforceTeam != team;

        public Aabb Bounds()
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c in Cells)
            {
                x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); z0 = Math.Min(z0, c.Z);
                x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y); z1 = Math.Max(z1, c.Z);
            }
            const double s = SubGrid.SubSize;
            return new Aabb(new Vec3d(x0 * s, y0 * s, z0 * s), new Vec3d((x1 + 1) * s, (y1 + 1) * s, (z1 + 1) * s));
        }

        public int MinY
        {
            get
            {
                int m = int.MaxValue;
                foreach (var c in Cells) m = Math.Min(m, c.Y);
                return m;
            }
        }
    }

    public enum PieceOpType : byte
    {
        /// <summary>Natural rock sub-cells become a new piece.</summary>
        Carve,
        /// <summary>A piece moves by Delta. FillVacated: sub-cells it leaves become natural rock (raise).</summary>
        Translate,
        /// <summary>Owner team, tint and reinforcement change.</summary>
        Stamp,
        /// <summary>A piece is removed from the world (thrown).</summary>
        Remove,
    }

    /// <summary>
    /// One terrain change by piece id. The server's sculpt system makes every grid change through these, so a client
    /// that applies the same ops to the same starting grid ends with the same sub-cells (M4 sends them as deltas).
    /// </summary>
    public struct PieceOp
    {
        public PieceOpType Type;
        public ushort Id;
        public Int3[] Cells;
        public Int3 Delta;
        public bool FillVacated;
        public byte Team;
        public int TintTick;
        public byte ReinforceTeam;
        public int ReinforceUntilTick;
    }

    /// <summary>All carved pieces of a grid, and the only code that moves them. Logs every op.</summary>
    public sealed class PieceTable
    {
        public readonly SubGrid Grid;
        public readonly List<PieceOp> Ops = new List<PieceOp>();
        /// <summary>Called for every sub-cell written (lock disturbance, gravity bookkeeping).</summary>
        public Action<Int3> OnWrite;

        private readonly Dictionary<ushort, PieceInfo> _pieces = new Dictionary<ushort, PieceInfo>();
        private int _nextId = SubId.FirstPiece;
        private readonly HashSet<Int3> _scratchNew = new HashSet<Int3>();

        public PieceTable(SubGrid grid) => Grid = grid ?? throw new ArgumentNullException(nameof(grid));

        public int Count => _pieces.Count;

        public PieceInfo Get(ushort id) => _pieces.TryGetValue(id, out var p) ? p : null;

        public IEnumerable<PieceInfo> All => _pieces.Values;

        /// <summary>Ids in ascending order (for deterministic iteration).</summary>
        public List<ushort> SortedIds()
        {
            var ids = new List<ushort>(_pieces.Keys);
            ids.Sort();
            return ids;
        }

        private ushort AllocateId()
        {
            if (_nextId <= ushort.MaxValue) return (ushort)_nextId++;
            for (int id = SubId.FirstPiece; id <= ushort.MaxValue; id++)
                if (!_pieces.ContainsKey((ushort)id)) return (ushort)id;
            throw new InvalidOperationException("Out of piece ids.");
        }

        private void Write(Int3 c, ushort id)
        {
            Grid.Set(c, id);
            OnWrite?.Invoke(c);
        }

        // ---------------- Ops ----------------

        /// <summary>Carves natural rock into a new piece. Cells must be natural rock.</summary>
        public PieceInfo Carve(IList<Int3> cells)
        {
            var op = new PieceOp { Type = PieceOpType.Carve, Id = AllocateId(), Cells = SortedCopy(cells) };
            Apply(op);
            return _pieces[op.Id];
        }

        public void Translate(ushort id, Int3 delta, bool fillVacated) =>
            Apply(new PieceOp { Type = PieceOpType.Translate, Id = id, Delta = delta, FillVacated = fillVacated });

        public void Stamp(ushort id, byte team, int tintTick, byte reinforceTeam, int reinforceUntilTick) =>
            Apply(new PieceOp { Type = PieceOpType.Stamp, Id = id, Team = team, TintTick = tintTick, ReinforceTeam = reinforceTeam, ReinforceUntilTick = reinforceUntilTick });

        public void Remove(ushort id) => Apply(new PieceOp { Type = PieceOpType.Remove, Id = id });

        /// <summary>Applies one op (server and client alike) and logs it.</summary>
        public void Apply(in PieceOp op)
        {
            switch (op.Type)
            {
                case PieceOpType.Carve:
                {
                    var p = new PieceInfo { Id = op.Id };
                    p.Cells.AddRange(op.Cells);
                    _pieces[op.Id] = p;
                    if (op.Id >= _nextId) _nextId = op.Id + 1;
                    foreach (var c in op.Cells) Write(c, op.Id);
                    break;
                }
                case PieceOpType.Translate:
                {
                    PieceInfo p = _pieces[op.Id];
                    _scratchNew.Clear();
                    for (int i = 0; i < p.Cells.Count; i++)
                    {
                        Int3 n = p.Cells[i] + op.Delta;
                        _scratchNew.Add(n);
                    }
                    foreach (var c in p.Cells)
                        if (!_scratchNew.Contains(c)) Write(c, op.FillVacated ? SubId.Natural : SubId.Empty);
                    for (int i = 0; i < p.Cells.Count; i++)
                    {
                        p.Cells[i] = p.Cells[i] + op.Delta;
                        if (Grid.Get(p.Cells[i]) != op.Id) Write(p.Cells[i], op.Id);
                    }
                    break;
                }
                case PieceOpType.Stamp:
                {
                    PieceInfo p = _pieces[op.Id];
                    p.Team = op.Team;
                    p.TintTick = op.TintTick;
                    p.ReinforceTeam = op.ReinforceTeam;
                    p.ReinforceUntilTick = op.ReinforceUntilTick;
                    break;
                }
                case PieceOpType.Remove:
                {
                    PieceInfo p = _pieces[op.Id];
                    foreach (var c in p.Cells) Write(c, SubId.Empty);
                    _pieces.Remove(op.Id);
                    break;
                }
            }
            Ops.Add(op);
        }

        /// <summary>Rebuilds the table from the ids in the grid (late join). Per-piece state starts clear.</summary>
        public void RebuildFromGrid()
        {
            _pieces.Clear();
            for (int y = Grid.MinY; y < Grid.MaxYExclusive; y++)
                for (int z = 0; z < Grid.SizeZ; z++)
                    for (int x = 0; x < Grid.SizeX; x++)
                    {
                        ushort id = Grid.Get(x, y, z);
                        if (!SubId.IsPiece(id)) continue;
                        if (!_pieces.TryGetValue(id, out var p)) _pieces[id] = p = new PieceInfo { Id = id };
                        p.Cells.Add(new Int3(x, y, z));
                        if (id >= _nextId) _nextId = id + 1;
                    }
        }

        public static Int3[] SortedCopy(IList<Int3> cells)
        {
            var a = new Int3[cells.Count];
            cells.CopyTo(a, 0);
            Array.Sort(a, Int3.CompareYZX);
            return a;
        }
    }
}
