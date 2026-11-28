using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Blast holes (Ben 2026-11-25: "not a grid snapped rectangle", "predictable but fun and satisfying"). Every wall and
    /// floor tile's face is an 8 x 8 grid of cells (0.1875 m on a 1.5 m tile). Health and state stay per tile (cracks,
    /// charge, heal), but when a hit breaks a tile the hole is shaped by the hit: a rough disc of cells around where it
    /// landed, sized by the hit's damage, which can cut across tile borders into the tiles around it.
    ///
    /// Fairness rules (Ben: no pixel holes, at least half a body, at least 0.3 m wide, no slivers):
    /// - Only cells that sit in a full 2 x 2 block of blown cells go, so every opening is at least 0.375 m wide everywhere.
    /// - A new hole has at least <see cref="BreachConfig.MinHoleArea"/> (0.63 m^2) of cells, or joins an opening already
    ///   there; the disc grows a little until it does.
    /// - Wall left thinner than 2 cells (0.375 m) between openings crumbles, so no thin slivers or spikes.
    /// - When cells come back (heal, reinforce, rebuild), openings next to them that became too thin or too small fill in.
    /// - Reinforced tiles never lose cells to a neighbour's break; a tile with under a quarter of its cells left goes whole.
    /// Deterministic: the disc's rough edge comes from a fixed hash of each cell's position, the same on every machine.
    /// </summary>
    public sealed partial class TileWalls
    {
        private const int N = Brush.CellsPerSide;
        private const int Cells = Brush.CellCount;

        // Per tile: the tile across each edge of its cell grid (+a1, -a1, +a2, -a2) with the same grid, or -1.
        private int[] _edge;
        private bool _filling;

        /// <summary>Can this tile lose single cells (wall and floor tiles)?</summary>
        public bool HasCells(int tile) => World[_tiles[tile].Brush].HasCells;

        /// <summary>The tile's open cells, all of them when it is Broken.</summary>
        public ulong OpenCells(int tile)
        {
            WallTile t = _tiles[tile];
            return t.State == TileState.Broken ? Brush.AllCells : t.Open;
        }

        /// <summary>The box of one cell of a celled tile.</summary>
        public Aabb CellBox(int tile, int cell) => World[_tiles[tile].Brush].CellBox(cell);

        /// <summary>The tile across edge <paramref name="dir"/> (0 +a1, 1 -a1, 2 +a2, 3 -a2) of a celled tile's grid, or -1.</summary>
        public int EdgeNeighbour(int tile, int dir)
        {
            EnsureEdges();
            return _edge[tile * 4 + dir];
        }

        /// <summary>
        /// The cell one step along <paramref name="dir"/> (0 +a1, 1 -a1, 2 +a2, 3 -a2) from a cell of a celled tile: its
        /// tile, or -1 if something solid that is not a celled tile is there (frame), or -2 for open air.
        /// </summary>
        public int CellNeighbour(int tile, int cell, int dir, out int cell2)
        {
            EnsureEdges();
            return Step(tile, cell, dir, out cell2);
        }

        public static int PopCount(ulong v)
        {
            v -= (v >> 1) & 0x5555555555555555ul;
            v = (v & 0x3333333333333333ul) + ((v >> 2) & 0x3333333333333333ul);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0Ful;
            return (int)((v * 0x0101010101010101ul) >> 56);
        }

        /// <summary>Cells a new hole needs at least (from <see cref="BreachConfig.MinHoleArea"/>).</summary>
        public int MinHoleCells(int tile)
        {
            Brush b = World[_tiles[tile].Brush];
            double s1 = (Ax(b.Bounds.Max, b.CellA1) - Ax(b.Bounds.Min, b.CellA1)) / N;
            double s2 = (Ax(b.Bounds.Max, b.CellA2) - Ax(b.Bounds.Min, b.CellA2)) / N;
            return Math.Max(4, (int)Math.Ceiling(Config.MinHoleArea / (s1 * s2) - 1e-9));
        }

        private static double Ax(Vec3d v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

        private static Vec3d WithAx(Vec3d v, int a, double x) =>
            a == 0 ? new Vec3d(x, v.Y, v.Z) : a == 1 ? new Vec3d(v.X, x, v.Z) : new Vec3d(v.X, v.Y, x);

        private static int ThinAxis(in Brush b) => 3 - b.CellA1 - b.CellA2;

        /// <summary>
        /// Builds the cell neighbour table now instead of on the first hit. It covers the whole map (about 0.09 s on the
        /// battle royale map), so building it lazily froze the first wall hit of every match; the game calls this at load.
        /// </summary>
        public void Prepare() => EnsureEdges();

        private void EnsureEdges()
        {
            if (_edge != null && _edge.Length == _tiles.Count * 4) return;
            _edge = new int[_tiles.Count * 4];
            for (int i = 0; i < _edge.Length; i++) _edge[i] = -1;
            var near = new List<int>();
            for (int t = 0; t < _tiles.Count; t++)
            {
                Brush b = World[_tiles[t].Brush];
                if (!b.HasCells) continue;
                Vec3d c = b.Bounds.Center;
                int thin = ThinAxis(b);
                for (int dir = 0; dir < 4; dir++)
                {
                    int axis = dir < 2 ? b.CellA1 : b.CellA2, other = dir < 2 ? b.CellA2 : b.CellA1;
                    double edge = dir % 2 == 0 ? Ax(b.Bounds.Max, axis) + 0.05 : Ax(b.Bounds.Min, axis) - 0.05;
                    Vec3d p = WithAx(c, axis, edge);
                    TilesNear(new Aabb(p - new Vec3d(0.01, 0.01, 0.01), p + new Vec3d(0.01, 0.01, 0.01)), near);
                    near.Sort();
                    foreach (int u in near)
                    {
                        if (u == t) continue;
                        Brush o = World[_tiles[u].Brush];
                        if (!o.HasCells || o.CellA1 != b.CellA1 || o.CellA2 != b.CellA2) continue;
                        if (!o.Bounds.Contains(p, 0)) continue;
                        // Same grid across the edge: the same span along the other face axis and the same slab.
                        if (Math.Abs(Ax(o.Bounds.Min, other) - Ax(b.Bounds.Min, other)) > 1e-6 || Math.Abs(Ax(o.Bounds.Max, other) - Ax(b.Bounds.Max, other)) > 1e-6) continue;
                        if (Math.Abs(Ax(o.Bounds.Min, thin) - Ax(b.Bounds.Min, thin)) > 1e-6 || Math.Abs(Ax(o.Bounds.Max, thin) - Ax(b.Bounds.Max, thin)) > 1e-6) continue;
                        _edge[t * 4 + dir] = u;
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ cell walking

        /// <summary>Marks beyond the grid: something solid there (frame, another wall), or open air (a doorway, the sky).</summary>
        private const int BeyondSolid = -1, BeyondAir = -2;

        /// <summary>The cell one step along <paramref name="dir"/>: its tile (or Beyond*) and cell index.</summary>
        private int Step(int tile, int cell, int dir, out int c2)
        {
            int i = cell % N, j = cell / N;
            switch (dir)
            {
                case 0: i++; break;
                case 1: i--; break;
                case 2: j++; break;
                default: j--; break;
            }
            if (i >= 0 && i < N && j >= 0 && j < N) { c2 = i + N * j; return tile; }
            int nb = _edge[tile * 4 + dir];
            i = (i + N) % N;
            j = (j + N) % N;
            c2 = i + N * j;
            if (nb >= 0) return nb;
            // Off the grid: probe just past the cell's edge (cached while one break or fill is being worked out; nothing
            // moves in between).
            long key = ((long)tile * 64 + cell) * 4 + dir;
            if (_beyond.TryGetValue(key, out int known)) return known;
            Brush b = World[_tiles[tile].Brush];
            Aabb cb = b.CellBox(cell);
            int axis = dir < 2 ? b.CellA1 : b.CellA2;
            Vec3d p = cb.Center;
            p = WithAx(p, axis, dir % 2 == 0 ? Ax(cb.Max, axis) + 0.04 : Ax(cb.Min, axis) - 0.04);
            var probe = new Aabb(p - new Vec3d(0.02, 0.02, 0.02), p + new Vec3d(0.02, 0.02, 0.02));
            bool assumed = false;
            foreach (Aabb s in _assumeSolid) if (s.Overlaps(probe)) { assumed = true; break; }
            int r = assumed || World.BoxOverlapsSolid(probe) || (Outside != null && Outside.BoxOverlapsSolid(probe)) ? BeyondSolid : BeyondAir;
            _beyond[key] = r;
            return r;
        }

        private readonly Dictionary<long, int> _beyond = new Dictionary<long, int>();
        /// <summary>While a heal is planned: frame pieces it will bring back, counted as solid past a grid's edge.</summary>
        private readonly List<Aabb> _assumeSolid = new List<Aabb>();

        /// <summary>The rest of the world (the ground under the buildings), also solid past a tile grid's edge.</summary>
        public ITerrain Outside;

        /// <summary>Is the cell part of a 2 x 2 block (any of the four around it) where every cell passes the test?</summary>
        private bool InBlock(int tile, int cell, Func<int, int, bool> test)
        {
            for (int di = 0; di < 2; di++)
                for (int dj = 2; dj < 4; dj++)
                {
                    int a = Step(tile, cell, di, out int ca);
                    int b = Step(tile, cell, dj, out int cb);
                    int d, cd;
                    if (a >= 0) d = Step(a, ca, dj, out cd);
                    else if (b >= 0) d = Step(b, cb, di, out cd);
                    else { d = a; cd = 0; }
                    if (test(a, ca) && test(b, cb) && test(d, cd)) return true;
                }
            return false;
        }

        /// <summary>
        /// <see cref="InBlock(int, int, Func{int, int, bool})"/> where <paramref name="pass"/> already holds which cells of
        /// this tile pass the test: a 2 x 2 block that lies inside the tile is read from the bits, only blocks across
        /// the tile's edge walk to the next tile. Same answer, a fraction of the work (the tidy asks this for every cell
        /// of every tile around a hole, many times over).
        /// </summary>
        private bool InBlock(int tile, int cell, ulong pass, Func<int, int, bool> test)
        {
            int i = cell % N, j = cell / N;
            for (int di = 0; di < 2; di++)
            {
                int i2 = di == 0 ? i + 1 : i - 1;
                for (int dj = 2; dj < 4; dj++)
                {
                    int j2 = dj == 2 ? j + 1 : j - 1;
                    if (i2 >= 0 && i2 < N && j2 >= 0 && j2 < N)
                    {
                        ulong need = (1ul << (i2 + N * j)) | (1ul << (i + N * j2)) | (1ul << (i2 + N * j2));
                        if ((pass & need) == need) return true;
                        continue;
                    }
                    int a = Step(tile, cell, di, out int ca);
                    int b = Step(tile, cell, dj, out int cb);
                    int d, cd;
                    if (a >= 0) d = Step(a, ca, dj, out cd);
                    else if (b >= 0) d = Step(b, cb, di, out cd);
                    else { d = a; cd = 0; }
                    if (test(a, ca) && test(b, cb) && test(d, cd)) return true;
                }
            }
            return false;
        }

        /// <summary>Fixed roughness for a spot on a wall: -1..1 from a hash of its position (mm).</summary>
        private static double Rough(Vec3d p)
        {
            unchecked
            {
                ulong h = (ulong)(long)Math.Round(p.X * 1000) * 0x9E3779B97F4A7C15ul
                          ^ (ulong)(long)Math.Round(p.Y * 1000) * 0xC2B2AE3D27D4EB4Ful
                          ^ (ulong)(long)Math.Round(p.Z * 1000) * 0x165667B19E3779F9ul;
                h ^= h >> 31; h *= 0xBF58476D1CE4E5B9ul; h ^= h >> 27; h *= 0x94D049BB133111EBul; h ^= h >> 31;
                return (h >> 11) * (1.0 / (1ul << 53)) * 2 - 1;
            }
        }

        // ------------------------------------------------------------------ breaking
        private bool TouchesOpening(HashSet<long> blow, Func<int, int, bool> open)
        {
            foreach (long k in blow)
            {
                int u = (int)(k / 64), c = (int)(k % 64);
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = Step(u, c, dir, out int cv);
                    if (open(v, cv) && !(v >= 0 && blow.Contains((long)v * 64 + cv))) return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ tidying

        /// <summary>
        /// Makes the cells of <paramref name="region"/> follow the fairness rules, changing only tiles that
        /// <paramref name="canChange"/> allows:
        /// 1. a few rounds of crumbling standing cells that are in no 2 x 2 standing block and closing open cells that are
        ///    in no 2 x 2 open block;
        /// 2. any cell still in neither snaps its own aligned 2 x 2 block (inside its tile) to the majority, ties to open
        ///    when <paramref name="tieOpen"/>. A uniform aligned block can never break either rule, so this always ends
        ///    with every cell passing;
        /// 3. openings smaller than a minimum hole that do not reach a broken tile or open air close (filling a whole
        ///    opening never breaks rule 1).
        /// </summary>
        private void Regularise(Dictionary<int, ulong> work, List<int> region, Func<int, bool> canChange, Func<int, bool> broken, bool tieOpen)
        {
            ulong W(int u) => work.TryGetValue(u, out ulong m) ? m : OpenCells(u);
            bool Open(int u, int c) => u == BeyondAir || (u >= 0 && ((W(u) >> c) & 1) != 0);
            bool Solid(int u, int c) => u == BeyondSolid || (u >= 0 && ((W(u) >> c) & 1) == 0);
            bool Partial(int u) => canChange(u) && !broken(u) && W(u) != 0 && W(u) != Brush.AllCells;
            Func<int, int, bool> openF = Open, solidF = Solid; // one delegate each, not one per cell

            for (int round = 0; round < 4; round++)
            {
                bool changed = false;
                foreach (int u in region)
                {
                    if (!Partial(u)) continue;
                    ulong m = W(u), lk = LockOf(u);
                    for (int c = 0; c < Cells; c++)
                    {
                        bool open = ((m >> c) & 1) != 0;
                        if (open || ((lk >> c) & 1) != 0 || InBlock(u, c, ~m, solidF)) continue;
                        m |= 1ul << c;
                        work[u] = m;
                        changed = true;
                    }
                }
                foreach (int u in region)
                {
                    if (!Partial(u)) continue;
                    ulong m = W(u);
                    for (int c = 0; c < Cells; c++)
                    {
                        bool open = ((m >> c) & 1) != 0;
                        if (!open || InBlock(u, c, m, openF)) continue;
                        m &= ~(1ul << c);
                        work[u] = m;
                        changed = true;
                    }
                }
                if (!changed) break;
            }

            // Each pass snaps every block with a failing cell; a uniform aligned block never fails again, so the passes
            // end (at most one per aligned block in the region).
            for (int guard = 0; guard < 64 * 16; guard++)
            {
                bool snapped = false;
                foreach (int u in region)
                {
                    if (!Partial(u)) continue;
                    for (int c = 0; c < Cells; c++)
                    {
                        ulong wu = W(u);
                        bool open = ((wu >> c) & 1) != 0;
                        if (InBlock(u, c, open ? wu : ~wu, open ? openF : solidF)) continue;
                        int bi = (c % N) & ~1, bj = (c / N) & ~1;
                        ulong block = (1ul << (bi + N * bj)) | (1ul << (bi + 1 + N * bj)) | (1ul << (bi + N * (bj + 1))) | (1ul << (bi + 1 + N * (bj + 1)));
                        int opens = PopCount(W(u) & block);
                        // A block with a locked (just filled) cell snaps to standing.
                        bool toOpen = (LockOf(u) & block) == 0 && (opens > 2 || (opens == 2 && tieOpen));
                        work[u] = toOpen ? W(u) | block : W(u) & ~block;
                        snapped = true;
                        if (!Partial(u)) break;
                    }
                }
                if (!snapped) break;
            }

            var visited = new HashSet<long>();
            foreach (int u in region)
            {
                if (!Partial(u)) continue;
                int minCells = MinHoleCells(u);
                for (int c = 0; c < Cells; c++)
                {
                    if (((W(u) >> c) & 1) == 0 || visited.Contains((long)u * 64 + c)) continue;
                    var comp = new List<long>();
                    var q = new Queue<long>();
                    q.Enqueue((long)u * 64 + c);
                    visited.Add((long)u * 64 + c);
                    // The whole opening (through holed tiles; a broken tile or open air next to it anchors it).
                    bool anchored = false;
                    while (q.Count > 0)
                    {
                        long k = q.Dequeue();
                        comp.Add(k);
                        int ku = (int)(k / 64), kc = (int)(k % 64);
                        if (!canChange(ku)) anchored = true; // part of an opening we may not touch
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int v = Step(ku, kc, dir, out int cv);
                            if (v == BeyondAir || (v >= 0 && (broken(v) || W(v) == Brush.AllCells))) { anchored = true; continue; }
                            if (v < 0 || !Open(v, cv)) continue;
                            long kv = (long)v * 64 + cv;
                            if (visited.Add(kv)) q.Enqueue(kv);
                        }
                    }
                    if (anchored || comp.Count >= minCells) continue;
                    foreach (long k in comp)
                    {
                        int ku = (int)(k / 64), kc = (int)(k % 64);
                        work[ku] = W(ku) & ~(1ul << kc);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ filling

        /// <summary>
        /// Cells came back (a heal, a reinforce, a rebuild): <paramref name="work"/> holds the open masks the changed
        /// tiles will have. Openings near them that are now thinner than a 2 x 2 block or smaller than a minimum hole
        /// (and not part of a broken tile or open air) fill in too: their masks are added to <paramref name="work"/>.
        /// Cells in <paramref name="locked"/> (what was just filled) stay standing. Changes nothing itself.
        /// </summary>
        private void PlanFill(Dictionary<int, ulong> work, Dictionary<int, ulong> locked)
        {
            EnsureEdges();
            _beyond.Clear();
            var region = new List<int>();
            var seen = new HashSet<int>();
            var frontier = new List<int>();
            var start = new List<int>(work.Keys);
            start.Sort();
            foreach (int u in start) if (seen.Add(u)) { region.Add(u); frontier.Add(u); }
            for (int depth = 0; depth < 3; depth++)
            {
                var next = new List<int>();
                foreach (int u in frontier)
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int v = _edge[u * 4 + dir];
                        if (v >= 0 && seen.Add(v)) { region.Add(v); next.Add(v); }
                    }
                frontier = next;
            }
            region.Sort();
            // A tile counts as broken (anchoring the openings beside it) if it is broken now and stays all open.
            bool Broken(int u) => u >= 0 && (work.TryGetValue(u, out ulong m) ? m == Brush.AllCells : _tiles[u].State == TileState.Broken);
            _lock = locked;
            try { Regularise(work, region, u => !Broken(u), Broken, false); }
            finally { _lock = null; }
        }

        /// <summary>Cells a tidy may not change (a heal's filled cells), or null.</summary>
        private Dictionary<int, ulong> _lock;

        private ulong LockOf(int u) => _lock != null && _lock.TryGetValue(u, out ulong m) ? m : 0ul;

        /// <summary>
        /// After a rebuild or a state write brought cells back at <paramref name="tile"/>: openings near it that became
        /// too thin or too small fill in (as plain wall for a rebuild, else as the tile's material).
        /// </summary>
        private void FillSmallHoles(int tile, TileCause cause, int by)
        {
            if (_filling || !HasCells(tile)) return;
            var work = new Dictionary<int, ulong> { [tile] = OpenCells(tile) };
            var locked = new Dictionary<int, ulong> { [tile] = ~OpenCells(tile) };
            PlanFill(work, locked);
            WriteFilled(work, cause, by, cause == TileCause.Heal ? HealKind.Heal : cause == TileCause.Reinforce ? (HealKind?)HealKind.Reinforce : null);
        }

        /// <summary>
        /// Writes the masks of <paramref name="work"/>: cells that closed come back as patch (heal), steel (reinforce) or
        /// plain wall (null), cells that opened lose their health. Fills never chain.
        /// </summary>
        private void WriteFilled(Dictionary<int, ulong> work, TileCause cause, int by, HealKind? kind, Dictionary<int, ulong> extra = null)
        {
            var keys = new List<int>(work.Keys);
            if (extra != null) foreach (int u in extra.Keys) if (!work.ContainsKey(u)) keys.Add(u);
            keys.Sort();
            _filling = true;
            BeginShapes();
            try
            {
                var hp = new byte[Cells];
                foreach (int u in keys)
                {
                    ulong was = OpenCells(u), now = work.TryGetValue(u, out ulong wm) ? wm : was;
                    ulong ex = extra != null && extra.TryGetValue(u, out ulong em) ? em : 0;
                    if (was == now && ex == 0) continue;
                    WallTile t = _tiles[u];
                    ulong closed = (was & ~now) | (ex & ~now);
                    ulong patch = t.Patch, steel = t.Steel;
                    for (int c = 0; c < Cells; c++) hp[c] = _hp[u * Cells + c];
                    for (int c = 0; c < Cells; c++)
                    {
                        ulong bit = 1ul << c;
                        if ((closed & bit) == 0) continue;
                        bool p = kind == HealKind.Heal, s = kind == HealKind.Reinforce;
                        patch = p ? patch | bit : patch & ~bit;
                        steel = s ? steel | bit : steel & ~bit;
                        hp[c] = (byte)Config.MaxCellHealth(p, s);
                    }
                    WriteCells(u, now, patch, steel, hp, cause, by);
                }
            }
            finally { _filling = false; EndShapes(); }
        }
    }
}
