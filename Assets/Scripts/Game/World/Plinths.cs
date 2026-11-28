using System;
using System.Collections.Generic;
using Sculpt.Core;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Which ground is a building's stone plinth (map v3): level ground at the building's own floor height within a few
    /// metres of its walls. A bucket grid of the buildings' footprints, so asking costs a handful of checks.
    /// </summary>
    public sealed class Plinths
    {
        private const double Bucket = 16;
        private readonly List<Aabb> _boxes = new List<Aabb>();
        private readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();
        private readonly double _grow;

        public Plinths(IEnumerable<Aabb> buildings, double grow)
        {
            _grow = grow;
            foreach (Aabb b in buildings)
            {
                int k = _boxes.Count;
                _boxes.Add(b);
                int i0 = (int)Math.Floor((b.Min.X - grow) / Bucket), i1 = (int)Math.Floor((b.Max.X + grow) / Bucket);
                int j0 = (int)Math.Floor((b.Min.Z - grow) / Bucket), j1 = (int)Math.Floor((b.Max.Z + grow) / Bucket);
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        long key = ((long)i << 32) ^ (uint)j;
                        if (!_buckets.TryGetValue(key, out List<int> l)) _buckets[key] = l = new List<int>(2);
                        l.Add(k);
                    }
            }
        }

        /// <summary>How much (x, z) with ground height y is paving: 1 on the plinth, falling to 0 over
        /// <paramref name="fade"/> metres past it (on level ground at the building's floor height only). Smooth, so the
        /// shader can draw a soft, ragged edge instead of whole mesh triangles.</summary>
        public float Weight(double x, double z, double y, double fade = 1.5)
        {
            long key = ((long)(int)Math.Floor(x / Bucket) << 32) ^ (uint)(int)Math.Floor(z / Bucket);
            if (!_buckets.TryGetValue(key, out List<int> l)) return 0f;
            double best = 0;
            foreach (int k in l)
            {
                Aabb b = _boxes[k];
                if (Math.Abs(y - b.Min.Y) > 0.05) continue;
                double dx = Math.Max(0, Math.Max(b.Min.X - x, x - b.Max.X));
                double dz = Math.Max(0, Math.Max(b.Min.Z - z, z - b.Max.Z));
                double d = Math.Sqrt(dx * dx + dz * dz) - (_grow - fade * 0.5);
                double w = 1.0 - Math.Max(0, Math.Min(1, d / fade));
                if (w > best) best = w;
            }
            return (float)best;
        }

        /// <summary>True if (x, z) with ground height y is plinth: near a building and level with its ground floor.</summary>
        public bool On(double x, double z, double y)
        {
            long key = ((long)(int)Math.Floor(x / Bucket) << 32) ^ (uint)(int)Math.Floor(z / Bucket);
            if (!_buckets.TryGetValue(key, out List<int> l)) return false;
            foreach (int k in l)
            {
                Aabb b = _boxes[k];
                if (Math.Abs(y - b.Min.Y) > 0.01) continue;
                if (x >= b.Min.X - _grow && x <= b.Max.X + _grow && z >= b.Min.Z - _grow && z <= b.Max.Z + _grow) return true;
            }
            return false;
        }
    }
}
