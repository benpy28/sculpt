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
            int r = World.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.02, 0.02, 0.02), p + new Vec3d(0.02, 0.02, 0.02))) ? BeyondSolid : BeyondAir;
            _beyond[key] = r;
            return r;
        }

        private readonly Dictionary<long, int> _beyond = new Dictionary<long, int>();

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

        /// <summary>
        /// Tile <paramref name="tile"/> has just been broken by a hit at <paramref name="at"/>: blow out the cells within
        /// <paramref name="radius"/> (rough edge), keeping the rules in the class summary, then write every tile that
        /// changed. The broken tile itself keeps what is left of it as a cracked tile with health for what is left.
        /// </summary>
        private void Blast(int tile, Vec3d at, double radius, TileCause cause, int by)
        {
            EnsureEdges();
            _beyond.Clear();
            var work = new Dictionary<int, ulong>();
            ulong W(int u) => work.TryGetValue(u, out ulong m) ? m : OpenCells(u);
            bool Open(int u, int c) => u == BeyondAir || (u >= 0 && ((W(u) >> c) & 1) != 0);

            // The tiles the disc can reach, through the grid edges.
            var region = new List<int>();
            var seen = new HashSet<int> { tile };
            var queue = new Queue<int>();
            queue.Enqueue(tile);
            double reach = radius + Config.BlastJitter + 0.3;
            while (queue.Count > 0 && region.Count < 128)
            {
                int u = queue.Dequeue();
                region.Add(u);
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = _edge[u * 4 + dir];
                    if (v < 0 || !seen.Add(v)) continue;
                    if ((BoundsOf(v).ClosestPoint(at) - at).Length <= reach) queue.Enqueue(v);
                }
            }
            region.Sort();
            bool CanLose(int u) => u == tile || (_tiles[u].State != TileState.Broken && _tiles[u].State != TileState.Reinforced);

            int minCells = MinHoleCells(tile);
            var blow = new HashSet<long>();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double r = radius + attempt * 0.12;
                var cand = new HashSet<long>();
                foreach (int u in region)
                {
                    if (!CanLose(u)) continue;
                    Brush b = World[_tiles[u].Brush];
                    int thin = ThinAxis(b);
                    double half = (Ax(b.Bounds.Max, thin) - Ax(b.Bounds.Min, thin)) * 0.5;
                    ulong open = W(u);
                    for (int c = 0; c < Cells; c++)
                    {
                        if (((open >> c) & 1) != 0) continue;
                        Vec3d cc = b.CellBox(c).Center;
                        // Distance in the face, plus how far the hit is off the face (a blast in front of a wall).
                        double d1 = Ax(cc, b.CellA1) - Ax(at, b.CellA1), d2 = Ax(cc, b.CellA2) - Ax(at, b.CellA2);
                        double dn = Math.Max(0, Math.Abs(Ax(cc, thin) - Ax(at, thin)) - half);
                        double reachHere = r + Rough(cc) * Config.BlastJitter;
                        if (d1 * d1 + d2 * d2 + dn * dn <= reachHere * reachHere) cand.Add((long)u * 64 + c);
                    }
                }
                bool InCand(int u, int c) => u == BeyondAir || (u >= 0 && (cand.Contains((long)u * 64 + c) || ((W(u) >> c) & 1) != 0));
                blow.Clear();
                Func<int, int, bool> inCand = InCand;
                foreach (long k in cand)
                    if (InBlock((int)(k / 64), (int)(k % 64), inCand)) blow.Add(k);
                if (blow.Count >= minCells) break;
                if (blow.Count > 0 && TouchesOpening(blow, Open)) break;
            }
            foreach (long k in blow)
            {
                int u = (int)(k / 64), c = (int)(k % 64);
                work[u] = W(u) | (1ul << c);
            }

            // Tidy it up: no slivers, no slits, no pixel holes (see Regularise). The ring of tiles around the disc is
            // checked too, since a 2 x 2 block can straddle a tile border.
            var tidy = new List<int>(region);
            foreach (int u in region)
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = _edge[u * 4 + dir];
                    if (v >= 0 && !tidy.Contains(v)) tidy.Add(v);
                }
            tidy.Sort();
            // A tile with too little left goes whole, which can leave thin bits beside it: tidy again until stable.
            for (int round = 0; round < 16; round++)
            {
                Regularise(work, tidy, u => CanLose(u) && (u == tile || _tiles[u].State != TileState.Broken), u => u != tile && _tiles[u].State == TileState.Broken, true);
                bool more = false;
                foreach (int u in tidy)
                {
                    if (!work.TryGetValue(u, out ulong m) || m == Brush.AllCells) continue;
                    int left = Cells - PopCount(m);
                    if (left > 0 && left >= Config.BreakBelowFraction * Cells) continue;
                    work[u] = Brush.AllCells;
                    more = true;
                }
                if (!more) break;
            }

            // Write it: each changed tile keeps health for the share of it that is left.
            bool wroteTile = false;
            foreach (int u in tidy)
            {
                if (!work.TryGetValue(u, out ulong m)) continue;
                ulong old = OpenCells(u);
                if (m == old && u != tile) continue;
                WallTile t = _tiles[u];
                int solid = Cells - PopCount(m);
                if (solid == 0 || solid < Config.BreakBelowFraction * Cells)
                {
                    Set(u, TileState.Broken, 0, cause, by);
                    if (u == tile) wroteTile = true;
                    continue;
                }
                if (u == tile)
                {
                    TileState st = t.State == TileState.Intact || t.State == TileState.Damaged ? TileState.Damaged : t.State;
                    int h = Math.Max(1, (int)Math.Round(Config.MaxHealthOf(st) * Config.DamagedBelowFraction * solid / Cells));
                    Set(u, st, h, cause, by, m);
                    wroteTile = true;
                }
                else
                {
                    int oldSolid = Math.Max(1, Cells - PopCount(old));
                    int h = Math.Max(1, (int)Math.Round(t.Health * (double)solid / oldSolid));
                    TileState st = t.State;
                    if ((st == TileState.Intact || st == TileState.Damaged) && h < Config.IntactHealth * Config.DamagedBelowFraction) st = TileState.Damaged;
                    Set(u, st, h, cause, by, m);
                }
            }
            if (!wroteTile)
            {
                // The disc took nothing new from the tile (its cells were already open around the hit): it still took a
                // breaking hit, so it is left cracked with health for what is left of it.
                WallTile t = _tiles[tile];
                int solid = Cells - PopCount(t.Open);
                TileState st = t.State == TileState.Intact || t.State == TileState.Damaged ? TileState.Damaged : t.State;
                Set(tile, st, Math.Max(1, (int)Math.Round(Config.MaxHealthOf(st) * Config.DamagedBelowFraction * solid / Cells)), cause, by, t.Open);
            }
        }

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
                    ulong m = W(u);
                    for (int c = 0; c < Cells; c++)
                    {
                        bool open = ((m >> c) & 1) != 0;
                        if (open || InBlock(u, c, solidF)) continue;
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
                        if (!open || InBlock(u, c, openF)) continue;
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
                        bool open = ((W(u) >> c) & 1) != 0;
                        if (InBlock(u, c, open ? openF : solidF)) continue;
                        int bi = (c % N) & ~1, bj = (c / N) & ~1;
                        ulong block = (1ul << (bi + N * bj)) | (1ul << (bi + 1 + N * bj)) | (1ul << (bi + N * (bj + 1))) | (1ul << (bi + 1 + N * (bj + 1)));
                        int opens = PopCount(W(u) & block);
                        bool toOpen = opens > 2 || (opens == 2 && tieOpen);
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
        /// The cells that would fill in around <paramref name="tile"/> if it came back whole now (openings next to it
        /// that would be too thin or too small): a heal checks nobody is standing in them.
        /// </summary>
        public void CellsFilledByRestoring(int tile, List<Aabb> into)
        {
            into.Clear();
            if (!HasCells(tile)) return;
            FillSmallHoles(tile, TileCause.Server, 0, into);
        }

        /// <summary>
        /// After cells came back at <paramref name="tile"/>: openings near it that are now thinner than a 2 x 2 block or
        /// smaller than a minimum hole (and not part of a broken tile or open air) fill in. With <paramref name="dryRun"/>
        /// nothing changes: the cells that would fill go into it, as if the tile were already whole.
        /// </summary>
        private void FillSmallHoles(int tile, TileCause cause, int by, List<Aabb> dryRun)
        {
            if (_filling || !HasCells(tile)) return;
            EnsureEdges();
            _beyond.Clear();
            var work = new Dictionary<int, ulong>();
            if (dryRun != null) work[tile] = 0;
            bool Broken(int u) => u >= 0 && (dryRun == null || u != tile) && _tiles[u].State == TileState.Broken;

            var region = new List<int>();
            var seen = new HashSet<int> { tile };
            var frontier = new List<int> { tile };
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

            Regularise(work, region, u => u != tile && !Broken(u) && _tiles[u].State != TileState.Reinforced, Broken, false);

            var keys = new List<int>(work.Keys);
            keys.Sort();
            if (dryRun != null)
            {
                foreach (int u in keys)
                {
                    if (u == tile) continue;
                    ulong filled = OpenCells(u) & ~work[u];
                    for (int c = 0; c < Cells; c++)
                        if (((filled >> c) & 1) != 0) dryRun.Add(CellBox(u, c));
                }
                return;
            }
            _filling = true;
            try
            {
                foreach (int u in keys)
                {
                    WallTile t = _tiles[u];
                    if (t.State == TileState.Broken || work[u] == t.Open) continue;
                    Set(u, t.State, t.Health, cause, by, work[u]);
                }
            }
            finally { _filling = false; }
        }
    }
}
