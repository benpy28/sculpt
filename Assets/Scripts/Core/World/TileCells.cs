using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// How one quarter of a cell is filled (a cell's corner region: from its centre to one of its corners). Hole edges
    /// are cut on the diagonal where two sides of a corner are open, so a hole never reads as stair steps (Ben
    /// 2026-11-25: "remove the grid"). In local coordinates u, v (0 at the cell centre, 1 at the corner):
    /// Inner = solid where u + v &lt;= 1 (a standing cell's corner cut off), Outer = solid where u + v &gt;= 1 (an open
    /// cell's corner filled in between two standing neighbours).
    /// </summary>
    public enum QuadFill : byte
    {
        Empty = 0,
        Full = 1,
        Inner = 2,
        Outer = 3,
    }

    /// <summary>
    /// Damage, healing and the exact shape of wall and floor tiles by cell (Ben 2026-11-25: cracks and holes by area,
    /// not by tile; heals by area). Every wall and floor tile is an 8 x 8 grid of cells; each standing cell has its own
    /// health and material (plain, patched, reinforced). A hit damages every cell within its radius, across tile borders,
    /// so where you hit is all that counts and tile edges never show. Tile state and health are a summary of the cells.
    /// The fairness rules of <see cref="TileWalls"/> (TileHoles.cs) still hold after every change. Deterministic.
    /// </summary>
    public sealed partial class TileWalls
    {
        private byte[] _hp = new byte[0];
        private int _shapeDepth;
        private readonly HashSet<int> _shapeDirty = new HashSet<int>();

        private void GrowCells(int id)
        {
            int need = (id + 1) * Cells;
            if (_hp.Length < need) Array.Resize(ref _hp, Math.Max(need, _hp.Length * 2));
            byte full = (byte)Config.MaxCellHealth(false, false);
            for (int c = 0; c < Cells; c++) _hp[id * Cells + c] = full;
        }

        /// <summary>Health of one cell of a celled tile (0 when it is open).</summary>
        public int CellHealth(int tile, int cell) => _tiles[tile].State == TileState.Broken ? 0 : _hp[tile * Cells + cell];

        /// <summary>The most health this cell can have (its material's).</summary>
        public int CellMaxHealth(int tile, int cell)
        {
            WallTile t = _tiles[tile];
            ulong bit = 1ul << cell;
            return Config.MaxCellHealth((t.Patch & bit) != 0, (t.Steel & bit) != 0);
        }

        public bool CellOpen(int tile, int cell) => ((OpenCells(tile) >> cell) & 1) != 0;

        /// <summary>A copy of every cell's health of a celled tile (64 bytes).</summary>
        public byte[] CellHealths(int tile)
        {
            var a = new byte[Cells];
            if (_tiles[tile].State != TileState.Broken) Array.Copy(_hp, tile * Cells, a, 0, Cells);
            return a;
        }

        /// <summary>The cell of a celled tile nearest its middle (the one used when a press names no cell).</summary>
        public const int MiddleCell = (N / 2 - 1) + N * (N / 2 - 1);

        /// <summary>The cell of a celled tile under a point on (or near) it.</summary>
        public int CellAt(int tile, Vec3d p)
        {
            Brush b = World[_tiles[tile].Brush];
            if (!b.HasCells) return 0;
            double u = (Ax(p, b.CellA1) - Ax(b.Bounds.Min, b.CellA1)) / (Ax(b.Bounds.Max, b.CellA1) - Ax(b.Bounds.Min, b.CellA1));
            double v = (Ax(p, b.CellA2) - Ax(b.Bounds.Min, b.CellA2)) / (Ax(b.Bounds.Max, b.CellA2) - Ax(b.Bounds.Min, b.CellA2));
            int i = Math.Max(0, Math.Min(N - 1, (int)Math.Floor(u * N)));
            int j = Math.Max(0, Math.Min(N - 1, (int)Math.Floor(v * N)));
            return i + N * j;
        }

        /// <summary>A number shared by every celled tile on the same face plane (the axis it is thin in and where).</summary>
        public int PlaneOf(int tile)
        {
            Brush b = World[_tiles[tile].Brush];
            int thin = ThinAxis(b);
            long k = thin * 2000003L + (long)Math.Round(Ax(b.Bounds.Min, thin) * 1000) + 1000000000L;
            return (int)(k & 0x3FFFFFFF);
        }

        // ------------------------------------------------------------------ the one writer of cells

        /// <summary>
        /// THE one function that changes a celled tile (walls and floors; <see cref="Set"/> comes here for them too):
        /// which cells are open, which are patched or reinforced, and every cell's health. Open cells have no health and
        /// no material; standing cells keep at least 1 and at most their material's maximum. All open = Broken. The tile's
        /// state and health become a summary (see <see cref="Summarise"/>), collision follows the cells exactly, and the
        /// change is logged. No-op if nothing differs.
        /// </summary>
        public void WriteCells(int tile, ulong open, ulong patch, ulong steel, byte[] hp, TileCause cause, int by = 0)
        {
            WallTile t = _tiles[tile];
            if (!HasCells(tile)) throw new ArgumentException("Only wall and floor tiles have cells.", nameof(tile));
            bool broken = open == Brush.AllCells;
            patch &= ~open;
            steel &= ~open;
            patch &= ~steel;
            ulong was = OpenCells(tile);
            bool same = was == open && t.Patch == patch && t.Steel == steel;
            var nh = _normScratch ??= new byte[Cells];
            for (int c = 0; c < Cells; c++)
            {
                ulong bit = 1ul << c;
                int v;
                if ((open & bit) != 0) v = 0;
                else
                {
                    int max = Config.MaxCellHealth((patch & bit) != 0, (steel & bit) != 0);
                    v = hp == null ? max : hp[c];
                    v = Math.Max(1, Math.Min(max, v));
                }
                nh[c] = (byte)v;
                if (same && v != (t.State == TileState.Broken ? 0 : _hp[tile * Cells + c])) same = false;
            }
            if (same) return;
            TileState from = t.State;
            Array.Copy(nh, 0, _hp, tile * Cells, Cells);
            Summarise(tile, open, patch, steel, out TileState state, out int health);
            t.State = state;
            t.Health = health;
            t.Open = broken ? 0ul : open;
            t.Patch = patch;
            t.Steel = steel;
            if (cause != TileCause.Built) t.Changed = true;
            _tiles[tile] = t;
            if ((from == TileState.Broken) != broken) World.SetSolid(t.Brush, !broken);
            World.SetShape(t.Brush, t.Open, World[t.Brush].Solids); // the exact shape follows in ShapeAround
            if (cause == TileCause.Charge && (open & ~was) != 0) _chargedAt[tile] = Tick;
            else if ((was & ~open) != 0 || cause == TileCause.Built) _chargedAt.Remove(tile);
            byte[] copy = new byte[Cells];
            Array.Copy(nh, copy, Cells);
            Changes.Add(new TileChange
            {
                Tick = Tick, Tile = tile, From = from, To = state, Health = health, Cause = cause, By = by,
                Open = t.Open, Patch = patch, Steel = steel, Hp = copy,
            });
            Revision++;
            _beyond.Clear(); // what lies past a tile grid's edge may have changed (frame follows tiles)
            if ((from == TileState.Broken) != broken) Propagate(tile, cause, by);
            ShapeAround(tile);
            // Cells came back (a rebuild, a state write): openings next to it that are now too small or too thin go too.
            if ((was & ~open) != 0 && !_filling && !CopyOnly && (cause == TileCause.Heal || cause == TileCause.Reinforce || cause == TileCause.Built))
                FillSmallHoles(tile, cause, by);
        }

        [ThreadStatic] private static byte[] _normScratch;

        /// <summary>A client's copy: it only copies the server's changes (the server already sent the fills a heal or a
        /// rebuild caused), so a write never fills holes by itself.</summary>
        public bool CopyOnly;

        /// <summary>
        /// A celled tile's state and health from its cells: Broken when all are open; Reinforced or Patched when every
        /// standing cell is; else Damaged when it has a hole or a plain cell below half health, else Intact. Health is the
        /// cells' health averaged over the whole tile (a holed tile has less).
        /// </summary>
        private void Summarise(int tile, ulong open, ulong patch, ulong steel, out TileState state, out int health)
        {
            if (open == Brush.AllCells) { state = TileState.Broken; health = 0; return; }
            ulong standing = ~open;
            int sum = 0;
            bool cracked = false;
            double crackBelow = Config.IntactHealth * Config.DamagedBelowFraction;
            for (int c = 0; c < Cells; c++)
            {
                ulong bit = 1ul << c;
                if ((open & bit) != 0) continue;
                int v = _hp[tile * Cells + c];
                sum += v;
                if ((patch & bit) == 0 && (steel & bit) == 0 && v < crackBelow) cracked = true;
            }
            if ((steel & standing) == standing) state = TileState.Reinforced;
            else if ((patch & standing) == standing) state = TileState.Patched;
            else state = open != 0 || cracked ? TileState.Damaged : TileState.Intact;
            health = Math.Max(1, (int)Math.Round(sum / (double)Cells));
        }

        // ------------------------------------------------------------------ exact shape

        private void BeginShapes() => _shapeDepth++;

        private void EndShapes()
        {
            if (--_shapeDepth > 0) return;
            _shapeDepth = 0;
            if (_shapeDirty.Count == 0) return;
            var list = new List<int>(_shapeDirty);
            _shapeDirty.Clear();
            list.Sort();
            foreach (int u in list) RebuildShape(u);
        }

        /// <summary>A tile's cells changed: its shape, and the shapes of the 8 tiles around it, are rebuilt.</summary>
        private void ShapeAround(int tile)
        {
            EnsureEdges();
            if (HasCells(tile)) _shapeDirty.Add(tile);
            for (int d = 0; d < 4; d++)
            {
                int n = _edge[tile * 4 + d];
                if (n < 0) continue;
                _shapeDirty.Add(n);
                for (int e = d < 2 ? 2 : 0; e < (d < 2 ? 4 : 2); e++)
                {
                    int m = _edge[n * 4 + e];
                    if (m >= 0) _shapeDirty.Add(m);
                }
            }
            if (!HasCells(tile))
            {
                // A plain tile (frame) changed: celled tiles beside it see it past their edge.
                var near = new List<int>();
                Aabb b = BoundsOf(tile);
                TilesNear(new Aabb(b.Min - new Vec3d(0.05, 0.05, 0.05), b.Max + new Vec3d(0.05, 0.05, 0.05)), near);
                foreach (int u in near) if (HasCells(u)) _shapeDirty.Add(u);
            }
            if (_shapeDepth == 0)
            {
                _shapeDepth = 1;
                EndShapes();
            }
        }

        private void RebuildShape(int tile)
        {
            if (!HasCells(tile)) return;
            WallTile t = _tiles[tile];
            if (t.State == TileState.Broken) { World.SetShape(t.Brush, 0, null); return; }
            if (t.Open == 0 && NothingOpenAround(tile)) { World.SetShape(t.Brush, 0, null); return; } // cracks only: the whole box
            var fills = _fillScratch ??= new QuadFill[Quads * Quads];
            bool all = true;
            for (int qj = 0; qj < Quads; qj++)
                for (int qi = 0; qi < Quads; qi++)
                {
                    QuadFill f = QuadOf(tile, qi, qj);
                    fills[qi + Quads * qj] = f;
                    if (f != QuadFill.Full) all = false;
                }
            World.SetShape(t.Brush, t.Open, all ? null : Rasterise(World[t.Brush], fills));
        }

        [ThreadStatic] private static QuadFill[] _fillScratch;

        /// <summary>No open cell in the 8 tiles of the same grid around (so none of this whole tile's corners are cut).</summary>
        private bool NothingOpenAround(int tile)
        {
            for (int d = 0; d < 4; d++)
            {
                int n = _edge[tile * 4 + d];
                if (n < 0) continue;
                if (OpenCells(n) != 0) return false;
                for (int e = d < 2 ? 2 : 0; e < (d < 2 ? 4 : 2); e++)
                {
                    int m = _edge[n * 4 + e];
                    if (m >= 0 && OpenCells(m) != 0) return false;
                }
            }
            return true;
        }

        /// <summary>Quarter cells along each side of a celled tile.</summary>
        public const int Quads = N * 2;

        private bool SolidCell(int u, int c) => u == BeyondSolid || (u >= 0 && ((OpenCells(u) >> c) & 1) == 0);

        /// <summary>
        /// How quarter cell (<paramref name="qi"/>, <paramref name="qj"/>) of a celled tile is filled (both 0..15 along
        /// the tile's first and second face axes). This is the one shape: collision and bullets use it (rasterised to
        /// within 2.4 cm, see <see cref="Rasterise"/>) and the game draws it exactly. Built openings (doorways, the edge
        /// of a wall) keep square edges: past the tile grid's edge counts as the same as the cell itself.
        /// </summary>
        public QuadFill QuadOf(int tile, int qi, int qj)
        {
            EnsureEdges();
            int cell = (qi >> 1) + N * (qj >> 1);
            int dx = qi & 1, dy = qj & 1;
            if (_tiles[tile].State == TileState.Broken) return QuadFill.Empty;
            bool s = SolidCell(tile, cell);
            int di = dx == 1 ? 0 : 1, dj = dy == 1 ? 2 : 3;
            int ta = Step(tile, cell, di, out int ca);
            int tb = Step(tile, cell, dj, out int cb);
            bool a = ta == BeyondAir ? s : SolidCell(ta, ca);
            bool b = tb == BeyondAir ? s : SolidCell(tb, cb);
            bool d;
            if (ta >= 0) { int td = Step(ta, ca, dj, out int cd); d = td == BeyondAir ? a : SolidCell(td, cd); }
            else if (tb >= 0) { int td = Step(tb, cb, di, out int cd); d = td == BeyondAir ? b : SolidCell(td, cd); }
            else d = ta == BeyondSolid || tb == BeyondSolid || s;
            if (s) return !a && !b && !d ? QuadFill.Inner : QuadFill.Full;
            return a && b ? QuadFill.Outer : QuadFill.Empty;
        }

        /// <summary>Is a point of a celled tile's face (u, v in quarter cells, 0..16) solid in the exact shape?</summary>
        public static bool SolidAt(QuadFill f, int qi, int qj, double x, double y)
        {
            if (f == QuadFill.Full) return true;
            if (f == QuadFill.Empty) return false;
            double u = (qi & 1) == 1 ? x - qi : qi + 1 - x;
            double v = (qj & 1) == 1 ? y - qj : qj + 1 - y;
            return f == QuadFill.Inner ? u + v <= 1 : u + v >= 1;
        }

        /// <summary>
        /// The solid quarter cells as boxes: 32 rows (half a quarter cell each) along the second face axis, each row's
        /// diagonal edges taken at the row's middle, so the boxes are within a quarter of a quarter cell (2.4 cm on a
        /// 1.5 m tile) of the drawn shape. Equal rows merge.
        /// </summary>
        private static Aabb[] Rasterise(in Brush b, QuadFill[] fills)
        {
            int a1 = b.CellA1, a2 = b.CellA2;
            double lo1 = Ax(b.Bounds.Min, a1), hi1 = Ax(b.Bounds.Max, a1), lo2 = Ax(b.Bounds.Min, a2), hi2 = Ax(b.Bounds.Max, a2);
            const int Rows = Quads * 2;
            var boxes = new List<Aabb>(16);
            var prev = new List<double>(8);
            var cur = new List<double>(8);
            int prevStart = 0;
            for (int r = 0; r <= Rows; r++)
            {
                cur.Clear();
                if (r < Rows)
                {
                    int qj = r >> 1;
                    double ym = (r + 0.5) * 0.5;
                    double v = (qj & 1) == 1 ? ym - qj : qj + 1 - ym;
                    for (int qi = 0; qi < Quads; qi++)
                    {
                        QuadFill f = fills[qi + Quads * qj];
                        double ua, ub;
                        if (f == QuadFill.Empty) continue;
                        if (f == QuadFill.Full) { ua = 0; ub = 1; }
                        else if (f == QuadFill.Inner) { ua = 0; ub = 1 - v; }
                        else { ua = 1 - v; ub = 1; }
                        double x0, x1;
                        if ((qi & 1) == 1) { x0 = qi + ua; x1 = qi + ub; }
                        else { x0 = qi + 1 - ub; x1 = qi + 1 - ua; }
                        if (cur.Count > 0 && x0 <= cur[cur.Count - 1] + 1e-9) cur[cur.Count - 1] = Math.Max(cur[cur.Count - 1], x1);
                        else { cur.Add(x0); cur.Add(x1); }
                    }
                }
                bool equal = r > 0 && r < Rows && cur.Count == prev.Count;
                if (equal)
                    for (int k = 0; k < cur.Count; k++)
                        if (Math.Abs(cur[k] - prev[k]) > 1e-9) { equal = false; break; }
                if (equal) continue;
                if (r > 0)
                    for (int k = 0; k < prev.Count; k += 2)
                    {
                        double p0 = prev[k] <= 1e-9 ? lo1 : prev[k] >= Quads - 1e-9 ? hi1 : lo1 + (hi1 - lo1) * prev[k] / Quads;
                        double p1 = prev[k + 1] <= 1e-9 ? lo1 : prev[k + 1] >= Quads - 1e-9 ? hi1 : lo1 + (hi1 - lo1) * prev[k + 1] / Quads;
                        double r0 = prevStart == 0 ? lo2 : lo2 + (hi2 - lo2) * prevStart / Rows;
                        double r1 = r == Rows ? hi2 : lo2 + (hi2 - lo2) * r / Rows;
                        if (p1 - p0 < 1e-9 || r1 - r0 < 1e-9) continue;
                        Vec3d mn = WithAx(WithAx(b.Bounds.Min, a1, p0), a2, r0), mx = WithAx(WithAx(b.Bounds.Max, a1, p1), a2, r1);
                        boxes.Add(new Aabb(mn, mx));
                    }
                prev.Clear();
                prev.AddRange(cur);
                prevStart = r;
            }
            return boxes.ToArray();
        }

        // ------------------------------------------------------------------ damage by area

        /// <summary>The celled tiles reachable from <paramref name="tile"/> across grid edges whose bounds come within
        /// <paramref name="reach"/> of <paramref name="at"/> (broken ones included), sorted.</summary>
        private List<int> Region(int tile, Vec3d at, double reach)
        {
            var region = new List<int>();
            var seen = new HashSet<int> { tile };
            var queue = new Queue<int>();
            queue.Enqueue(tile);
            while (queue.Count > 0 && region.Count < 256)
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
            return region;
        }

        /// <summary>Whole-tile damage of a celled tile (a hit that names no spot): every standing cell loses it.</summary>
        private bool DamageWholeCells(int tile, int amount, TileCause cause, int by)
        {
            WallTile t = _tiles[tile];
            byte[] hp = CellHealths(tile);
            ulong open = t.Open;
            bool all = true;
            for (int c = 0; c < Cells; c++)
            {
                if (((open >> c) & 1) != 0) continue;
                int v = hp[c] - amount;
                if (v > 0) all = false;
                hp[c] = (byte)Math.Max(1, v);
            }
            if (all) { WriteCells(tile, Brush.AllCells, 0, 0, null, cause, by); return true; }
            WriteCells(tile, open, t.Patch, t.Steel, hp, cause, by);
            return false;
        }

        /// <summary>
        /// A hit at <paramref name="at"/> (on or in front of a wall or floor; a blast can be off the face): every standing
        /// cell within <paramref name="radius"/> (a rough edge) of the tiles reachable from <paramref name="start"/> loses
        /// <paramref name="damage"/>. Cells at 0 open, keeping the fairness rules: only cells in a 2 x 2 block of open or
        /// zeroed cells go (the rest stay at 1 health, badly cracked); a new hole smaller than half a body grows into its
        /// most damaged neighbours until it is big enough; slivers crumble; a tile with too little left goes whole.
        /// Returns true if anything opened.
        /// </summary>
        public bool HitArea(int start, Vec3d at, int damage, double radius, TileCause cause, int by, double jitter = -1)
        {
            if (damage <= 0 || start < 0 || !HasCells(start)) return false;
            EnsureEdges();
            _beyond.Clear();
            if (jitter < 0) jitter = Config.HitJitter;
            List<int> region = Region(start, at, radius + jitter + 0.3);
            var hp = new Dictionary<int, byte[]>();
            var zero = new HashSet<long>();
            foreach (int u in region)
            {
                WallTile t = _tiles[u];
                if (t.State == TileState.Broken) continue;
                Brush b = World[t.Brush];
                int thin = ThinAxis(b);
                double half = (Ax(b.Bounds.Max, thin) - Ax(b.Bounds.Min, thin)) * 0.5;
                for (int c = 0; c < Cells; c++)
                {
                    if (((t.Open >> c) & 1) != 0) continue;
                    Vec3d cc = b.CellBox(c).Center;
                    double d1 = Ax(cc, b.CellA1) - Ax(at, b.CellA1), d2 = Ax(cc, b.CellA2) - Ax(at, b.CellA2);
                    double dn = Math.Max(0, Math.Abs(Ax(cc, thin) - Ax(at, thin)) - half);
                    double reach = radius + Rough(cc) * jitter;
                    if (d1 * d1 + d2 * d2 + dn * dn > reach * reach) continue;
                    if (!hp.TryGetValue(u, out byte[] arr)) hp[u] = arr = CellHealths(u);
                    int v = arr[c] - damage;
                    if (v <= 0) { v = 0; zero.Add((long)u * 64 + c); }
                    arr[c] = (byte)v;
                }
            }
            if (hp.Count == 0) return false;

            var work = new Dictionary<int, ulong>();
            ulong W(int u) => work.TryGetValue(u, out ulong m) ? m : OpenCells(u);
            bool Open(int u, int c) => u == BeyondAir || (u >= 0 && ((W(u) >> c) & 1) != 0);
            bool Cand(int u, int c) => u == BeyondAir || (u >= 0 && (zero.Contains((long)u * 64 + c) || ((W(u) >> c) & 1) != 0));
            Func<int, int, bool> cand = Cand, openF = Open;
            var blow = new HashSet<long>();
            foreach (long k in zero)
                if (InBlock((int)(k / 64), (int)(k % 64), cand)) blow.Add(k);
            if (blow.Count > 0)
            {
                int minCells = MinHoleCells(start);
                bool joins = TouchesOpening(blow, openF);
                var first = new HashSet<long>(blow);
                // The tidy can trim a just-big-enough hole below the minimum, and then it would close it: grow it a little
                // more each time until a hole survives the tidy.
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    work.Clear();
                    blow = new HashSet<long>(first);
                    int target = attempt == 0 ? minCells : Math.Max(minCells, first.Count) + 6 * attempt;
                    if (!joins && blow.Count < target) Grow(blow, target, hp, at);
                    foreach (long k in blow)
                    {
                        int u = (int)(k / 64), c = (int)(k % 64);
                        work[u] = W(u) | (1ul << c);
                    }
                    Tidy(work, region, true);
                    bool kept = false;
                    foreach (long k in first)
                        if (((W((int)(k / 64)) >> (int)(k % 64)) & 1) != 0) { kept = true; break; }
                    if (kept || joins) break;
                }
            }
            WriteHit(work, hp, cause, by);
            return blow.Count > 0;
        }

        /// <summary>A new hole too small to be fair grows into the standing cells around it, most damaged first, then
        /// nearest the hit (then by id), until it has <paramref name="minCells"/>.</summary>
        private void Grow(HashSet<long> blow, int minCells, Dictionary<int, byte[]> hp, Vec3d at)
        {
            int Hp(int u, int c) => hp.TryGetValue(u, out byte[] a) ? a[c] : _hp[u * Cells + c];
            var frontier = new HashSet<long>();
            void AddAround(long k)
            {
                int u = (int)(k / 64), c = (int)(k % 64);
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = Step(u, c, dir, out int cv);
                    if (v < 0 || _tiles[v].State == TileState.Broken || ((OpenCells(v) >> cv) & 1) != 0) continue;
                    long kv = (long)v * 64 + cv;
                    if (!blow.Contains(kv)) frontier.Add(kv);
                }
            }
            foreach (long k in blow) AddAround(k);
            while (blow.Count < minCells && frontier.Count > 0)
            {
                long best = -1;
                int bestHp = int.MaxValue;
                double bestD = double.MaxValue;
                foreach (long k in frontier)
                {
                    int u = (int)(k / 64), c = (int)(k % 64);
                    int h = Hp(u, c);
                    double d = (CellBox(u, c).Center - at).LengthSquared;
                    if (h < bestHp || (h == bestHp && (d < bestD - 1e-12 || (Math.Abs(d - bestD) <= 1e-12 && k < best))))
                    {
                        best = k; bestHp = h; bestD = d;
                    }
                }
                frontier.Remove(best);
                blow.Add(best);
                AddAround(best);
            }
        }

        /// <summary>
        /// After cells opened: no slivers, no slits, no pixel holes (see Regularise), checking the ring of tiles around the
        /// region too (a 2 x 2 block can straddle a tile border); a tile with too little left goes whole, which can leave
        /// thin bits beside it, so this repeats until stable.
        /// </summary>
        private void Tidy(Dictionary<int, ulong> work, List<int> region, bool tieOpen)
        {
            var tidy = new List<int>(region);
            foreach (int u in region)
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = _edge[u * 4 + dir];
                    if (v >= 0 && !tidy.Contains(v)) tidy.Add(v);
                }
            foreach (int u in work.Keys) if (!tidy.Contains(u)) tidy.Add(u);
            tidy.Sort();
            bool Broken(int u) => u >= 0 && _tiles[u].State == TileState.Broken;
            for (int round = 0; round < 16; round++)
            {
                Regularise(work, tidy, u => !Broken(u), Broken, tieOpen);
                bool more = false;
                var grown = new List<int>();
                foreach (int u in tidy)
                {
                    if (!work.TryGetValue(u, out ulong m) || m == Brush.AllCells) continue;
                    int left = Cells - PopCount(m);
                    if (left > 0 && left >= Config.BreakBelowFraction * Cells) continue;
                    work[u] = Brush.AllCells;
                    more = true;
                    // The tiles beside one that went whole can be left with thin bits: tidy them too.
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int v = _edge[u * 4 + dir];
                        if (v >= 0 && !tidy.Contains(v) && !grown.Contains(v)) grown.Add(v);
                    }
                }
                if (!more) break;
                if (grown.Count > 0) { tidy.AddRange(grown); tidy.Sort(); }
            }
        }

        /// <summary>Writes a hit: new health where it changed, and the open masks of <paramref name="work"/>. A cell a tidy
        /// closed again comes back at 1 health (badly cracked); a cell it opened loses its health.</summary>
        private void WriteHit(Dictionary<int, ulong> work, Dictionary<int, byte[]> hp, TileCause cause, int by)
        {
            var keys = new List<int>(hp.Keys);
            foreach (int u in work.Keys) if (!hp.ContainsKey(u)) keys.Add(u);
            keys.Sort();
            BeginShapes();
            try
            {
                foreach (int u in keys)
                {
                    WallTile t = _tiles[u];
                    ulong open = work.TryGetValue(u, out ulong m) ? m : OpenCells(u);
                    if (!hp.TryGetValue(u, out byte[] arr)) arr = CellHealths(u);
                    for (int c = 0; c < Cells; c++)
                    {
                        if (((open >> c) & 1) != 0) arr[c] = 0;
                        else if (arr[c] == 0) arr[c] = 1;
                    }
                    WriteCells(u, open, t.Patch, t.Steel, arr, cause, by);
                }
            }
            finally { EndShapes(); }
        }

        /// <summary>
        /// A blast at <paramref name="at"/> on the tiles it reaches (sorted ids, e.g. from GrenadeRules.TilesInBlast):
        /// each wall or floor face takes one hit by area of radius <paramref name="radius"/> (with the blast's rough edge),
        /// stair, ramp and frame tiles take it whole.
        /// </summary>
        public void Blast(Vec3d at, IReadOnlyList<int> tiles, int damage, double radius, TileCause cause, int by)
        {
            var covered = new HashSet<int>();
            foreach (int t in tiles)
            {
                if (_tiles[t].Links != null || _tiles[t].State == TileState.Broken && !HasCells(t)) continue;
                if (HasCells(t) && Config.BlastHoles)
                {
                    if (covered.Contains(t)) continue;
                    EnsureEdges();
                    foreach (int u in Region(t, at, radius + Config.BlastJitter + 0.3)) covered.Add(u);
                    HitArea(t, at, damage, radius, cause, by, Config.BlastJitter);
                }
                else if (_tiles[t].State != TileState.Broken) Damage(t, damage, cause, by);
            }
        }

        // ------------------------------------------------------------------ charge

        /// <summary>The standing cells of a celled tile whose boxes overlap <paramref name="box"/>.</summary>
        public ulong CellsTouching(int tile, Aabb box)
        {
            if (_tiles[tile].State == TileState.Broken) return 0;
            ulong open = OpenCells(tile), hit = 0;
            Brush b = World[_tiles[tile].Brush];
            if (!b.Bounds.Overlaps(box)) return 0;
            for (int c = 0; c < Cells; c++)
                if (((open >> c) & 1) == 0 && b.CellBox(c).Overlaps(box)) hit |= 1ul << c;
            return hit;
        }

        /// <summary>Has this cell been hit (a charge counts it)? Any damage on a plain cell; a patched cell only once it has
        /// been hit again since the heal (balance pass 2026-11-25, Ben: a fresh heal keeps a sprint out; set
        /// <see cref="BreachConfig.ChargePatchedCounts"/> for the old rule, every patched cell); never a reinforced one
        /// (Ben 2026-11-25: "sprint through low health walls").</summary>
        public bool CellWeak(int tile, int cell)
        {
            WallTile t = _tiles[tile];
            ulong bit = 1ul << cell;
            if ((t.Steel & bit) != 0) return false;
            bool patched = (t.Patch & bit) != 0;
            if (patched && Config.ChargePatchedCounts) return true;
            return _hp[tile * Cells + cell] < Config.MaxCellHealth(patched, false);
        }

        public bool CellReinforced(int tile, int cell) => ((_tiles[tile].Steel >> cell) & 1) != 0;

        /// <summary>A charge breaks these cells (per tile), then the fairness tidy runs (ties go open).</summary>
        public void ChargeOpen(IReadOnlyList<ChargeCut> cuts, int by)
        {
            if (cuts.Count == 0) return;
            EnsureEdges();
            _beyond.Clear();
            var work = new Dictionary<int, ulong>();
            var region = new List<int>();
            foreach (ChargeCut cut in cuts)
            {
                if (!HasCells(cut.Tile)) continue;
                work[cut.Tile] = (work.TryGetValue(cut.Tile, out ulong m) ? m : OpenCells(cut.Tile)) | cut.Cells;
                if (!region.Contains(cut.Tile)) region.Add(cut.Tile);
            }
            region.Sort();
            Tidy(work, region, true);
            WriteHit(work, new Dictionary<int, byte[]>(), TileCause.Charge, by);
        }

        /// <summary>Everything about a celled tile's cells, to put it back exactly (a refused predicted charge).</summary>
        public struct CellSnapshot
        {
            public ulong Open, Patch, Steel;
            public byte[] Hp;
        }

        public CellSnapshot Snapshot(int tile)
        {
            WallTile t = _tiles[tile];
            return new CellSnapshot { Open = OpenCells(tile), Patch = t.Patch, Steel = t.Steel, Hp = CellHealths(tile) };
        }

        public void Restore(int tile, in CellSnapshot s, TileCause cause) => WriteCells(tile, s.Open, s.Patch, s.Steel, s.Hp, cause);

        // ------------------------------------------------------------------ healing by area

        /// <summary>
        /// What a heal (or reinforce) aimed at <paramref name="cell"/> of <paramref name="tile"/> would change, per tile
        /// (Ben 2026-11-25): every cell within <see cref="BreachConfig.HealRadius"/> of the aimed cell (a rough edge),
        /// plus the whole hole the aimed cell is in when that hole is no bigger than <see cref="BreachConfig.SmallHoleArea"/>.
        /// A heal takes the open cells and the badly cracked ones (below patched health; never reinforced cells, that would
        /// weaken them); a reinforce takes every cell that is not full reinforced. Empty = nothing to do.
        /// </summary>
        public void HealPlan(int tile, int cell, HealKind kind, Dictionary<int, ulong> into)
        {
            into.Clear();
            if (tile < 0 || tile >= _tiles.Count || !HasCells(tile) || _tiles[tile].Links != null) return;
            cell = Math.Max(0, Math.Min(Cells - 1, cell));
            EnsureEdges();
            _beyond.Clear();
            Vec3d p = CellBox(tile, cell).Center;
            double r = Config.HealRadius;
            foreach (int u in Region(tile, p, r + Config.HitJitter + 0.3))
            {
                Brush b = World[_tiles[u].Brush];
                ulong m = 0;
                for (int c = 0; c < Cells; c++)
                {
                    Vec3d cc = b.CellBox(c).Center;
                    double d1 = Ax(cc, b.CellA1) - Ax(p, b.CellA1), d2 = Ax(cc, b.CellA2) - Ax(p, b.CellA2);
                    double reach = r + Rough(cc) * Config.HitJitter;
                    if (d1 * d1 + d2 * d2 <= reach * reach && Wants(u, c, kind)) m |= 1ul << c;
                }
                if (m != 0) into[u] = m;
            }
            if (CellOpen(tile, cell)) AddSmallHole(tile, cell, into);
        }

        private readonly Dictionary<long, bool> _healMemo = new Dictionary<long, bool>();
        private readonly Dictionary<int, ulong> _memoPlan = new Dictionary<int, ulong>();
        private int _healMemoRev = -1;

        /// <summary>Would a heal (or reinforce) aimed there have anything to do? Remembered until the walls change (the
        /// crosshair asks every frame).</summary>
        public bool HasHealWork(int tile, int cell, HealKind kind)
        {
            if (_healMemoRev != Revision) { _healMemo.Clear(); _healMemoRev = Revision; }
            long k = ((long)tile * 64 + Math.Max(0, Math.Min(63, cell))) * 2 + (int)kind;
            if (_healMemo.TryGetValue(k, out bool v)) return v;
            HealPlan(tile, cell, kind, _memoPlan);
            v = _memoPlan.Count > 0;
            _healMemo[k] = v;
            return v;
        }

        private bool Wants(int u, int c, HealKind kind)
        {
            WallTile t = _tiles[u];
            ulong bit = 1ul << c;
            bool open = ((OpenCells(u) >> c) & 1) != 0;
            if (open) return true;
            int hp = _hp[u * Cells + c];
            if (kind == HealKind.Reinforce) return (t.Steel & bit) == 0 || hp < Config.MaxCellHealth(false, true);
            return (t.Steel & bit) == 0 && hp < Config.MaxCellHealth(true, false);
        }

        /// <summary>The hole a cell is in, if it is small (closed by other wall, not reaching open air).</summary>
        private void AddSmallHole(int tile, int cell, Dictionary<int, ulong> into)
        {
            double cellArea = CellArea(tile);
            int limit = (int)Math.Floor(Config.SmallHoleArea / cellArea + 1e-9);
            var seen = new HashSet<long> { (long)tile * 64 + cell };
            var q = new Queue<long>();
            q.Enqueue((long)tile * 64 + cell);
            var comp = new List<long>();
            while (q.Count > 0)
            {
                long k = q.Dequeue();
                comp.Add(k);
                if (comp.Count > limit) return; // a big hole: the chunk around the aim only
                int u = (int)(k / 64), c = (int)(k % 64);
                for (int dir = 0; dir < 4; dir++)
                {
                    int v = Step(u, c, dir, out int cv);
                    if (v == BeyondAir) return; // it opens onto a doorway or the sky: not a hole you can close
                    if (v < 0 || ((OpenCells(v) >> cv) & 1) == 0) continue;
                    long kv = (long)v * 64 + cv;
                    if (seen.Add(kv)) q.Enqueue(kv);
                }
            }
            foreach (long k in comp)
            {
                int u = (int)(k / 64), c = (int)(k % 64);
                into[u] = (into.TryGetValue(u, out ulong m) ? m : 0) | (1ul << c);
            }
        }

        private double CellArea(int tile)
        {
            Brush b = World[_tiles[tile].Brush];
            double s1 = (Ax(b.Bounds.Max, b.CellA1) - Ax(b.Bounds.Min, b.CellA1)) / N;
            double s2 = (Ax(b.Bounds.Max, b.CellA2) - Ax(b.Bounds.Min, b.CellA2)) / N;
            return s1 * s2;
        }

        /// <summary>
        /// The open masks every tile will have once <paramref name="plan"/> is filled, with the tidy around it (openings
        /// left too thin or too small fill too).
        /// </summary>
        private Dictionary<int, ulong> HealResultOf(Dictionary<int, ulong> plan)
        {
            var work = new Dictionary<int, ulong>();
            foreach (var kv in plan) work[kv.Key] = OpenCells(kv.Key) & ~kv.Value;
            // Frame pieces this heal brings back are solid once it is done: an opening beside one must not count as
            // reaching open air (it would be left smaller than a fair hole against the column).
            _assumeSolid.Clear();
            var restored = new List<int>();
            foreach (var kv in work)
            {
                if (_tiles[kv.Key].State != TileState.Broken || kv.Value == Brush.AllCells) continue;
                RestoredWith(kv.Key, restored);
                foreach (int f in restored) _assumeSolid.Add(BoundsOf(f));
            }
            try { PlanFill(work, new Dictionary<int, ulong>(plan)); }
            finally
            {
                if (_assumeSolid.Count > 0) { _assumeSolid.Clear(); _beyond.Clear(); }
            }
            return work;
        }

        /// <summary>
        /// The space a heal would fill (the box of every cell that comes back, the fill around it included) and the frame
        /// pieces it would bring back: nobody may be in any of them when it finishes.
        /// </summary>
        public void HealSpace(Dictionary<int, ulong> plan, List<Aabb> cells, List<int> frame)
        {
            cells.Clear();
            frame.Clear();
            if (plan.Count == 0) return;
            Dictionary<int, ulong> work = HealResultOf(plan);
            var keys = new List<int>(work.Keys);
            keys.Sort();
            var restored = new List<int>();
            foreach (int u in keys)
            {
                ulong closed = OpenCells(u) & ~work[u];
                for (int c = 0; c < Cells; c++)
                    if (((closed >> c) & 1) != 0) cells.Add(CellBox(u, c));
                if (_tiles[u].State == TileState.Broken && work[u] != Brush.AllCells)
                {
                    RestoredWith(u, restored);
                    foreach (int f in restored) if (!frame.Contains(f)) frame.Add(f);
                }
            }
        }

        /// <summary>Does it: every planned cell (and the fill around it) comes back patched at full patched health, or
        /// reinforced at full reinforced health.</summary>
        public void ApplyHeal(Dictionary<int, ulong> plan, HealKind kind, int by)
        {
            if (plan.Count == 0) return;
            Dictionary<int, ulong> work = HealResultOf(plan);
            WriteFilled(work, kind == HealKind.Reinforce ? TileCause.Reinforce : TileCause.Heal, by, kind, plan);
        }
    }

    /// <summary>Cells of one tile a charge breaks.</summary>
    public struct ChargeCut
    {
        public int Tile;
        public ulong Cells;
    }
}
