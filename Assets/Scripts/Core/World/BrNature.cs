using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The battle royale map's plains (2026-11-27, Ben: "it must not look like a cube game"): groves and lone trees,
    /// boulders and bushes between the buildings. They are sub-cell terrain in the ground grid, so what you see is exactly
    /// what you hit (the same chamfered surface draws, collides and stops bullets): trunks, canopies, rocks and bushes are
    /// anchors (never sculpted, never fall). Canopies start 3.3 m up, so nobody walks or jumps into one; a bush is solid
    /// like a low wall (it hides a crouching player and stops bullets).
    ///
    /// Since the segments (2026-11-28) every prop is one of a small library of shapes (<see cref="Variants"/> per
    /// <see cref="Shape"/>), turned or mirrored one of eight ways and moved by whole sub-cells, so the game meshes each
    /// shape once however many props a map has. Each segment asks for props at spots (<see cref="BrBuild.Tree"/>) and
    /// the framework scatters more over its open ground (<see cref="NatureFill"/>), each segment from its own random
    /// stream, so changing one segment never moves another's props. Placement is a fixed hash, not a match seed: every
    /// server and client builds the same plains.
    /// </summary>
    public static class BrNature
    {
        /// <summary>How a prop sub-cell is drawn.</summary>
        public const byte KindStone = 0, KindWood = 2, KindLeaves = 3;

        public enum Shape : byte { Tree, BigTree, Rocks, Bush }

        /// <summary>Shapes in the library, per <see cref="Shape"/>.</summary>
        public static readonly int[] Variants = { 14, 10, 14, 10 };

        /// <summary>Lowest canopy sub-cell above the ground: 3.3 m, so nobody walks or jumps into it.</summary>
        public const int CanopyClearSubs = 11;
        /// <summary>A prop's top (sub-cells above its base).</summary>
        public const int TopSubs = 32;
        /// <summary>How much the ground may rise across a prop's footprint (props stand on walkable hillsides too, Hilltop
        /// Fort 2026-11-28): trees (their canopy is counted from the highest ground under it, the trunk reaches the
        /// lowest), boulder groups and bushes (both sit on the lowest ground, the uphill side sunk in).</summary>
        public const double MaxRiseTree = 5.0, MaxRiseRocks = 2.0, MaxRiseBush = 1.4;
        /// <summary>The most any prop allows (tests).</summary>
        public const double MaxRise = MaxRiseTree;
        /// <summary>A trunk reaches this far under its base (6 m: past the lowest ground under its canopy on a hillside).</summary>
        public const int TrunkDown = 20;

        public struct Cell
        {
            public int X, Y, Z;
            public byte Kind;
        }

        /// <summary>One shape of the library: its sub-cells round its own origin (base layer y 0, the origin a cell corner).</summary>
        public sealed class Variant
        {
            public Shape Shape;
            public readonly List<Cell> Cells = new List<Cell>();
            /// <summary>Footprint radius (metres) round the origin.</summary>
            public double Radius;
        }

        /// <summary>One prop on the map.</summary>
        public struct Prop
        {
            public string Kind;
            public Shape Shape;
            /// <summary>Its library shape (index into <see cref="Plan.Library"/>) and orientation (index into <see cref="BrMap.Sym"/>).</summary>
            public int Variant, Orient;
            /// <summary>Where the shape's origin lands (sub-cell coordinates).</summary>
            public int SX, SY, SZ;
            /// <summary>Centre on the ground (metres, map coordinates).</summary>
            public double X, Z;
            /// <summary>Footprint radius (metres): nothing else within it.</summary>
            public double Radius;
            public int Segment;
        }

        public sealed class Plan
        {
            public readonly List<Variant> Library = new List<Variant>();
            public readonly List<Prop> Props = new List<Prop>();
            /// <summary>Requests a segment made that didn't fit (for its tests).</summary>
            public readonly List<PropRequest> Dropped = new List<PropRequest>();
            /// <summary>First library index of each <see cref="Shape"/>.</summary>
            public readonly int[] First = new int[4];

            /// <summary>A prop's sub-cells on the map.</summary>
            public IEnumerable<Cell> CellsOf(Prop p)
            {
                int[] m = BrMap.Sym[p.Orient];
                foreach (Cell c in Library[p.Variant].Cells)
                {
                    Orient(c.X, c.Z, m, out int x, out int z);
                    yield return new Cell { X = p.SX + x, Y = p.SY + c.Y, Z = p.SZ + z, Kind = c.Kind };
                }
            }

            public int CellCount
            {
                get
                {
                    int n = 0;
                    foreach (Prop p in Props) n += Library[p.Variant].Cells.Count;
                    return n;
                }
            }
        }

        /// <summary>A local cell (x, z) under one of the eight orientations about the origin (cell corners stay cell corners).</summary>
        public static void Orient(int x, int z, int[] m, out int tx, out int tz)
        {
            // Odd doubled coordinates turn exactly; every result is odd, so (r - 1) / 2 is exact (no rounding).
            int u = 2 * x + 1, v = 2 * z + 1;
            tx = (m[0] * u + m[1] * v - 1) >> 1;
            tz = (m[2] * u + m[3] * v - 1) >> 1;
        }

        private static readonly object Lock = new object();
        private static Plan _plan;
        private static volatile Dictionary<long, byte[]> _kinds;

        /// <summary>The plains, worked out once per run (the same every time).</summary>
        public static Plan GetPlan()
        {
            lock (Lock)
            {
                if (_plan == null) _plan = MakePlan();
                return _plan;
            }
        }

        /// <summary>Puts the plains into a battle royale ground grid.</summary>
        public static void Stamp(SubGrid g)
        {
            Plan plan = GetPlan();
            foreach (Prop p in plan.Props)
                foreach (Cell c in plan.CellsOf(p))
                    g.Set(c.X, c.Y, c.Z, SubId.Anchor);
            g.ClearChanges();
        }

        /// <summary>How the prop sub-cell at <paramref name="c"/> is drawn (stone for anything not a prop).</summary>
        public static byte KindAt(Int3 c)
        {
            Dictionary<long, byte[]> kinds = _kinds;
            if (kinds == null)
                lock (Lock)
                {
                    if (_kinds == null) _kinds = MakeKinds(GetPlan());
                    kinds = _kinds;
                }
            int n = SubGrid.ChunkSize;
            long key = ChunkKey(c.X >> 4, (c.Y + 1024) >> 4, c.Z >> 4);
            if (!kinds.TryGetValue(key, out byte[] a)) return KindStone;
            return a[(((c.Y + 1024) & (n - 1)) * n + (c.Z & (n - 1))) * n + (c.X & (n - 1))];
        }

        private static long ChunkKey(int cx, int cy, int cz) => ((long)cx << 40) | ((long)cy << 20) | (uint)cz;

        private static Dictionary<long, byte[]> MakeKinds(Plan plan)
        {
            const int s = SubGrid.ChunkSize;
            var d = new Dictionary<long, byte[]>();
            foreach (Prop p in plan.Props)
                foreach (Cell c in plan.CellsOf(p))
                {
                    int y = c.Y + 1024;
                    long key = ChunkKey(c.X >> 4, y >> 4, c.Z >> 4);
                    if (!d.TryGetValue(key, out byte[] a)) d[key] = a = new byte[s * s * s];
                    a[((y & (s - 1)) * s + (c.Z & (s - 1))) * s + (c.X & (s - 1))] = c.Kind;
                }
            return d;
        }

        // ================================================================ Placement

        private static Plan MakePlan()
        {
            var plan = new Plan();
            MakeLibrary(plan);
            BrMap.TemplateBuild t = BrMap.Template;
            for (int id = 0; id < BrLayout.Count; id++) PlaceSegment(plan, t, id);
            return plan;
        }

        private static void PlaceSegment(Plan plan, BrMap.TemplateBuild t, int id)
        {
            BrBuild b = t.Segments[id];
            Rect2 area = b.Area;
            var rng = new Rng(0x5EED7EE5 + id * 7919);
            var mine = new List<Prop>();
            var buildings = new List<Rect2>();
            foreach (Aabb bd in t.Info.Block.Buildings)
            {
                var r = Rect2.Of(bd);
                if (r.Overlaps(area.Grow(10))) buildings.Add(r);
            }

            bool Free(double x, double z, double r)
            {
                if (!area.Contains(x, z, r + 1.5)) return false;
                foreach (Rect2 q in buildings)
                    if (x + r > q.X0 - 5 && x - r < q.X1 + 5 && z + r > q.Z0 - 5 && z - r < q.Z1 + 5) return false;
                var me = new Rect2(x - r, z - r, x + r, z + r);
                foreach (Rect2 q in b.KeepOpen) if (me.Overlaps(q)) return false;
                foreach (Rect2 q in b.Reserved) if (me.Grow(2).Overlaps(q)) return false;
                foreach (var l in b.Lanes) if (BrBuild.LaneTouches(l.From, l.To, l.Half + 2, me)) return false;
                var box = new Aabb(new Vec3d(x - r - 2.5, BrGround.Lowest - 2, z - r - 2.5), new Vec3d(x + r + 2.5, BrMap.NavTop, z + r + 2.5));
                if (t.World.BoxOverlapsSolid(box)) return false;
                Rise(x, z, r + 1, out double lo, out double hi);
                if (hi - lo > MaxRiseTree) return false; // a rock face; each shape checks its own limit
                foreach (Vec3d k in b.KeepClearSpots)
                    if (Sq(k.X - x) + Sq(k.Z - z) < Sq(r + 4)) return false;
                foreach (Prop p in mine)
                    if (Sq(p.X - x) + Sq(p.Z - z) < Sq(p.Radius + r + 1.2)) return false;
                return true;
            }

            bool Put(Shape shape, double x, double z, int seed)
            {
                int v = plan.First[(int)shape] + (int)(Hash(seed, 1, 77) % (uint)Variants[(int)shape]);
                double r = plan.Library[v].Radius;
                if (mine.Count >= BrLayout.MaxProps || !Free(x, z, r)) return false;
                const double s = SubGrid.SubSize;
                Rise(x, z, r, out double lo, out double hi);
                bool tree = shape == Shape.Tree || shape == Shape.BigTree;
                if (hi - lo > (tree ? MaxRiseTree : shape == Shape.Rocks ? MaxRiseRocks : MaxRiseBush)) { return false; }
                // Trees count from the highest ground under the canopy (so it clears jump reach everywhere under it),
                // rocks and bushes from the lowest.
                int sy = tree ? (int)Math.Ceiling(hi / s - 1e-9) : (int)Math.Floor(lo / s + 1e-9);
                if (sy + TopSubs >= BrMap.GroundMinY + BrMap.GroundLayers * SubGrid.ChunkSize || sy - TrunkDown <= BrMap.GroundMinY) return false;
                if (tree)
                {
                    // The trunk reaches 0.3 m under the lowest ground round its foot.
                    Rise(x, z, 1.2, out double trunkLo, out _);
                    if (sy * s - trunkLo > TrunkDown * s - 0.3) return false;
                }
                mine.Add(new Prop
                {
                    Kind = tree ? "tree" : shape == Shape.Rocks ? "rocks" : "bush", Shape = shape, Variant = v, Orient = (int)(Hash(seed, 2, 78) % 8),
                    SX = (int)Math.Floor(x / s), SY = sy, SZ = (int)Math.Floor(z / s), X = x, Z = z, Radius = r, Segment = id,
                });
                return true;
            }

            // What the segment asked for, first.
            foreach (PropRequest q in b.Props)
                if (!Put(q.Shape, q.X, q.Z, q.Seed)) plan.Dropped.Add(q);

            double RandX() => area.X0 + rng.Unit() * (area.X1 - area.X0);
            double RandZ() => area.Z0 + rng.Unit() * (area.Z1 - area.Z0);
            NatureFill fill = b.Nature;
            // Groves: a few trees round a centre, bushes at their edges.
            for (int gi = 0, made = 0; gi < 60 * fill.Groves && made < fill.Groves; gi++)
            {
                double gx = RandX(), gz = RandZ();
                if (!Free(gx, gz, 6)) continue;
                made++;
                int want = 4 + rng.Range(0, 5);
                for (int k = 0, tries = 0; tries < 60 && k < want; tries++)
                {
                    double a = rng.Unit() * Math.PI * 2, d = 3 + rng.Unit() * 13;
                    if (Put(rng.Range(0, 100) < 35 ? Shape.BigTree : Shape.Tree, gx + Math.Cos(a) * d, gz + Math.Sin(a) * d, rng.Next())) k++;
                }
                for (int k = 0, tries = 0; tries < 30 && k < 3; tries++)
                {
                    double a = rng.Unit() * Math.PI * 2, d = 10 + rng.Unit() * 10;
                    if (Put(Shape.Bush, gx + Math.Cos(a) * d, gz + Math.Sin(a) * d, rng.Next())) k++;
                }
            }
            for (int k = 0, tries = 0; tries < 40 * fill.LoneTrees && k < fill.LoneTrees; tries++)
                if (Put(rng.Range(0, 100) < 35 ? Shape.BigTree : Shape.Tree, RandX(), RandZ(), rng.Next())) k++;
            for (int k = 0, tries = 0; tries < 40 * fill.RockGroups && k < fill.RockGroups; tries++)
                if (Put(Shape.Rocks, RandX(), RandZ(), rng.Next())) k++;
            for (int k = 0, tries = 0; tries < 40 * fill.Bushes && k < fill.Bushes; tries++)
                if (Put(Shape.Bush, RandX(), RandZ(), rng.Next())) k++;
            plan.Props.AddRange(mine);
        }

        /// <summary>Lowest and highest ground (<see cref="BrMap.Field"/>) over a disc: its centre and 16 points round it at two radii.</summary>
        private static void Rise(double x, double z, double r, out double lo, out double hi)
        {
            HeightField f = BrMap.Field;
            lo = hi = f.HeightAt(x, z);
            for (int k = 0; k < 16; k++)
                for (int ring = 1; ring <= 2; ring++)
                {
                    double a = k * Math.PI / 8, d = r * ring / 2;
                    double h = f.HeightAt(x + Math.Cos(a) * d, z + Math.Sin(a) * d);
                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }
        }

        // ================================================================ The library (shapes round their own origin)

        private static void MakeLibrary(Plan plan)
        {
            for (int sh = 0; sh < 4; sh++)
            {
                plan.First[sh] = plan.Library.Count;
                for (int k = 0; k < Variants[sh]; k++)
                {
                    var v = new Variant { Shape = (Shape)sh };
                    int seed = (int)(Hash(0x7EE5 + sh * 131, k, 5) & 0x7FFFFFFF);
                    switch ((Shape)sh)
                    {
                        case Shape.Tree: Tree(v, seed, false); break;
                        case Shape.BigTree: Tree(v, seed, true); break;
                        case Shape.Rocks: RockGroup(v, seed); break;
                        default: Bush(v, seed); break;
                    }
                    Unique(v);
                    plan.Library.Add(v);
                }
            }
        }

        /// <summary>Drops repeated cells (lobes overlap), wood winning over leaves, and works out the footprint.</summary>
        private static void Unique(Variant v)
        {
            var at = new Dictionary<long, int>();
            var list = new List<Cell>(v.Cells.Count);
            foreach (Cell c in v.Cells)
            {
                long key = ((long)(c.X + 4096) << 32) | ((long)(c.Y + 1024) << 16) | (uint)(c.Z + 4096);
                if (at.TryGetValue(key, out int j)) { if (c.Kind == KindWood) { Cell w = list[j]; w.Kind = KindWood; list[j] = w; } }
                else { at[key] = list.Count; list.Add(c); }
            }
            v.Cells.Clear();
            v.Cells.AddRange(list);
            const double s = SubGrid.SubSize;
            double r = 0;
            foreach (Cell c in v.Cells)
            {
                // Farthest cell corner from the origin (the same in every orientation: the origin is a cell corner).
                double fx = Math.Max(Math.Abs(c.X), Math.Abs(c.X + 1)) * s, fz = Math.Max(Math.Abs(c.Z), Math.Abs(c.Z + 1)) * s;
                r = Math.Max(r, Math.Sqrt(fx * fx + fz * fz));
            }
            v.Radius = r + 0.05;
        }

        /// <summary>A tree: trunk reaching down under the base with a few root knuckles, a lumpy canopy of overlapping blobs.</summary>
        private static void Tree(Variant v, int seed, bool big)
        {
            int w = big ? 3 : 2;
            int tx = -(w / 2), tz = tx;
            int trunkTop = (big ? 15 : 12) + (int)(Hash(seed, 1, 1) % 3);
            for (int y = -TrunkDown; y <= trunkTop; y++)
                for (int dz = 0; dz < w; dz++)
                    for (int dx = 0; dx < w; dx++) Add(v, tx + dx, y, tz + dz, KindWood);
            for (int dz = -1; dz <= w; dz++)
                for (int dx = -1; dx <= w; dx++)
                {
                    bool ring = dx == -1 || dz == -1 || dx == w || dz == w;
                    bool corner = (dx == -1 || dx == w) && (dz == -1 || dz == w);
                    if (ring && !corner && Hash(seed, dx + 7, dz + 7) % 100 < 45)
                        for (int y = -TrunkDown + 3; y <= 0; y++) Add(v, tx + dx, y, tz + dz, KindWood);
                }
            double cx = tx + w * 0.5, cz = tz + w * 0.5;
            int lobes = 3 + (int)(Hash(seed, 2, 2) % 3);
            double r0 = big ? 6.0 : 4.8;
            var lobe = new (double x, double y, double z, double r)[lobes + 1];
            lobe[0] = (cx, trunkTop + 2.5, cz, r0 + (Hash(seed, 3, 3) % 3) * 0.5);
            for (int i = 1; i <= lobes; i++)
            {
                double ang = (Hash(seed, 10 + i, 0) % 360) * Math.PI / 180.0;
                double dist = (big ? 3.5 : 2.5) + (Hash(seed, 20 + i, 0) % 20) / 10.0;
                lobe[i] = (cx + Math.Cos(ang) * dist, trunkTop + 0.5 + (Hash(seed, 30 + i, 0) % 30) / 10.0, cz + Math.Sin(ang) * dist,
                           r0 - 1.5 + (Hash(seed, 40 + i, 0) % 15) / 10.0);
            }
            int reach = big ? 13 : 10;
            for (int y = CanopyClearSubs; y < TopSubs; y++)
                for (int qz = tz - reach; qz <= tz + w + reach; qz++)
                    for (int qx = tx - reach; qx <= tx + w + reach; qx++)
                    {
                        double px = qx + 0.5, py = y + 0.5, pz = qz + 0.5;
                        double wobble = (Hash(seed + 5, qx * 131 + y, qz) % 100) / 100.0 * 0.28;
                        foreach (var l in lobe)
                        {
                            double dx = (px - l.x) / l.r, dy = (py - l.y) / (l.r * 0.78), dz = (pz - l.z) / l.r;
                            if (dx * dx + dy * dy + dz * dz <= 0.86 + wobble) { Add(v, qx, y, qz, KindLeaves); break; }
                        }
                    }
        }

        /// <summary>One to three boulders together, each a lumpy squashed blob sunk a little into the ground, 1-1.8 m tall.</summary>
        private static void RockGroup(Variant v, int seed)
        {
            int count = 1 + (int)(Hash(seed, 0, 1) % 3);
            for (int i = 0; i < count; i++)
            {
                double a = Hash01(seed, i, 1) * Math.PI * 2, d = i == 0 ? 0 : 1.6 + Hash01(seed, i, 2) * 0.8;
                Rock(v, Math.Cos(a) * d / SubGrid.SubSize, Math.Sin(a) * d / SubGrid.SubSize, Hash(seed, i, 3));
            }
        }

        private static void Rock(Variant v, double cx, double cz, uint seed)
        {
            double rx = 3.5 + (seed % 5), rz = 3.5 + (seed / 5 % 5), ry = 3.5 + (seed / 25 % 3);
            double cy = -1, tilt = (seed / 75 % 100) / 100.0 * 0.4 - 0.2;
            int ext = (int)Math.Ceiling(Math.Max(rx, rz) * 1.2) + 1;
            int bx = (int)Math.Floor(cx), bz = (int)Math.Floor(cz);
            for (int y = -3; y < (int)(cy + ry * 1.2) + 1; y++)
                for (int qz = bz - ext; qz <= bz + ext; qz++)
                    for (int qx = bx - ext; qx <= bx + ext; qx++)
                    {
                        double px = qx + 0.5 - cx, py = y + 0.5 - cy - tilt * (qx + 0.5 - cx), pz = qz + 0.5 - cz;
                        double lump = (Hash((int)seed, qx * 7 + (y & 3), qz * 3 + (y >> 2)) % 100) / 100.0 * 0.22;
                        double d = Sq(px / rx) + Sq(py / ry) + Sq(pz / rz);
                        if (d <= 0.9 + lump) Add(v, qx, y, qz, KindStone);
                    }
        }

        /// <summary>A bush: a solid low dome of leaves, 1-1.4 m tall (hides a crouching player and stops bullets).</summary>
        private static void Bush(Variant v, int seed)
        {
            double r = 3.5 + (Hash(seed, 1, 1) % 3) * 0.7, h = 3.4 + (Hash(seed, 2, 2) % 2);
            int ext = (int)Math.Ceiling(r * 1.2) + 1;
            for (int y = -1; y < (int)h + 1; y++)
                for (int qz = -ext; qz <= ext; qz++)
                    for (int qx = -ext; qx <= ext; qx++)
                    {
                        double px = qx + 0.5, py = y + 0.5, pz = qz + 0.5;
                        double lump = (Hash(seed, qx * 5 + y, qz) % 100) / 100.0 * 0.25;
                        if (Sq(px / r) + Sq(py / h) + Sq(pz / r) <= 0.85 + lump) Add(v, qx, y, qz, KindLeaves);
                    }
        }

        private static void Add(Variant v, int x, int y, int z, byte kind)
        {
            if (y >= TopSubs) return;
            v.Cells.Add(new Cell { X = x, Y = y, Z = z, Kind = kind });
        }

        private static double Sq(double v) => v * v;

        private static uint Hash(int seed, int x, int z)
        {
            uint h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)z * 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }

        private static double Hash01(int seed, int x, int z) => (Hash(seed, x, z) % 10000) / 10000.0;

        private sealed class Rng
        {
            private uint _s;
            public Rng(int seed) { _s = (uint)seed * 2654435761u + 1; }
            public int Next()
            {
                _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
                return (int)(_s & 0x7FFFFFFF);
            }
            public int Range(int lo, int hiExclusive) => lo + Next() % Math.Max(1, hiExclusive - lo);
            public double Unit() => (Next() % 1000000) / 1000000.0;
        }
    }
}
