using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Works out, at aim time, which natural rock a sculpt grabs: the tile you aim at, grown up to one tile out in an
    /// irregular shape that follows the terrain it starts from. Deterministic and pure (changes nothing).
    ///
    /// Rules:
    /// - Band: the one-level layer (4 sub-cells) of the aimed tile.
    /// - Footprint: the aimed tile's 4x4 columns always; around it, a blob reaching 1..4 sub-cells out (one tile at
    ///   most), the reach per compass sector picked by a hash of the tile, so outlines are irregular but fixed.
    ///   Typical size is about 2x2 tiles, at most 3x3.
    /// - "About the same height": a column joins only if its rock top (the solid run up from the band) is within
    ///   PieceHeightTolerance sub-cells of the aimed column's.
    /// - Connected: flood fill through natural rock (6-neighbours) from the aimed sub-cell inside band and footprint.
    /// </summary>
    public static class PieceFinder
    {
        // Reach (in sub-cells beyond the aimed tile) for each of 8 sectors is picked from this table by hash.
        private static readonly int[] Reach = { 1, 2, 2, 2, 3, 3, 2, 4 };

        /// <summary>Fills <paramref name="cells"/> (sorted Y, Z, X) with the natural rock a sculpt aimed at <paramref name="seed"/> grabs.</summary>
        public static void FindNatural(ISubCells g, Int3 seed, SculptConfig cfg, List<Int3> cells)
        {
            cells.Clear();
            if (g.Get(seed.X, seed.Y, seed.Z) != SubId.Natural) return;

            Int3 tile = SubGrid.TileOf(seed);
            int bandLo = tile.Y * SubGrid.TileSubs, bandHi = bandLo + SubGrid.TileSubs - 1;
            int tx0 = tile.X * SubGrid.TileSubs, tz0 = tile.Z * SubGrid.TileSubs;
            int seedTop = RunTop(g, seed.X, seed.Y, seed.Z);

            // Per-sector reach from a hash of the tile.
            var reach = new int[8];
            uint h = Hash((uint)tile.X, (uint)tile.Y, (uint)tile.Z);
            for (int s = 0; s < 8; s++)
            {
                reach[s] = Reach[h & 7];
                h = Hash(h, (uint)s, 0x9E37u);
            }

            // Column mask over the 12 x 12 region (tile +- one tile).
            const int N = 12;
            var allowed = new bool[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    int x = tx0 - 4 + i, z = tz0 - 4 + j;
                    int ox = i < 4 ? 4 - i : (i > 7 ? i - 7 : 0);
                    int oz = j < 4 ? 4 - j : (j > 7 ? j - 7 : 0);
                    int d = Math.Max(ox, oz);
                    if (d > 0)
                    {
                        // Sector by direction from the tile centre (between columns 5.5 and 6.5 of the region).
                        int dx = 2 * i - 11, dz = 2 * j - 11;
                        int sector = Sector(dx, dz);
                        if (d > reach[sector]) continue;
                    }
                    int top = ColumnTop(g, x, z, bandLo, bandHi);
                    if (top == int.MinValue) continue;
                    if (Math.Abs(top - seedTop) > cfg.PieceHeightTolerance) continue;
                    allowed[j * N + i] = true;
                }

            // Flood fill natural rock from the seed.
            var seen = new HashSet<Int3> { seed };
            var stack = new Stack<Int3>();
            stack.Push(seed);
            while (stack.Count > 0)
            {
                Int3 c = stack.Pop();
                cells.Add(c);
                for (int k = 0; k < 6; k++)
                {
                    Int3 n = c + Step(k);
                    if (n.Y < bandLo || n.Y > bandHi) continue;
                    int i = n.X - (tx0 - 4), j = n.Z - (tz0 - 4);
                    if (i < 0 || i >= N || j < 0 || j >= N || !allowed[j * N + i]) continue;
                    if (seen.Contains(n)) continue;
                    if (g.Get(n.X, n.Y, n.Z) != SubId.Natural) continue;
                    seen.Add(n);
                    stack.Push(n);
                }
            }
            cells.Sort(Int3.CompareYZX);
        }

        /// <summary>Top of the solid run going up from y (any solid id).</summary>
        private static int RunTop(ISubCells g, int x, int y, int z)
        {
            int t = y;
            for (int k = 0; k < 64 && g.Get(x, t + 1, z) != SubId.Empty; k++) t++;
            return t;
        }

        /// <summary>Rock top of a column for the band: the run through the highest natural sub-cell in the band.</summary>
        private static int ColumnTop(ISubCells g, int x, int z, int bandLo, int bandHi)
        {
            for (int y = bandHi; y >= bandLo; y--)
                if (g.Get(x, y, z) == SubId.Natural) return RunTop(g, x, y, z);
            return int.MinValue;
        }

        private static int Sector(int dx, int dz)
        {
            // 8 sectors around the compass, exact integer test (no trig).
            int ax = Math.Abs(dx), az = Math.Abs(dz);
            bool diag = 5 * Math.Min(ax, az) > 2 * Math.Max(ax, az); // within ~22.5 degrees of a diagonal
            if (!diag)
            {
                if (ax >= az) return dx > 0 ? 0 : 4;
                return dz > 0 ? 2 : 6;
            }
            if (dx > 0) return dz > 0 ? 1 : 7;
            return dz > 0 ? 3 : 5;
        }

        private static Int3 Step(int k)
        {
            switch (k)
            {
                case 0: return new Int3(1, 0, 0);
                case 1: return new Int3(-1, 0, 0);
                case 2: return new Int3(0, 1, 0);
                case 3: return new Int3(0, -1, 0);
                case 4: return new Int3(0, 0, 1);
                default: return new Int3(0, 0, -1);
            }
        }

        private static uint Hash(uint a, uint b, uint c)
        {
            uint h = a * 0x8DA6B343u ^ b * 0xD8163841u ^ c * 0xCB1AB31Fu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }
    }
}
