using System;
using System.Collections.Generic;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// SplitMix64. Our own generator so fixed seeds give the same sequence on every .NET runtime,
    /// which System.Random does not promise.
    /// </summary>
    internal sealed class Rng
    {
        private ulong _s;

        public Rng(ulong seed) { _s = seed; }

        public ulong Next()
        {
            ulong z = (_s += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Integer in [lo, hiExclusive).</summary>
        public int Int(int lo, int hiExclusive) => lo + (int)(Next() % (ulong)(hiExclusive - lo));

        /// <summary>Double in [0, 1).</summary>
        public double Double() => (Next() >> 11) * (1.0 / (1UL << 53));

        public double Range(double lo, double hi) => lo + (hi - lo) * Double();

        public bool Chance(double p) => Double() < p;

        public T Pick<T>(T[] items) => items[Int(0, items.Length)];
    }

    /// <summary>Read-only inspection helpers shared by the fuzz and invariant tests.</summary>
    internal static class Inspect
    {
        public const double B = VoxelGrid.BlockSize;

        /// <summary>FNV-1a over every field of every cell in a Y band of the grid.</summary>
        public static ulong HashGrid(VoxelGrid g, int yMin, int yMaxInclusive)
        {
            ulong h = 14695981039346656037UL;
            for (int y = yMin; y <= yMaxInclusive; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                        h = Mix(h, Pack(g.Get(x, y, z)));
            return h;
        }

        /// <summary>Every field of a block in one value.</summary>
        public static ulong Pack(Block b) =>
            (ulong)b.Kind | ((b.IsRamp ? 1UL : 0UL) << 2) | ((ulong)b.RampLow << 3) | ((ulong)b.ReinforceTeam << 6) | ((ulong)b.TintTeam << 14)
            ^ ((ulong)(uint)b.ReinforceUntilTick << 22) ^ ((ulong)(uint)b.TintTick << 41);

        public static ulong Mix(ulong h, ulong v)
        {
            h ^= v;
            h *= 0x100000001B3UL;
            return h ^ (h >> 29);
        }

        public static ulong MixDouble(ulong h, double d) => Mix(h, (ulong)BitConverter.DoubleToInt64Bits(d));

        public static int CountNonEmpty(VoxelGrid g, int yMin, int yMaxInclusive)
        {
            int n = 0;
            for (int y = yMin; y <= yMaxInclusive; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                        if (!g.Get(x, y, z).IsEmpty) n++;
            return n;
        }

        /// <summary>
        /// Lowest feet height where the player's box clears the terrain under it, by bisection.
        /// Valid because columns are solid from the bottom up (no overhangs), so overlap is monotone in height.
        /// </summary>
        public static bool DropToGround(VoxelGrid g, PlayerBody p, double lo = -4.0, double hi = 7.0)
        {
            var at = new Func<double, bool>(y => VoxelGeometry.BoxOverlapsSolid(g, p.BoundsAt(new Vec3d(p.Position.X, y, p.Position.Z))));
            if (at(hi)) return false;
            if (!at(lo)) { p.Position = p.Position.WithY(lo); return true; }
            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) * 0.5;
                if (at(mid)) lo = mid; else hi = mid;
            }
            p.Position = p.Position.WithY(hi);
            return !at(hi);
        }

        public static bool IsInsideTerrain(VoxelGrid g, PlayerBody p) => VoxelGeometry.BoxOverlapsSolid(g, p.Bounds);

        /// <summary>Exact point-in-block test written independently of VoxelGeometry (closed cell, wedge for ramps).</summary>
        public static bool PointInBlock(Block b, Int3 cell, Vec3d p)
        {
            if (b.IsEmpty) return false;
            Aabb c = VoxelGrid.CellBounds(cell);
            if (p.X < c.Min.X || p.X > c.Max.X || p.Y < c.Min.Y || p.Y > c.Max.Y || p.Z < c.Min.Z || p.Z > c.Max.Z) return false;
            if (!b.IsRamp) return true;
            double s; // metres from the high edge toward the low edge
            switch (b.RampLow)
            {
                case Dir.East: s = p.X - c.Min.X; break;
                case Dir.West: s = c.Max.X - p.X; break;
                case Dir.North: s = p.Z - c.Min.Z; break;
                default: s = c.Max.Z - p.Z; break;
            }
            return (p.Y - c.Min.Y) + s <= B;
        }
    }
}
