using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Where the generator put things, for spawning and the game layer.</summary>
    public sealed class MapInfo
    {
        public int Seed;
        public int SizeTiles;
        /// <summary>Spawn feet positions per team (index 0 = team 1, index 1 = team 2).</summary>
        public readonly List<Vec3d>[] TeamSpawns = { new List<Vec3d>(), new List<Vec3d>() };
        /// <summary>Team 1 faces +Z (north), team 2 faces -Z.</summary>
        public double[] SpawnYaw = { 0, Math.PI };
        /// <summary>Golden ball spawn, centre of the map.</summary>
        public Vec3d BallSpawn;
        /// <summary>Trees: solid anchor terrain (trunk and canopy block walking and bullets), listed for the look.</summary>
        public readonly List<TreeInfo> Trees = new List<TreeInfo>();
    }

    /// <summary>
    /// One tree. Every listed sub-cell is an anchor in the grid, so it collides and stops bullets like any rock (what you
    /// see is what you hit); the lists only tell the game layer which anchors to draw as bark and which as foliage.
    /// </summary>
    public sealed class TreeInfo
    {
        /// <summary>Trunk base corner (sub-cells) and trunk width.</summary>
        public int X, Z, Width;
        /// <summary>Sub-cell height of the ground the tree stands on (first empty sub-cell above it).</summary>
        public int GroundY;
        public readonly List<Int3> Wood = new List<Int3>();
        public readonly List<Int3> Leaves = new List<Int3>();
    }

    /// <summary>
    /// Seeded arena generator at sub-cell detail, integer only, so server and clients build the same terrain from the
    /// seed. The map has two-fold rotational symmetry (fair for both teams): rolling ground with walkable 0.3 m steps,
    /// irregular boulders for cover, raised shelves with natural ramps, anchor spawn pads, a central anchor plaza for the
    /// ball, landmark pillars and an anchor border wall.
    /// </summary>
    public static class MapGen
    {
        public const int DefaultSizeTiles = 64;

        public static SubGrid Generate(int seed, out MapInfo info, int sizeTiles = DefaultSizeTiles)
        {
            if (sizeTiles % 4 != 0 || sizeTiles < 32) throw new ArgumentException("Map size must be a multiple of 4 tiles, at least 32.");
            int n = sizeTiles * SubGrid.TileSubs;          // sub-cells across
            var g = new SubGrid(n / SubGrid.ChunkSize, 3, n / SubGrid.ChunkSize, -16);
            g.FillBaseGround();
            info = new MapInfo { Seed = seed, SizeTiles = sizeTiles };

            // ---- Height field (sub-cells above ground), generated for one half and rotated for the other ----
            var height = new int[n * n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    if (!InFirstHalf(x, z, n)) continue;
                    int h = Noise(seed, x, z, 32) * 3 / 256 + Noise(seed + 17, x, z, 12) * 2 / 256 - 2; // -2 .. 3
                    h = Math.Max(-2, Math.Min(3, h));
                    height[z * n + x] = h;
                }
            Mirror(height, n);
            // Walkable everywhere: neighbouring columns differ by at most one sub-cell (45 degree slopes).
            RelaxSlopes(height, n, 1);

            // Flatten spawn pads and the centre plaza to ground level.
            int pad = 3 * SubGrid.TileSubs;
            int mid = n / 2;
            FlattenDisc(height, n, mid, mid, 4 * SubGrid.TileSubs, 0);
            int spawnZ = 6 * SubGrid.TileSubs;
            FlattenDisc(height, n, mid, spawnZ, pad + 6, 0);
            Mirror(height, n); // keep the flattening exactly symmetric
            RelaxSlopes(height, n, 1);

            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int h = height[z * n + x];
                    if (h > 0) g.Fill(new Int3(x, 0, z), new Int3(x, h - 1, z), SubId.Natural);
                    else if (h < 0) g.Fill(new Int3(x, h, z), new Int3(x, -1, z), SubId.Empty);
                }

            // ---- Features in the first half, rotated copies in the other ----
            var rng = new HashRng(seed);
            var stamps = new List<Action<Func<int, int, Int3>>>();

            // Boulders: irregular 1-2 level rocks, 1-3 tiles across.
            for (int i = 0; i < 26; i++)
            {
                int cx = rng.Range(3 * 4, n - 3 * 4), cz = rng.Range(3 * 4, mid - 2);
                int r = rng.Range(3, 7);
                int top = rng.Chance(30) ? 2 * SubGrid.TileSubs : SubGrid.TileSubs + rng.Range(-1, 2);
                if (Near(cx, cz, mid, spawnZ, pad + 10) || Near(cx, cz, mid, mid, 4 * 4 + 6)) continue;
                int bseed = rng.Next();
                stamps.Add(map => Boulder(g, height, n, map, cx, cz, r, top, bseed));
            }

            // Raised shelves (2 levels) with a natural ramp up one side.
            for (int i = 0; i < 4; i++)
            {
                int cx = rng.Range(8 * 4, n - 8 * 4), cz = rng.Range(10 * 4, mid - 6 * 4);
                if (Near(cx, cz, mid, spawnZ, pad + 16)) continue;
                int w = rng.Range(3, 5) * 4, d = rng.Range(2, 4) * 4;
                int dir = rng.Range(0, 4);
                stamps.Add(map => Shelf(g, height, n, map, cx, cz, w, d, dir));
            }

            foreach (var s in stamps)
            {
                s((x, z) => new Int3(x, 0, z));
                s((x, z) => new Int3(n - 1 - x, 0, n - 1 - z));
            }

            // ---- Anchors: spawn pads, centre plaza ring markers, landmark pillars, border ----
            FillMirrored(g, n, new Int3(mid - pad / 2 - 2, -4, spawnZ - pad / 2), new Int3(mid + pad / 2 + 1, -1, spawnZ + pad / 2 - 1), SubId.Anchor);
            double size = n * SubGrid.SubSize;
            for (int k = -2; k <= 2; k++)
            {
                var s1 = new Vec3d((mid + k * 2.5) * SubGrid.SubSize, 0, (spawnZ + 0.5) * SubGrid.SubSize);
                info.TeamSpawns[0].Add(s1);
                info.TeamSpawns[1].Add(new Vec3d(size - s1.X, 0, size - s1.Z));
            }
            g.Fill(new Int3(mid - 4, -4, mid - 4), new Int3(mid + 3, -1, mid + 3), SubId.Anchor);
            info.BallSpawn = new Vec3d(mid * SubGrid.SubSize, 0, mid * SubGrid.SubSize);

            // Landmark pillars (anchor, 6 levels), symmetric.
            int[] px = { mid - n * 7 / 32, mid + n * 5 / 32 }, pz = { mid - n * 3 / 32, mid - n * 8 / 32 };
            for (int i = 0; i < px.Length; i++)
            {
                Pillar(g, n, px[i], pz[i]);
            }

            PlaceTrees(g, n, info, seed, mid, spawnZ, pad, px, pz);

            // Border wall, 4 levels, two sub-cells thick.
            int wallTop = 4 * SubGrid.TileSubs - 1;
            g.Fill(new Int3(0, -12, 0), new Int3(n - 1, wallTop, 1), SubId.Anchor);
            g.Fill(new Int3(0, -12, n - 2), new Int3(n - 1, wallTop, n - 1), SubId.Anchor);
            g.Fill(new Int3(0, -12, 0), new Int3(1, wallTop, n - 1), SubId.Anchor);
            g.Fill(new Int3(n - 2, -12, 0), new Int3(n - 1, wallTop, n - 1), SubId.Anchor);

            g.Compact();
            g.ClearChanges();
            g.MarkAllDirty();
            return g;
        }

        // ------------------------------------------------------------------ Features

        private static void Boulder(SubGrid g, int[] height, int n, Func<int, int, Int3> map, int cx, int cz, int r, int top, int seed)
        {
            for (int dz = -r - 2; dz <= r + 2; dz++)
                for (int dx = -r - 2; dx <= r + 2; dx++)
                {
                    // Irregular outline: radius wobbles with a hash per column.
                    int rr = r * r * 4 + (int)(Hash(seed, dx, dz) % (uint)(r * 6 + 1)) - r * 3;
                    if (dx * dx * 4 + dz * dz * 4 > rr) continue;
                    int edge = rr - (dx * dx * 4 + dz * dz * 4);
                    int h = edge < r * 2 ? top - 1 : top; // slightly rounded rim
                    if (Hash(seed + 1, dx, dz) % 9 == 0) h -= 1;
                    Int3 c = map(cx + dx, cz + dz);
                    if (c.X < 2 || c.Z < 2 || c.X >= n - 2 || c.Z >= n - 2) continue;
                    int ground = height[c.Z * n + c.X];
                    if (h <= ground) continue;
                    g.Fill(new Int3(c.X, ground, c.Z), new Int3(c.X, h - 1, c.Z), SubId.Natural);
                }
        }

        private static void Shelf(SubGrid g, int[] height, int n, Func<int, int, Int3> map, int cx, int cz, int w, int d, int dir)
        {
            int top = 2 * SubGrid.TileSubs;
            for (int dz = 0; dz < d; dz++)
                for (int dx = 0; dx < w; dx++)
                {
                    Int3 c = map(cx + dx, cz + dz);
                    if (c.X < 2 || c.Z < 2 || c.X >= n - 2 || c.Z >= n - 2) continue;
                    int ground = height[c.Z * n + c.X];
                    if (top > ground) g.Fill(new Int3(c.X, ground, c.Z), new Int3(c.X, top - 1, c.Z), SubId.Natural);
                }
            // Ramp: a staircase of one sub-cell per sub-cell along one side, 4 sub-cells wide.
            for (int k = 0; k < top; k++)
                for (int j = 0; j < 4; j++)
                {
                    int x, z;
                    switch (dir)
                    {
                        case 0: x = cx - top + k; z = cz + j; break;          // rising toward +X into the shelf's west side
                        case 1: x = cx + w + top - 1 - k; z = cz + j; break;  // rising toward -X
                        case 2: x = cx + j; z = cz - top + k; break;          // rising toward +Z
                        default: x = cx + j; z = cz + d + top - 1 - k; break; // rising toward -Z
                    }
                    Int3 c = map(x, z);
                    if (c.X < 2 || c.Z < 2 || c.X >= n - 2 || c.Z >= n - 2) continue;
                    int ground = height[c.Z * n + c.X];
                    if (k + 1 > ground) g.Fill(new Int3(c.X, ground, c.Z), new Int3(c.X, k, c.Z), SubId.Natural);
                }
        }

        // ------------------------------------------------------------------ Trees

        /// <summary>Lowest canopy sub-cell above the ground the tree stands on: 3.3 m, so nobody walks or jumps into it.</summary>
        public const int CanopyClearSubs = 11;
        /// <summary>Trunk height above its ground (about 3.6 m), into the canopy.</summary>
        public const int TrunkSubs = 12;
        /// <summary>How far a canopy can reach from the trunk, in sub-cells.</summary>
        public const int CanopyReach = 9;

        /// <summary>
        /// A light forest: a few groves plus lone trees on low, flat ground in the first half, rotated into the other
        /// half (fair for both teams). Trees are anchors: never sculpted, never fall, collide and stop bullets. They keep
        /// clear of spawns, the plaza, pillars, boulders and shelves, so the canopy always has headroom underneath.
        /// Uses its own random stream so adding trees does not move anything else on the map.
        /// </summary>
        private static void PlaceTrees(SubGrid g, int n, MapInfo info, int seed, int mid, int spawnZ, int pad, int[] px, int[] pz)
        {
            var rng = new HashRng(seed ^ 0x51ED7EE5);
            var placed = new List<(int x, int z)>();
            // Target per half scales with the map area (7 on the 64-tile arena). Most trees grow near an earlier one
            // (groves), the rest anywhere (lone trees). Bounded attempts, deterministic.
            int target = Math.Max(3, n * n * 7 / (256 * 256));
            for (int k = 0; k < 600 && placed.Count < target; k++)
            {
                int x, z;
                if (placed.Count > 0 && rng.Chance(60))
                {
                    var p = placed[rng.Range(0, placed.Count)];
                    x = p.x + rng.Range(-16, 17);
                    z = p.z + rng.Range(-16, 17);
                }
                else
                {
                    x = rng.Range(4 * 4, n - 4 * 4);
                    z = rng.Range(4 * 4, mid - 3 * 4);
                }
                int w = rng.Chance(25) ? 3 : 2;
                int tseed = rng.Next();
                if (!TreeFits(g, n, x, z, w, mid, spawnZ, pad, px, pz, placed, out int ground)) continue;
                placed.Add((x + w / 2, z + w / 2));
                var a = new TreeInfo { X = x, Z = z, Width = w, GroundY = ground };
                var b = new TreeInfo { X = n - x - w, Z = n - z - w, Width = w, GroundY = ground };
                BuildTree(g, n, a, b, tseed);
                info.Trees.Add(a);
                info.Trees.Add(b);
            }
        }

        private static bool TreeFits(SubGrid g, int n, int x, int z, int w, int mid, int spawnZ, int pad, int[] px, int[] pz,
                                     List<(int x, int z)> placed, out int ground)
        {
            ground = 0;
            const int r = CanopyReach;
            if (x - r < 4 || z - r < 4 || x + w + r >= n - 4) return false;
            if (z + w + r >= mid - 1) return false; // never reaches its own rotated copy
            int cx = x + w / 2, cz = z + w / 2;
            if (Near(cx, cz, mid, spawnZ, pad + 18) || Near(cx, cz, mid, mid, 4 * 4 + 14)) return false;
            for (int i = 0; i < px.Length; i++)
                if (Near(cx, cz, px[i], pz[i], 14) || Near(cx, cz, n - 1 - px[i], n - 1 - pz[i], 14)) return false;
            foreach (var p in placed)
                if (Near(cx, cz, p.x, p.z, 11)) return false;
            // Trunk on low, flat natural ground; under the canopy nothing anchored (the canopy lifts over rocks and shelves).
            ground = Top(g, x, z);
            if (ground > 2 || ground < -2) return false;
            for (int dz = -r; dz <= r + w; dz++)
                for (int dx = -r; dx <= r + w; dx++)
                {
                    int t = Top(g, x + dx, z + dz);
                    bool trunk = dx >= -1 && dx <= w && dz >= -1 && dz <= w;
                    if (trunk && t != ground) return false;
                    if (t > ground + 9) return false;
                    if (g.Get(x + dx, t - 1, z + dz) == SubId.Anchor) return false;
                }
            return true;
        }

        /// <summary>First empty sub-cell above the column's terrain (the surface height in sub-cells).</summary>
        private static int Top(SubGrid g, int x, int z)
        {
            for (int y = g.MaxYExclusive - 1; y >= g.MinY; y--)
                if (g.Get(x, y, z) != SubId.Empty) return y + 1;
            return g.MinY;
        }

        private static void BuildTree(SubGrid g, int n, TreeInfo a, TreeInfo b, int seed)
        {
            int x = a.X, z = a.Z, w = a.Width, ground = a.GroundY;
            int trunkTop = ground + TrunkSubs - 1 + (int)(Hash(seed, 1, 1) % 3);
            void Put(int cx, int cy, int cz, bool wood)
            {
                if (cy >= g.MaxYExclusive) return;
                var c = new Int3(cx, cy, cz);
                var m = new Int3(n - 1 - cx, cy, n - 1 - cz);
                if (cy >= ground && g.Get(c) == SubId.Anchor) return; // already part of this tree
                g.Set(c, SubId.Anchor);
                g.Set(m, SubId.Anchor);
                if (cy < ground) return; // buried root: never drawn
                (wood ? a.Wood : a.Leaves).Add(c);
                (wood ? b.Wood : b.Leaves).Add(m);
            }

            // Trunk, sunk into the ground so it reads as rooted, with a few root knuckles (0.3 m, walkable).
            for (int y = ground - 4; y <= trunkTop; y++)
                for (int dz = 0; dz < w; dz++)
                    for (int dx = 0; dx < w; dx++)
                        Put(x + dx, y, z + dz, true);
            for (int dz = -1; dz <= w; dz++)
                for (int dx = -1; dx <= w; dx++)
                {
                    bool ring = dx == -1 || dz == -1 || dx == w || dz == w;
                    bool corner = (dx == -1 || dx == w) && (dz == -1 || dz == w);
                    if (ring && !corner && Hash(seed, dx + 7, dz + 7) % 100 < 45) Put(x + dx, ground, z + dz, true);
                }

            // Canopy: a few overlapping lumpy blobs around the trunk top, cut flat at the clearance height.
            double tx = x + w * 0.5, tz = z + w * 0.5;
            int lobes = 3 + (int)(Hash(seed, 2, 2) % 2);
            var lobe = new (double x, double y, double z, double r)[lobes + 1];
            lobe[0] = (tx, trunkTop + 2.5, tz, 4.5 + (Hash(seed, 3, 3) % 3) * 0.5);
            for (int i = 1; i <= lobes; i++)
            {
                double ang = (Hash(seed, 10 + i, 0) % 360) * Math.PI / 180.0;
                double dist = 2.5 + (Hash(seed, 20 + i, 0) % 20) / 10.0;
                lobe[i] = (tx + Math.Cos(ang) * dist, trunkTop + 0.5 + (Hash(seed, 30 + i, 0) % 30) / 10.0, tz + Math.Sin(ang) * dist,
                           3.0 + (Hash(seed, 40 + i, 0) % 15) / 10.0);
            }
            // Per column the canopy starts at least 3.3 m above the tree's ground and 2.7 m above whatever is under it.
            int span = w + 2 * CanopyReach + 1;
            var lowest = new int[span * span];
            for (int cz = z - CanopyReach; cz <= z + w + CanopyReach; cz++)
                for (int cx = x - CanopyReach; cx <= x + w + CanopyReach; cx++)
                    lowest[(cz - z + CanopyReach) * span + cx - x + CanopyReach] = Math.Max(ground + CanopyClearSubs, Top(g, cx, cz) + 9);
            for (int y = ground + CanopyClearSubs; y < g.MaxYExclusive; y++)
                for (int cz = z - CanopyReach; cz <= z + w + CanopyReach; cz++)
                    for (int cx = x - CanopyReach; cx <= x + w + CanopyReach; cx++)
                    {
                        bool overTrunk = cx >= x && cx < x + w && cz >= z && cz < z + w;
                        if (!overTrunk && y < lowest[(cz - z + CanopyReach) * span + cx - x + CanopyReach]) continue;
                        double qx = cx + 0.5, qy = y + 0.5, qz = cz + 0.5;
                        double wobble = (Hash(seed + 5, cx * 131 + y, cz) % 100) / 100.0 * 0.28;
                        foreach (var l in lobe)
                        {
                            double dx = (qx - l.x) / l.r, dy = (qy - l.y) / (l.r * 0.78), dz = (qz - l.z) / l.r;
                            if (dx * dx + dy * dy + dz * dz <= 0.86 + wobble) { Put(cx, y, cz, false); break; }
                        }
                    }
        }

        private static void Pillar(SubGrid g, int n, int x, int z) =>
            FillMirrored(g, n, new Int3(x - 3, -4, z - 3), new Int3(x + 2, 6 * SubGrid.TileSubs - 1, z + 2), SubId.Anchor);

        /// <summary>Fills a box and its rotated twin (x, z) -> (n-1-x, n-1-z).</summary>
        private static void FillMirrored(SubGrid g, int n, Int3 min, Int3 max, ushort id)
        {
            g.Fill(min, max, id);
            g.Fill(new Int3(n - 1 - max.X, min.Y, n - 1 - max.Z), new Int3(n - 1 - min.X, max.Y, n - 1 - min.Z), id);
        }

        // ------------------------------------------------------------------ Height field helpers

        private static bool InFirstHalf(int x, int z, int n) => z < n / 2;

        private static void Mirror(int[] h, int n)
        {
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    if (!InFirstHalf(x, z, n)) h[z * n + x] = h[(n - 1 - z) * n + (n - 1 - x)];
        }

        /// <summary>Lowers columns until neighbours differ by at most maxStep (symmetric, order-independent).</summary>
        private static void RelaxSlopes(int[] h, int n, int maxStep)
        {
            bool changed = true;
            var next = new int[h.Length];
            while (changed)
            {
                changed = false;
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        int v = h[z * n + x];
                        if (x > 0) v = Math.Min(v, h[z * n + x - 1] + maxStep);
                        if (x < n - 1) v = Math.Min(v, h[z * n + x + 1] + maxStep);
                        if (z > 0) v = Math.Min(v, h[(z - 1) * n + x] + maxStep);
                        if (z < n - 1) v = Math.Min(v, h[(z + 1) * n + x] + maxStep);
                        next[z * n + x] = v;
                        if (v != h[z * n + x]) changed = true;
                    }
                Array.Copy(next, h, h.Length);
            }
        }

        private static void FlattenDisc(int[] h, int n, int cx, int cz, int r, int value)
        {
            for (int z = Math.Max(0, cz - r); z <= Math.Min(n - 1, cz + r); z++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(n - 1, cx + r); x++)
                    if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= r * r) h[z * n + x] = value;
        }

        private static bool Near(int x, int z, int cx, int cz, int r) => (x - cx) * (x - cx) + (z - cz) * (z - cz) <= r * r;

        /// <summary>Smooth integer value noise in 0..255, cells of the given size (bilinear, integer only).</summary>
        private static int Noise(int seed, int x, int z, int cell)
        {
            int gx = Div(x, cell), gz = Div(z, cell);
            int fx = x - gx * cell, fz = z - gz * cell;
            int a = (int)(Hash(seed, gx, gz) & 255), b = (int)(Hash(seed, gx + 1, gz) & 255);
            int c = (int)(Hash(seed, gx, gz + 1) & 255), d = (int)(Hash(seed, gx + 1, gz + 1) & 255);
            int top = a * (cell - fx) + b * fx, bottom = c * (cell - fx) + d * fx;
            return (top * (cell - fz) + bottom * fz) / (cell * cell);
        }

        private static int Div(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

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

        private sealed class HashRng
        {
            private uint _s;
            public HashRng(int seed) { _s = (uint)seed * 2654435761u + 1; }
            public int Next()
            {
                _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
                return (int)(_s & 0x7FFFFFFF);
            }
            public int Range(int lo, int hiExclusive) => lo + Next() % Math.Max(1, hiExclusive - lo);
            public bool Chance(int percent) => Next() % 100 < percent;
        }
    }
}
