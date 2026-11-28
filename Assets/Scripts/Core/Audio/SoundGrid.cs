using System;
using System.Collections.Generic;

namespace Sculpt.Core.Audio
{
    /// <summary>
    /// The air grid sound is routed on: which faces between 0.5 m cells sound can pass, worked out straight from the
    /// world's shapes (no ray casts), once at load and again only where a wall changes.
    ///
    /// Each face is tested by four short lines that run from one cell centre to the next, a quarter cell off the centre
    /// on both face axes. All such lines in the world sit on one lattice 0.25 m apart, so any opening at least 0.25 m
    /// across on both axes always has a line through it. A line is blocked if any solid shape touches it (touching counts,
    /// like the bullet rays: a seam between two tiles never leaks). A face is open while any of its four lines is.
    ///
    /// Shapes: building brushes (boxes, ramps and stairs as wedges, holed wall tiles as their standing cells; broken and
    /// open-door tiles are not solid) and sub-cell ground (each solid sub-cell as its 0.3 m box: chamfers count as solid,
    /// which only matters for sound grazing a hillside). Below <see cref="FloorY"/> everything is solid; above the world
    /// and off its edges is open air.
    /// </summary>
    public sealed class SoundGrid
    {
        public const int ChunkBits = 3, ChunkSize = 1 << ChunkBits, ChunkMask = ChunkSize - 1;
        public const ushort AllBlocked = 0x0FFF;
        private const double Eps = 1e-6;
        private const int Off = 1 << 20;

        public readonly double CellSize;
        /// <summary>Everything below this height is solid for sound (metres).</summary>
        public readonly double FloorY;
        private int _floorCell;
        /// <summary>Sub-cell grids that are plain flat ground (solid below one height, air above): no need to rasterise
        /// them, the floor cell covers them exactly (a 614 m map has millions of ground columns).</summary>
        private readonly List<SubGrid> _flat = new List<SubGrid>();
        /// <summary>On flat ground: the chunk columns (cx, cz) that hold props above it (trees, rocks: chunks with their own
        /// sub-cells above the ground's top), rasterised cell by cell like any other terrain.</summary>
        private readonly List<(int cx, int cz)> _propColumns = new List<(int cx, int cz)>();
        private int _flatTopSub;
        private readonly List<BrushWorld> _brushes = new List<BrushWorld>();
        private readonly List<SubGrid> _subs = new List<SubGrid>();
        /// <summary>Smooth height-map ground: never stored, worked out from the heights whenever a cell is asked about
        /// (a 1,306 m map's ground would be millions of cells).</summary>
        private readonly List<HeightField> _fields = new List<HeightField>();
        private readonly Dictionary<long, ushort[]> _chunks = new Dictionary<long, ushort[]>();
        private readonly List<int> _near = new List<int>();
        private long _lastKey = long.MinValue;
        private ushort[] _last;

        /// <summary>False if the world holds a terrain type this grid can't read (then <see cref="SoundPaths"/> tests faces
        /// with ray casts instead).</summary>
        public readonly bool Supported = true;

        /// <summary>Lines set blocked by the last build or rebuild (stats).</summary>
        public long LinesMarked { get; private set; }

        /// <param name="flatGroundAsFloor">Plain flat ground is covered by the floor instead of marked cell by cell (the
        /// same result, tested; false only to check that).</param>
        public SoundGrid(ITerrain world, double cellSize = 0.5, double floorY = -3.0, bool flatGroundAsFloor = true)
        {
            CellSize = cellSize;
            Supported = Collect(world);
            // Smooth ground can dip under the usual floor: the floor goes under its lowest point.
            foreach (HeightField f in _fields) floorY = Math.Min(floorY, Math.Floor(f.MinHeight - 1));
            FloorY = floorY;
            _floorCell = (int)Math.Floor(floorY / cellSize);
            if (Supported && flatGroundAsFloor) FindFlatGround();
        }

        private bool Collect(ITerrain t)
        {
            switch (t)
            {
                case CompositeTerrain c: return Collect(c.A) & Collect(c.B);
                case BrushWorld b: _brushes.Add(b); return true;
                case SubGrid g: _subs.Add(g); return true;
                case HeightField f: _fields.Add(f); return true;
                default: return false;
            }
        }

        /// <summary>The 12 blocked bits of a cell: axis a (0 x, 1 y, 2 z) owns bits 4a..4a+3, one per line of the face
        /// between this cell and its +a neighbour.</summary>
        public ushort Blocked(int x, int y, int z)
        {
            if (y < _floorCell) return AllBlocked;
            long k = Key(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);
            ushort[] c;
            if (k == _lastKey) c = _last;
            else
            {
                _chunks.TryGetValue(k, out c);
                _lastKey = k;
                _last = c;
            }
            ushort b = c == null ? (ushort)0 : c[Index(x, y, z)];
            for (int i = 0; i < _fields.Count && b != AllBlocked; i++) b |= FieldBlocked(_fields[i], x, y, z);
            return b;
        }

        /// <summary>
        /// The lines of cell (x, y, z) the smooth ground touches (touching counts, as for every other shape). A line along
        /// X or Z is blocked where the ground anywhere along it reaches its height; a line up Y where the ground under
        /// it reaches its bottom end.
        /// </summary>
        private ushort FieldBlocked(HeightField f, int x, int y, int z)
        {
            double cs = CellSize, q = cs * 0.25;
            double lowest = y * cs + q;
            if (lowest > f.MaxHeight + Eps) return 0;
            double fx0 = x * cs + q, fx1 = (x + 1.5) * cs, fz0 = z * cs + q, fz1 = (z + 1.5) * cs;
            if (f.MaxBoundOver(fx0, fz0, fx1, fz1) < lowest - Eps) return 0;
            if (f.MinBoundOver(fx0, fz0, fx1, fz1) >= y * cs + 3 * q && f.Covers(fx0, fz0) && f.Covers(fx1, fz1)) return AllBlocked;
            int bits = 0;
            for (int k = 0; k < 4; k++)
            {
                double ou = (k & 1) == 0 ? q : 3 * q, ov = (k & 2) == 0 ? q : 3 * q;
                // Axis X: across it lie y (u) and z (v).
                double ly = y * cs + ou, lz = z * cs + ov;
                if (f.MaxOver((x + 0.5) * cs, lz, (x + 1.5) * cs, lz) >= ly - Eps) bits |= 1 << k;
                // Axis Y: x (u) and z (v); the line rises from (y + 0.5) cells.
                double vx = x * cs + ou, vz = z * cs + ov;
                if (f.HeightAt(vx, vz) >= (y + 0.5) * cs - Eps) bits |= 1 << (4 + k);
                // Axis Z: x (u) and y (v).
                double zx = x * cs + ou, zy = y * cs + ov;
                if (f.MaxOver(zx, (z + 0.5) * cs, zx, (z + 1.5) * cs) >= zy - Eps) bits |= 1 << (8 + k);
            }
            return (ushort)bits;
        }

        /// <summary>Can sound pass between cell (x,y,z) and its neighbour one step along +axis?</summary>
        public bool Open(int x, int y, int z, int axis) => ((Blocked(x, y, z) >> (axis * 4)) & 0xF) != 0xF;

        public int Chunks => _chunks.Count;

        /// <summary>
        /// Can a line from <paramref name="a"/> to <paramref name="b"/> get through? Walks the cells it crosses and stops
        /// at the first face no line can pass. Errs toward open: a face counts as open while any of its four lines is,
        /// so an opening a quarter cell across lets it through even if the real line would just miss it. Used by the
        /// server's interest management as a cheap, generous line of sight (never blocks a line a ray would pass).
        /// </summary>
        public bool LineOpen(Vec3d a, Vec3d b)
        {
            double cs = CellSize;
            double ax = a.X / cs, ay = a.Y / cs, az = a.Z / cs;
            double dx = b.X / cs - ax, dy = b.Y / cs - ay, dz = b.Z / cs - az;
            int x = (int)Math.Floor(ax), y = (int)Math.Floor(ay), z = (int)Math.Floor(az);
            int ex = (int)Math.Floor(ax + dx), ey = (int)Math.Floor(ay + dy), ez = (int)Math.Floor(az + dz);
            int sx = dx > 0 ? 1 : dx < 0 ? -1 : 0, sy = dy > 0 ? 1 : dy < 0 ? -1 : 0, sz = dz > 0 ? 1 : dz < 0 ? -1 : 0;
            double tdx = sx != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity;
            double tdy = sy != 0 ? Math.Abs(1 / dy) : double.PositiveInfinity;
            double tdz = sz != 0 ? Math.Abs(1 / dz) : double.PositiveInfinity;
            double tx = sx > 0 ? (x + 1 - ax) * tdx : sx < 0 ? (ax - x) * tdx : double.PositiveInfinity;
            double ty = sy > 0 ? (y + 1 - ay) * tdy : sy < 0 ? (ay - y) * tdy : double.PositiveInfinity;
            double tz = sz > 0 ? (z + 1 - az) * tdz : sz < 0 ? (az - z) * tdz : double.PositiveInfinity;
            int guard = Math.Abs(ex - x) + Math.Abs(ey - y) + Math.Abs(ez - z) + 3;
            while ((x != ex || y != ey || z != ez) && guard-- > 0)
            {
                if (tx <= ty && tx <= tz)
                {
                    if (tx > 1) break;
                    if (!Open(sx > 0 ? x : x - 1, y, z, 0)) return false;
                    x += sx; tx += tdx;
                }
                else if (ty <= tz)
                {
                    if (ty > 1) break;
                    if (!Open(x, sy > 0 ? y : y - 1, z, 1)) return false;
                    y += sy; ty += tdy;
                }
                else
                {
                    if (tz > 1) break;
                    if (!Open(x, y, sz > 0 ? z : z - 1, 2)) return false;
                    z += sz; tz += tdz;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ building

        /// <summary>Works out the whole world (at load).</summary>
        public void Build()
        {
            _chunks.Clear();
            _lastKey = long.MinValue;
            LinesMarked = 0;
            var all = new CellBox(int.MinValue / 4, _floorCell, int.MinValue / 4, int.MaxValue / 4, int.MaxValue / 4, int.MaxValue / 4);
            foreach (BrushWorld w in _brushes)
                for (int i = 0; i < w.Count; i++) Rasterize(w[i], all);
            foreach (SubGrid g in _subs)
                if (!_flat.Contains(g)) RasterizeSubs(g, all, null);
                else RasterizeProps(g, all, null);
        }

        /// <summary>
        /// Plain flat ground (plain chunks solid below one height and empty above it, its top on this grid's cell lattice;
        /// chunks with their own sub-cells are props above the top, or solid all through below it):
        /// marking it cell by cell gives exactly what a floor at its top gives, so it is not marked at all. Only when it
        /// is the only sub-cell grid, so the floor is right everywhere it matters.
        /// </summary>
        private void FindFlatGround()
        {
            _flat.Clear();
            _propColumns.Clear();
            if (_subs.Count != 1) return;
            SubGrid g = _subs[0];
            int n = SubGrid.ChunkSize, top = int.MinValue;
            var mixed = new List<(int cx, int cy, int cz)>();
            for (int cy = 0; cy < g.ChunksY; cy++)
            {
                // Plain chunks of a layer must all be solid or all be empty; chunks with their own sub-cells (props, and
                // roots sunk into the ground) are checked below.
                int layer = -2;
                for (int cx = 0; cx < g.ChunksX; cx++)
                    for (int cz = 0; cz < g.ChunksZ; cz++)
                    {
                        int sol = g.ChunkSolidity(cx, cy, cz);
                        if (sol < 0) { mixed.Add((cx, cy, cz)); continue; }
                        if (layer == -2) layer = sol;
                        else if (sol != layer) return;
                    }
                if (layer == -2) return; // a layer with nothing plain in it: not plain ground
                bool solid = layer == 1;
                int y0 = g.MinY + cy * n;
                if (solid) { if (top != int.MinValue) return; } // solid above air: not plain ground
                else if (top == int.MinValue) top = y0;
            }
            if (top == int.MinValue) top = g.MaxYExclusive;
            double topY = top * SubGrid.SubSize;
            double cells = topY / CellSize;
            if (Math.Abs(cells - Math.Round(cells)) > 1e-9 || topY < FloorY) return;
            // Mixed chunks below the top must be solid all through (the floor stands for them); above it they are props.
            var cols = new HashSet<(int, int)>();
            foreach (var (cx, cy, cz) in mixed)
            {
                int y0 = g.MinY + cy * n;
                if (y0 >= top) { cols.Add((cx, cz)); continue; }
                for (int y = y0; y < y0 + n; y++)
                    for (int z = cz * n; z < cz * n + n; z++)
                        for (int x = cx * n; x < cx * n + n; x++)
                            if (!g.IsSolid(x, y, z)) return;
            }
            _floorCell = (int)Math.Round(cells);
            _flatTopSub = top;
            _propColumns.AddRange(cols);
            _propColumns.Sort();
            _flat.Add(g);
        }

        /// <summary>Works out again every face that a change inside <paramref name="region"/> can touch (a tile broke,
        /// healed, got a hole, a door opened or shut).</summary>
        public void Rebuild(Aabb region)
        {
            double cs = CellSize;
            // A line of cell c along an axis spans c+0.5 .. c+1.5 cells, so the cells to redo start one further down.
            int x0 = (int)Math.Floor(region.Min.X / cs) - 2, x1 = (int)Math.Floor(region.Max.X / cs) + 1;
            int y0 = Math.Max(_floorCell, (int)Math.Floor(region.Min.Y / cs) - 2), y1 = (int)Math.Floor(region.Max.Y / cs) + 1;
            int z0 = (int)Math.Floor(region.Min.Z / cs) - 2, z1 = (int)Math.Floor(region.Max.Z / cs) + 1;
            var box = new CellBox(x0, y0, z0, x1, y1, z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);
                        if (_chunks.TryGetValue(k, out ushort[] c)) c[Index(x, y, z)] = 0;
                    }
            // Every shape whose lines can reach those cells, clipped to them.
            var reach = new Aabb(new Vec3d((x0 - 1) * cs, (y0 - 1) * cs, (z0 - 1) * cs), new Vec3d((x1 + 2) * cs, (y1 + 2) * cs, (z1 + 2) * cs));
            foreach (BrushWorld w in _brushes)
            {
                w.BrushesNear(reach, _near);
                for (int i = 0; i < _near.Count; i++) Rasterize(w[_near[i]], box);
            }
            foreach (SubGrid g in _subs)
                if (!_flat.Contains(g)) RasterizeSubs(g, box, reach);
                else RasterizeProps(g, box, reach);
        }

        /// <summary>Flat ground with props on it: only the columns that hold props, from the ground's top up (below it the
        /// floor covers everything, roots included: they are solid like the ground round them).</summary>
        private void RasterizeProps(SubGrid g, CellBox clip, Aabb? reach)
        {
            if (_propColumns.Count == 0) return;
            double s = SubGrid.SubSize, w = SubGrid.ChunkSize * s;
            foreach (var (cx, cz) in _propColumns)
            {
                var col = new Aabb(new Vec3d(cx * w, _flatTopSub * s, cz * w), new Vec3d((cx + 1) * w - 1e-9, g.MaxYExclusive * s, (cz + 1) * w - 1e-9));
                if (reach.HasValue)
                {
                    Aabb r = reach.Value;
                    if (r.Max.X < col.Min.X || r.Min.X > col.Max.X || r.Max.Z < col.Min.Z || r.Min.Z > col.Max.Z || r.Max.Y < col.Min.Y) continue;
                    col = new Aabb(new Vec3d(Math.Max(col.Min.X, r.Min.X), Math.Max(col.Min.Y, r.Min.Y), Math.Max(col.Min.Z, r.Min.Z)),
                                   new Vec3d(Math.Min(col.Max.X, r.Max.X), Math.Min(col.Max.Y, r.Max.Y), Math.Min(col.Max.Z, r.Max.Z)));
                }
                RasterizeSubs(g, clip, col);
            }
        }

        private readonly struct CellBox
        {
            public readonly int X0, Y0, Z0, X1, Y1, Z1;
            public CellBox(int x0, int y0, int z0, int x1, int y1, int z1) { X0 = x0; Y0 = y0; Z0 = z0; X1 = x1; Y1 = y1; Z1 = z1; }
        }

        private void Rasterize(in Brush b, CellBox clip)
        {
            if (!b.Solid) return;
            if (b.Solids != null)
            {
                for (int i = 0; i < b.Solids.Length; i++) MarkBox(b.Solids[i], clip, null);
                return;
            }
            if (b.IsRamp) MarkBox(b.Bounds, clip, b);
            else MarkBox(b.Bounds, clip, null);
        }

        private void RasterizeSubs(SubGrid g, CellBox clip, Aabb? reach)
        {
            double s = SubGrid.SubSize;
            int ymin = Math.Max(g.MinY, (int)Math.Floor(FloorY / s) - 1), ymax = g.MaxYExclusive - 1;
            int xa = 0, xb = g.SizeX - 1, za = 0, zb = g.SizeZ - 1;
            if (reach.HasValue)
            {
                Aabb r = reach.Value;
                xa = Math.Max(xa, (int)Math.Floor(r.Min.X / s)); xb = Math.Min(xb, (int)Math.Floor(r.Max.X / s));
                za = Math.Max(za, (int)Math.Floor(r.Min.Z / s)); zb = Math.Min(zb, (int)Math.Floor(r.Max.Z / s));
                ymin = Math.Max(ymin, (int)Math.Floor(r.Min.Y / s)); ymax = Math.Min(ymax, (int)Math.Floor(r.Max.Y / s));
            }
            for (int x = xa; x <= xb; x++)
                for (int z = za; z <= zb; z++)
                {
                    int run = int.MinValue;
                    for (int y = ymin; y <= ymax + 1; y++)
                    {
                        bool solid = y <= ymax && g.IsSolid(x, y, z);
                        if (solid && run == int.MinValue) run = y;
                        else if (!solid && run != int.MinValue)
                        {
                            MarkBox(new Aabb(new Vec3d(x * s, run * s, z * s), new Vec3d((x + 1) * s, y * s, (z + 1) * s)), clip, null);
                            run = int.MinValue;
                        }
                    }
                }
        }

        /// <summary>Marks every line the box (or the wedge, when <paramref name="wedge"/> is given) touches.</summary>
        private void MarkBox(Aabb box, CellBox clip, Brush? wedge)
        {
            double cs = CellSize, h = cs * 0.5;
            Vec3d n = default, p0 = default;
            if (wedge.HasValue)
            {
                n = wedge.Value.SlopeNormal;
                p0 = LowEdge(wedge.Value);
            }
            for (int a = 0; a < 3; a++)
            {
                int ua = a == 0 ? 1 : 0, va = a == 2 ? 1 : 2;
                double minA = Get(box.Min, a), maxA = Get(box.Max, a);
                double minU = Get(box.Min, ua), maxU = Get(box.Max, ua), minV = Get(box.Min, va), maxV = Get(box.Max, va);
                // Lines on this axis sit at (k + 0.5) * h on both other axes.
                int ku0 = (int)Math.Ceiling((minU - Eps) / h - 0.5), ku1 = (int)Math.Floor((maxU + Eps) / h - 0.5);
                int kv0 = (int)Math.Ceiling((minV - Eps) / h - 0.5), kv1 = (int)Math.Floor((maxV + Eps) / h - 0.5);
                // The line of cell c runs from (c + 0.5) cs to (c + 1.5) cs.
                int c0 = (int)Math.Ceiling((minA - Eps) / cs - 1.5), c1 = (int)Math.Floor((maxA + Eps) / cs - 0.5);
                for (int ku = ku0; ku <= ku1; ku++)
                {
                    int cu = ku >> 1;
                    for (int kv = kv0; kv <= kv1; kv++)
                    {
                        int cv = kv >> 1;
                        int bit = 1 << (a * 4 + ((ku & 1) | ((kv & 1) << 1)));
                        for (int c = c0; c <= c1; c++)
                        {
                            int x, y, z;
                            if (a == 0) { x = c; y = cu; z = cv; }
                            else if (a == 1) { x = cu; y = c; z = cv; }
                            else { x = cu; y = cv; z = c; }
                            if (x < clip.X0 || x > clip.X1 || y < clip.Y0 || y > clip.Y1 || z < clip.Z0 || z > clip.Z1) continue;
                            if (wedge.HasValue)
                            {
                                // The part of the line inside the box, then is any of it under the slope?
                                double s0 = Math.Max((c + 0.5) * cs, minA), s1 = Math.Min((c + 1.5) * cs, maxA);
                                double lu = (ku + 0.5) * h, lv = (kv + 0.5) * h;
                                Vec3d q0 = Put(a, ua, va, s0, lu, lv), q1 = Put(a, ua, va, s1, lu, lv);
                                if (Math.Min(n.Dot(q0 - p0), n.Dot(q1 - p0)) > Eps) continue;
                            }
                            Mark(x, y, z, bit);
                        }
                    }
                }
            }
        }

        private void Mark(int x, int y, int z, int bit)
        {
            long k = Key(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);
            ushort[] c;
            if (k == _lastKey && _last != null) c = _last;
            else
            {
                if (!_chunks.TryGetValue(k, out c))
                {
                    c = new ushort[ChunkSize * ChunkSize * ChunkSize];
                    _chunks[k] = c;
                }
                _lastKey = k; // the same cache Blocked reads (key to chunk, kept in step)
                _last = c;
            }
            int i = Index(x, y, z);
            if ((c[i] & bit) == 0) LinesMarked++;
            c[i] |= (ushort)bit;
        }

        private static Vec3d Put(int a, int ua, int va, double sa, double su, double sv)
        {
            double x = 0, y = 0, z = 0;
            Set(a, sa); Set(ua, su); Set(va, sv);
            return new Vec3d(x, y, z);
            void Set(int axis, double v) { if (axis == 0) x = v; else if (axis == 1) y = v; else z = v; }
        }

        private static Vec3d LowEdge(in Brush b)
        {
            Aabb w = b.Bounds;
            switch (b.Rise)
            {
                case Dir.West: return new Vec3d(w.Max.X, w.Min.Y, w.Min.Z);
                case Dir.South: return new Vec3d(w.Min.X, w.Min.Y, w.Max.Z);
                default: return new Vec3d(w.Min.X, w.Min.Y, w.Min.Z); // East, North
            }
        }

        private static double Get(Vec3d v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
        private static int Index(int x, int y, int z) => ((y & ChunkMask) << (2 * ChunkBits)) | ((z & ChunkMask) << ChunkBits) | (x & ChunkMask);
        private static long Key(int x, int y, int z) => ((long)(x + Off) << 42) | ((long)(y + Off) << 21) | (long)(z + Off);
    }
}
