using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Tests.Fuzz;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// "What you see is what you hit": random rays against the drawn faces (exactly the faces the mesher emits,
    /// as fans of triangles) must meet the surface at the same distance as TerrainGeometry.Raycast, which bullets and
    /// sculpt aiming use; and the point just in front of every drawn hit is free for a box while the point just past it
    /// is solid, so movement collides with the same surface. Random occupancy reaches every dual-cell configuration.
    /// </summary>
    [Category("Fuzz")]
    public class RenderEqualsHitTests
    {
        private const double S = SubGrid.SubSize;

        private static IEnumerable<int> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 6;
            for (int i = 0; i < n; i++) yield return 7 + i * 6151;
        }

        private struct Tri
        {
            public Vec3d A, B, C, N;
        }

        private static SubGrid RandomRock(Rng r, double density)
        {
            var g = new SubGrid(2, 2, 2, 0);
            for (int y = 6; y < 26; y++)
                for (int z = 6; z < 26; z++)
                    for (int x = 6; x < 26; x++)
                        if (r.Chance(density)) g.Set(x, y, z, r.Chance(0.5) ? SubId.Natural : (ushort)(SubId.FirstPiece + r.Int(0, 5)));
            // A few solid slabs and holes, like sculpted terrain.
            for (int k = 0; k < 6; k++)
            {
                int x = r.Int(6, 20), y = r.Int(6, 20), z = r.Int(6, 20);
                g.Fill(new Int3(x, y, z), new Int3(x + r.Int(1, 6), y + r.Int(1, 4), z + r.Int(1, 6)), r.Chance(0.7) ? SubId.Natural : SubId.Empty);
            }
            return g;
        }

        private static List<Tri> DrawnTriangles(SubGrid g)
        {
            var tris = new List<Tri>();
            for (int vy = 5; vy <= 27; vy++)
                for (int vz = 5; vz <= 27; vz++)
                    for (int vx = 5; vx <= 27; vx++)
                    {
                        int cfg = TerrainShape.ConfigAt(g, vx, vy, vz);
                        if (cfg == 0) continue;
                        TerrainShape.Poly p = TerrainShape.Get(cfg);
                        Vec3d m = TerrainShape.CellMin(vx, vy, vz);
                        foreach (var f in p.Faces)
                        {
                            if (!TerrainShape.IsFaceVisible(g, vx, vy, vz, p, f)) continue;
                            Vec3d v0 = m + f.Verts[0] * TerrainShape.H;
                            for (int i = 1; i + 1 < f.Verts.Length; i++)
                                tris.Add(new Tri { A = v0, B = m + f.Verts[i] * TerrainShape.H, C = m + f.Verts[i + 1] * TerrainShape.H, N = f.Normal });
                        }
                    }
            return tris;
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Moller-Trumbore, double sided. Returns t or -1.</summary>
        private static double Intersect(in Tri t, Vec3d o, Vec3d d)
        {
            Vec3d e1 = t.B - t.A, e2 = t.C - t.A;
            Vec3d pv = Cross(d, e2);
            double det = e1.Dot(pv);
            if (Math.Abs(det) < 1e-14) return -1;
            double inv = 1.0 / det;
            Vec3d tv = o - t.A;
            double u = tv.Dot(pv) * inv;
            if (u < -1e-9 || u > 1 + 1e-9) return -1;
            Vec3d qv = Cross(tv, e1);
            double v = d.Dot(qv) * inv;
            if (v < -1e-9 || u + v > 1 + 1e-9) return -1;
            double tt = e2.Dot(qv) * inv;
            return tt > 1e-9 ? tt : -1;
        }

        private static bool SolidAt(SubGrid g, Vec3d p)
        {
            const double e = 1e-5;
            return g.BoxOverlapsSolid(new Aabb(p - new Vec3d(e, e, e), p + new Vec3d(e, e, e)));
        }

        [TestCaseSource(nameof(Seeds))]
        public void DrawnSurfaceIsTheHitSurface(int seed)
        {
            var r = new Rng((ulong)seed);
            foreach (double density in new[] { 0.15, 0.35, 0.6 })
            {
                SubGrid g = RandomRock(r, density);
                List<Tri> tris = DrawnTriangles(g);
                Assert.That(tris.Count, Is.GreaterThan(100));
                double lo = 7 * S, hi = 25 * S;
                int compared = 0, misses = 0;

                for (int i = 0; i < 800; i++)
                {
                    var o = new Vec3d(r.Range(lo, hi), r.Range(lo, hi), r.Range(lo, hi));
                    if (SolidAt(g, o)) continue;
                    Vec3d d = new Vec3d(r.Range(-1, 1), r.Range(-1, 1), r.Range(-1, 1)).Normalized;
                    if (d.LengthSquared == 0) continue;
                    const double maxDist = 2.5;

                    double best = double.PositiveInfinity;
                    var ts = new List<double>();
                    foreach (var t in tris)
                    {
                        double tt = Intersect(t, o, d);
                        if (tt <= 0) continue;
                        ts.Add(tt);
                        if (tt < best) best = tt;
                    }
                    // Where the ray leaves the rock again (next drawn crossing), to skip slivers thinner than the probe.
                    double exit = double.PositiveInfinity;
                    foreach (double tt in ts) if (tt > best + 1e-7 && tt < exit) exit = tt;
                    TerrainHit h = g.Raycast(o, d, maxDist);

                    bool meshHit = best <= maxDist;
                    if (meshHit && Math.Abs(best - maxDist) < 1e-4) continue; // ambiguous at the range limit
                    Vec3d at = o + d * (meshHit ? best : maxDist);
                    bool interior = at.X > 6 * S && at.X < 26 * S && at.Y > 6 * S && at.Y < 26 * S && at.Z > 6 * S && at.Z < 26 * S;
                    if (!interior) continue; // outside the drawn region (grid bounds read as bedrock)

                    compared++;
                    if (!meshHit) misses++;
                    Assert.That(h.Hit, Is.EqualTo(meshHit), $"seed {seed} density {density} ray {i}: drawn {(meshHit ? $"hit at {best:R}" : "miss")}, Raycast {(h.Hit ? $"hit at {h.Distance:R}" : "miss")} (o {Inspect.Fmt(o)} d {Inspect.Fmt(d)})");
                    if (!meshHit) continue;
                    Assert.That(h.Distance, Is.EqualTo(best).Within(1e-6), $"seed {seed} density {density} ray {i}: drawn surface at {best:R} m, Raycast at {h.Distance:R} m");

                    // Collision agrees: free just before the drawn hit, solid just after (skip grazing hits).
                    double cos = Math.Abs(h.Normal.Dot(d));
                    if (cos > 0.05 && exit - best > 1e-3 / cos)
                    {
                        Assert.That(SolidAt(g, o + d * (best - 2e-4 / cos)), Is.False, $"seed {seed} ray {i}: box collides in front of the drawn surface");
                        Assert.That(SolidAt(g, o + d * (best + 2e-4 / cos)), Is.True, $"seed {seed} ray {i}: box passes through the drawn surface at {best:R} (exit {exit:R}, cos {cos:0.###}, normal {h.Normal}, o {Inspect.Fmt(o)} d {Inspect.Fmt(d)})");
                    }
                }
                Assert.That(compared, Is.GreaterThan(40), $"enough rays compared (density {density}: {compared})");
                Assert.That(misses, Is.LessThan(compared), "and most of them hit something");
            }
        }

        /// <summary>Every one of the 256 dual-cell shapes appears in the random rock, so the test above covers them all.</summary>
        [Test]
        public void RandomRockReachesEveryDualCellShape()
        {
            var seen = new bool[TerrainShape.ConfigCount];
            var r = new Rng(99);
            foreach (double density in new[] { 0.15, 0.35, 0.6 })
            {
                SubGrid g = RandomRock(r, density);
                for (int vy = 5; vy <= 27; vy++)
                    for (int vz = 5; vz <= 27; vz++)
                        for (int vx = 5; vx <= 27; vx++)
                            seen[TerrainShape.ConfigAt(g, vx, vy, vz)] = true;
            }
            int missing = 0;
            foreach (bool s in seen) if (!s) missing++;
            Assert.That(missing, Is.Zero, $"{missing} configurations never appear");
        }
    }
}
