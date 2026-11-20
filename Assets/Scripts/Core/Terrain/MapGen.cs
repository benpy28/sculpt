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
