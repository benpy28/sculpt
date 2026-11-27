using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The battle royale map's plains (2026-11-27, Ben: "it must not look like a cube game", the stylised grassy plains
    /// look): groves and lone trees, boulders and bushes in the fields between the buildings. They are sub-cell terrain
    /// in the ground grid, so what you see is exactly what you hit (the same chamfered surface draws, collides and stops
    /// bullets): trunks, canopies, rocks and bushes are anchors (never sculpted, never fall). Canopies start 3.3 m up, so
    /// nobody walks or jumps into one; a bush is solid like a low wall (it hides a crouching player and stops bullets).
    ///
    /// Fair like the rest of the map: placed once in the design eighth (clear of buildings, doorways, chests, hills and
    /// walls, never across the eighth's edges) and stamped by the eight symmetries of the square, so every prop has an
    /// exact twin in every other eighth (tested). Placement is a fixed hash of the map, not a seed: every server and
    /// client builds the same plains.
    /// </summary>
    public static class BrNature
    {
        /// <summary>How a prop sub-cell is drawn.</summary>
        public const byte KindStone = 0, KindWood = 2, KindLeaves = 3;

        /// <summary>Lowest canopy sub-cell above the ground: 3.3 m, so nobody walks or jumps into it.</summary>
        public const int CanopyClearSubs = 11;
        /// <summary>The grid's top (sub-cells above the ground): canopies stay under it.</summary>
        public const int TopSubs = 32;

        public struct Cell
        {
            public int X, Y, Z;
            public byte Kind;
        }

        /// <summary>One prop in the design eighth, for tests and the map plan.</summary>
        public struct Prop
        {
            public string Kind;
            /// <summary>Centre on the ground (metres, map coordinates, design eighth).</summary>
            public double X, Z;
            /// <summary>Footprint radius (metres): nothing else within it.</summary>
            public double Radius;
        }

        public sealed class Plan
        {
            /// <summary>Every prop sub-cell in the design eighth (map sub-cell coordinates).</summary>
            public readonly List<Cell> Cells = new List<Cell>();
            public readonly List<Prop> Props = new List<Prop>();
        }

        private static readonly object Lock = new object();
        private static Plan _plan;
        private static volatile Dictionary<long, byte[]> _kinds;

        /// <summary>The plains of the design eighth, worked out once per run (the same every time).</summary>
        public static Plan GetPlan()
        {
            lock (Lock)
            {
                if (_plan == null) _plan = MakePlan();
                return _plan;
            }
        }

        /// <summary>Puts the plains into a battle royale ground grid (all eight eighths).</summary>
        public static void Stamp(SubGrid g)
        {
            Plan plan = GetPlan();
            int n = g.SizeX;
            foreach (int[] m in BrMap.Sym)
                foreach (Cell c in plan.Cells)
                {
                    Tr(c.X, c.Z, m, n, out int x, out int z);
                    g.Set(x, c.Y, z, SubId.Anchor);
                }
            g.ClearChanges();
        }

        /// <summary>How the prop sub-cell at <paramref name="c"/> is drawn (stone for anything not a prop).</summary>
        public static byte KindAt(Int3 c)
        {
            Dictionary<long, byte[]> kinds = _kinds;
            if (kinds == null)
                lock (Lock)
                {
                    if (_kinds == null) _kinds = MakeKinds(GetPlan(), BrMap.ChunksAcross * SubGrid.ChunkSize);
                    kinds = _kinds;
                }
            int n = SubGrid.ChunkSize;
            long key = ChunkKey(c.X >> 4, (c.Y + 1024) >> 4, c.Z >> 4);
            if (!kinds.TryGetValue(key, out byte[] a)) return KindStone;
            return a[(((c.Y + 1024) & (n - 1)) * n + (c.Z & (n - 1))) * n + (c.X & (n - 1))];
        }

        private static long ChunkKey(int cx, int cy, int cz) => ((long)cx << 40) | ((long)cy << 20) | (uint)cz;

        private static Dictionary<long, byte[]> MakeKinds(Plan plan, int n)
        {
            const int s = SubGrid.ChunkSize;
            var d = new Dictionary<long, byte[]>();
            foreach (int[] m in BrMap.Sym)
                foreach (Cell c in plan.Cells)
                {
                    Tr(c.X, c.Z, m, n, out int x, out int z);
                    int y = c.Y + 1024;
                    long key = ChunkKey(x >> 4, y >> 4, z >> 4);
                    if (!d.TryGetValue(key, out byte[] a)) d[key] = a = new byte[s * s * s];
                    a[((y & (s - 1)) * s + (z & (s - 1))) * s + (x & (s - 1))] = c.Kind;
                }
            return d;
        }

        /// <summary>A sub-cell column under one of the symmetries: odd centred coordinates (2x + 1 - n) turn exactly.</summary>
        public static void Tr(int x, int z, int[] m, int n, out int tx, out int tz)
        {
            int u = 2 * x + 1 - n, v = 2 * z + 1 - n;
            int a = m[0] * u + m[1] * v, b = m[2] * u + m[3] * v;
            tx = (a + n - 1) / 2;
            tz = (b + n - 1) / 2;
        }

        // ================================================================ Placement

        private static Plan MakePlan()
        {
            var plan = new Plan();
            BrushWorld src = BrMap.BuildDesignEighth(out GreyboxInfo part, out List<Vec3d> keepClear, out Aabb region);
            double c = BrMap.Size / 2;
            var rng = new Rng(0x5EED7EE5);
            var placed = new List<Prop>();

            bool Free(double x, double z, double r)
            {
                double u = x - c, v = z - c;
                // Inside the eighth with room to spare: its twins never touch it.
                if (v - r < 1.5 || (u - v) / Math.Sqrt(2) - r < 1.5) return false;
                if (u + r > BrMap.RimStart - 3) return false;
                if (u - r < 70 && v - r < 45) return false; // the town's plaza and streets stay open
                // Clear of buildings (and their doorways), the spawn region and its pad walks, everything solid.
                foreach (Aabb b in part.Buildings)
                    if (x + r > b.Min.X - 5 && x - r < b.Max.X + 5 && z + r > b.Min.Z - 5 && z - r < b.Max.Z + 5) return false;
                if (x + r > region.Min.X - 20 && x - r < region.Max.X + 6 && z + r > region.Min.Z - 6 && z - r < region.Max.Z + 6) return false;
                var box = new Aabb(new Vec3d(x - r - 2.5, -0.5, z - r - 2.5), new Vec3d(x + r + 2.5, 12, z + r + 2.5));
                if (src.BoxOverlapsSolid(box)) return false;
                foreach (Vec3d k in keepClear)
                    if (Sq(k.X - x) + Sq(k.Z - z) < Sq(r + 4)) return false;
                foreach (Prop p in placed)
                    if (Sq(p.X - x) + Sq(p.Z - z) < Sq(p.Radius + r + 1.2)) return false;
                return true;
            }

            void Add(string kind, double x, double z, double r) => placed.Add(new Prop { Kind = kind, X = x, Z = z, Radius = r });

            double uMin = 20, uMax = BrMap.RimStart - 4;
            double RandU() => uMin + rng.Unit() * (uMax - uMin);

            // Groves: a few trees round a centre, bushes at their edges.
            for (int gi = 0, made = 0; gi < 400 && made < 7; gi++)
            {
                double u = RandU(), v = rng.Unit() * u;
                double gx = c + u, gz = c + v;
                if (!Free(gx, gz, 6)) continue;
                made++;
                int want = 4 + rng.Range(0, 5);
                for (int t = 0, k = 0; t < 60 && k < want; t++)
                {
                    double a = rng.Unit() * Math.PI * 2, d = 3 + rng.Unit() * 13;
                    double x = gx + Math.Cos(a) * d, z = gz + Math.Sin(a) * d;
                    int seed = rng.Next();
                    double r = TreeRadius(seed);
                    if (!Free(x, z, r)) continue;
                    Tree(plan, x, z, seed);
                    Add("tree", x, z, r);
                    k++;
                }
                for (int t = 0, k = 0; t < 30 && k < 3; t++)
                {
                    double a = rng.Unit() * Math.PI * 2, d = 10 + rng.Unit() * 10;
                    double x = gx + Math.Cos(a) * d, z = gz + Math.Sin(a) * d;
                    int seed = rng.Next();
                    if (!Free(x, z, 1.6)) continue;
                    Bush(plan, x, z, seed);
                    Add("bush", x, z, 1.6);
                    k++;
                }
            }
            // Lone trees.
            for (int t = 0, k = 0; t < 600 && k < 16; t++)
            {
                double u = RandU(), v = rng.Unit() * u;
                int seed = rng.Next();
                double r = TreeRadius(seed);
                if (!Free(c + u, c + v, r)) continue;
                Tree(plan, c + u, c + v, seed);
                Add("tree", c + u, c + v, r);
                k++;
            }
            // Boulders, alone or in twos and threes: cover in the open fields.
            for (int t = 0, k = 0; t < 600 && k < 16; t++)
            {
                double u = RandU(), v = rng.Unit() * u;
                double x = c + u, z = c + v;
                if (!Free(x, z, 3.2)) continue;
                int count = 1 + rng.Range(0, 3);
                int s0 = rng.Next();
                for (int i = 0; i < count; i++)
                {
                    double a = Hash01(s0, i, 1) * Math.PI * 2, d = i == 0 ? 0 : 1.6 + Hash01(s0, i, 2) * 0.8;
                    Rock(plan, x + Math.Cos(a) * d, z + Math.Sin(a) * d, Hash(s0, i, 3));
                }
                Add("rocks", x, z, 3.2);
                k++;
            }
            // Bushes along the way: single, low, solid.
            for (int t = 0, k = 0; t < 600 && k < 24; t++)
            {
                double u = RandU(), v = rng.Unit() * u;
                int seed = rng.Next();
                if (!Free(c + u, c + v, 1.6)) continue;
                Bush(plan, c + u, c + v, seed);
                Add("bush", c + u, c + v, 1.6);
                k++;
            }
            plan.Props.AddRange(placed);
            // One list, no repeats (lobes overlap).
            var seen = new HashSet<long>();
            var unique = new List<Cell>(plan.Cells.Count);
            foreach (Cell cell in plan.Cells)
            {
                long key = ((long)cell.X << 32) | ((long)(cell.Y + 1024) << 16) | (uint)cell.Z;
                if (seen.Add(key)) unique.Add(cell);
                else if (cell.Kind == KindWood) // wood wins over leaves where they meet
                    for (int i = unique.Count - 1; i >= 0; i--)
                        if (unique[i].X == cell.X && unique[i].Y == cell.Y && unique[i].Z == cell.Z) { var w = unique[i]; w.Kind = KindWood; unique[i] = w; break; }
            }
            plan.Cells.Clear();
            plan.Cells.AddRange(unique);
            return plan;
        }

        private static double TreeRadius(int seed) => (Big(seed) ? 13 : 10) * SubGrid.SubSize + 0.6;
        private static bool Big(int seed) => Hash(seed, 0, 9) % 100 < 35;

        // ================================================================ Shapes (sub-cells, ground top at y 0)

        /// <summary>A tree: trunk sunk into the ground with a few root knuckles, a lumpy canopy of overlapping blobs.</summary>
        private static void Tree(Plan plan, double x, double z, int seed)
        {
            const double s = SubGrid.SubSize;
            bool big = Big(seed);
            int w = big ? 3 : 2;
            int tx = (int)Math.Floor(x / s) - w / 2, tz = (int)Math.Floor(z / s) - w / 2;
            int trunkTop = (big ? 15 : 12) + (int)(Hash(seed, 1, 1) % 3);
            for (int y = -4; y <= trunkTop; y++)
                for (int dz = 0; dz < w; dz++)
                    for (int dx = 0; dx < w; dx++)
                        Put(plan, tx + dx, y, tz + dz, KindWood);
            for (int dz = -1; dz <= w; dz++)
                for (int dx = -1; dx <= w; dx++)
                {
                    bool ring = dx == -1 || dz == -1 || dx == w || dz == w;
                    bool corner = (dx == -1 || dx == w) && (dz == -1 || dz == w);
                    if (ring && !corner && Hash(seed, dx + 7, dz + 7) % 100 < 45) Put(plan, tx + dx, 0, tz + dz, KindWood);
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
                            if (dx * dx + dy * dy + dz * dz <= 0.86 + wobble) { Put(plan, qx, y, qz, KindLeaves); break; }
                        }
                    }
        }

        /// <summary>A boulder: a lumpy, squashed blob sunk a little into the ground, 1-1.8 m tall.</summary>
        private static void Rock(Plan plan, double x, double z, uint seed)
        {
            const double s = SubGrid.SubSize;
            double rx = 3.5 + (seed % 5), rz = 3.5 + (seed / 5 % 5), ry = 3.5 + (seed / 25 % 3);
            double cx = x / s, cz = z / s, cy = -1;
            double tilt = (seed / 75 % 100) / 100.0 * 0.4 - 0.2;
            int ext = (int)Math.Ceiling(Math.Max(rx, rz) * 1.2) + 1;
            for (int y = -3; y < (int)(cy + ry * 1.2) + 1; y++)
                for (int qz = (int)cz - ext; qz <= (int)cz + ext; qz++)
                    for (int qx = (int)cx - ext; qx <= (int)cx + ext; qx++)
                    {
                        double px = qx + 0.5 - cx, py = y + 0.5 - cy - tilt * (qx + 0.5 - cx), pz = qz + 0.5 - cz;
                        double lump = (Hash((int)seed, qx * 7 + (y & 3), qz * 3 + (y >> 2)) % 100) / 100.0 * 0.22;
                        double d = Sq(px / rx) + Sq(py / ry) + Sq(pz / rz);
                        if (d <= 0.9 + lump) Put(plan, qx, y, qz, KindStone);
                    }
        }

        /// <summary>A bush: a solid low dome of leaves, 1-1.4 m tall (hides a crouching player and stops bullets).</summary>
        private static void Bush(Plan plan, double x, double z, int seed)
        {
            const double s = SubGrid.SubSize;
            double r = 3.5 + (Hash(seed, 1, 1) % 3) * 0.7, h = 3.4 + (Hash(seed, 2, 2) % 2);
            double cx = x / s, cz = z / s;
            int ext = (int)Math.Ceiling(r * 1.2) + 1;
            for (int y = -1; y < (int)h + 1; y++)
                for (int qz = (int)cz - ext; qz <= (int)cz + ext; qz++)
                    for (int qx = (int)cx - ext; qx <= (int)cx + ext; qx++)
                    {
                        double px = qx + 0.5 - cx, py = y + 0.5, pz = qz + 0.5 - cz;
                        double lump = (Hash(seed, qx * 5 + y, qz) % 100) / 100.0 * 0.25;
                        if (Sq(px / r) + Sq(py / h) + Sq(pz / r) <= 0.85 + lump) Put(plan, qx, y, qz, KindLeaves);
                    }
        }

        private static void Put(Plan plan, int x, int y, int z, byte kind)
        {
            if (y >= TopSubs || y < 0) return; // below the ground top it is solid ground anyway
            plan.Cells.Add(new Cell { X = x, Y = y, Z = z, Kind = kind });
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
