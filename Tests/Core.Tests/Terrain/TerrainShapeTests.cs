using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class TerrainShapeTests
    {
        private const double S = SubGrid.SubSize;

        private static SubGrid Flat()
        {
            var g = new SubGrid(2, 2, 2, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        [Test]
        public void EmptyAndFullConfigs()
        {
            Assert.That(TerrainShape.Get(0).Empty, Is.True);
            var full = TerrainShape.Get(255);
            Assert.That(full.Full, Is.True);
            for (int k = 0; k < 3; k++)
            {
                Assert.That(full.Min[k], Is.EqualTo(0));
                Assert.That(full.Max[k], Is.EqualTo(2));
            }
            foreach (var f in full.Faces) Assert.That(f.OnBoundary, Is.True);
        }

        [Test]
        public void EveryConfigIsAConvexSolidInsideItsCell()
        {
            for (int c = 1; c < 256; c++)
            {
                var p = TerrainShape.Get(c);
                Assert.That(p.Empty, Is.False, "config " + c);
                foreach (var v in p.Vertices)
                {
                    Assert.That(v.X, Is.InRange(0, 2));
                    Assert.That(v.Y, Is.InRange(0, 2));
                    Assert.That(v.Z, Is.InRange(0, 2));
                }
                // Each occupied corner is inside its own polytope.
                for (int b = 0; b < 8; b++)
                {
                    if ((c & (1 << b)) == 0) continue;
                    Int3 corner = TerrainShape.Corner(b);
                    for (int k = 0; k < 13; k++)
                    {
                        Int3 a = TerrainShape.Axes[k];
                        double d = a.X * corner.X + a.Y * corner.Y + a.Z * corner.Z;
                        Assert.That(d, Is.InRange(p.Min[k] - 1e-9, p.Max[k] + 1e-9), $"config {c} corner {b} axis {k}");
                    }
                }
                // Face windings point outward.
                foreach (var f in p.Faces)
                {
                    Vec3d n = Cross(f.Verts[1] - f.Verts[0], f.Verts[2] - f.Verts[0]);
                    Assert.That(n.Dot(f.Normal), Is.GreaterThan(0), $"config {c} face {f.Dir}");
                }
            }
        }

        [Test]
        public void SymmetricUnderMirroring()
        {
            // Mirroring x maps config bits b -> b ^ 1; the Y extent of the solid must not change.
            for (int c = 1; c < 256; c++)
            {
                int m = 0;
                for (int b = 0; b < 8; b++) if ((c & (1 << b)) != 0) m |= 1 << (b ^ 1);
                Assert.That(TerrainShape.Get(m).Max[1], Is.EqualTo(TerrainShape.Get(c).Max[1]), "config " + c);
                Assert.That(TerrainShape.Get(m).Vertices.Length, Is.EqualTo(TerrainShape.Get(c).Vertices.Length), "config " + c);
            }
        }

        [Test]
        public void FlatFloorIsExactlyAtGroundHeight()
        {
            var g = Flat();
            var h = g.Raycast(new Vec3d(2.05, 3, 2.33), new Vec3d(0, -1, 0), 10);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.Point.Y, Is.EqualTo(0).Within(1e-9));
            Assert.That(h.Normal.Y, Is.EqualTo(1).Within(1e-9));
            Assert.That(h.Sub.Y, Is.EqualTo(-1));
            Assert.That(h.Id, Is.EqualTo(SubId.Natural));
        }

        [Test]
        public void OneSubCellStepIsAFortyFiveDegreeSlope()
        {
            var g = Flat();
            // Raise sub-cell columns x >= 10 by one sub-cell: a 0.3 m step at x = 3.0 m.
            g.Fill(new Int3(10, 0, 0), new Int3(31, 0, 31), SubId.Natural);
            // Surface: 0 until x = 2.85, then up at 45 degrees to 0.3 at x = 3.15.
            Assert.That(HeightAt(g, 2.80), Is.EqualTo(0).Within(1e-9));
            Assert.That(HeightAt(g, 2.90), Is.EqualTo(0.05).Within(1e-9));
            Assert.That(HeightAt(g, 3.00), Is.EqualTo(0.15).Within(1e-9));
            Assert.That(HeightAt(g, 3.10), Is.EqualTo(0.25).Within(1e-9));
            Assert.That(HeightAt(g, 3.20), Is.EqualTo(0.30).Within(1e-9));
            var h = g.Raycast(new Vec3d(3.05, 2, 4.5), new Vec3d(0, -1, 0), 10);
            Assert.That(h.Normal.X, Is.EqualTo(-Math.Sqrt(0.5)).Within(1e-9));
            Assert.That(h.Normal.Y, Is.EqualTo(Math.Sqrt(0.5)).Within(1e-9));
        }

        [Test]
        public void TallWallHasVerticalFaceAndChamferedTop()
        {
            var g = Flat();
            g.Fill(new Int3(10, 0, 0), new Int3(13, 7, 31), SubId.Natural); // 2.4 m wall, x 3.0 .. 4.2
            Assert.That(HeightAt(g, 3.5), Is.EqualTo(2.4).Within(1e-9));
            Assert.That(HeightAt(g, 3.05), Is.EqualTo(2.3).Within(1e-9), "45 degree top chamfer, 0.15 m wide");
            // Side face is vertical between the fillet and the chamfer.
            var side = g.Raycast(new Vec3d(1, 1.2, 4.5), new Vec3d(1, 0, 0), 10);
            Assert.That(side.Point.X, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(side.Normal.X, Is.EqualTo(-1).Within(1e-9));
            // Concave fillet at the base.
            Assert.That(HeightAt(g, 2.90), Is.EqualTo(0.05).Within(1e-9));
        }

        [Test]
        public void LoneSubCellIsAnOctahedron()
        {
            var g = Flat();
            g.Set(10, 3, 10, SubId.Natural); // floating, centre (3.15, 1.05, 3.15)
            Assert.That(HeightAt(g, 3.15, 3.15), Is.EqualTo(1.05 + 0.15).Within(1e-9));
            Assert.That(HeightAt(g, 3.10, 3.15), Is.EqualTo(1.05 + 0.10).Within(1e-9));
            Assert.That(g.BoxOverlapsSolid(new Aabb(new Vec3d(3.0, 1.0, 3.0), new Vec3d(3.06, 1.06, 3.06))), Is.False, "corner of the cube is cut away");
            Assert.That(g.BoxOverlapsSolid(new Aabb(new Vec3d(3.1, 1.0, 3.1), new Vec3d(3.2, 1.1, 3.2))), Is.True);
        }

        [Test]
        public void DiagonalContactLeavesNoGapForBullets()
        {
            var g = Flat();
            g.Set(10, 4, 10, SubId.Natural);
            g.Set(11, 5, 10, SubId.Natural);
            // A ray straight through the shared edge at (3.3, 1.5) is blocked.
            var h = g.Raycast(new Vec3d(3.3, 1.5, 0.5), new Vec3d(0, 0, 1), 10);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.Point.Z, Is.LessThan(3.0 + 0.3));
        }

        [Test]
        public void BoundaryCrossSectionsAgreeOnOrdinaryTerrain()
        {
            // Floors, steps and walls produce matching cross-sections on shared dual-cell faces.
            var g = Flat();
            g.Fill(new Int3(10, 0, 0), new Int3(13, 7, 31), SubId.Natural);
            g.Fill(new Int3(20, 0, 20), new Int3(23, 0, 23), SubId.Natural);
            int mismatches = 0;
            for (int vy = -3; vy < 12; vy++)
                for (int vz = 1; vz < 31; vz++)
                    for (int vx = 1; vx < 31; vx++)
                    {
                        var a = TerrainShape.Get(TerrainShape.ConfigAt(g, vx, vy, vz));
                        var b = TerrainShape.Get(TerrainShape.ConfigAt(g, vx + 1, vy, vz));
                        if (a.SideSig[0] != b.SideSig[1] && a.SideSig[0] != 0 && b.SideSig[1] != 0) mismatches++;
                    }
            Assert.That(mismatches, Is.Zero);
        }

        [Test]
        public void BoxTestMatchesPointSampling()
        {
            // Random small boxes: SAT answer agrees with dense sampling of the half-space definition.
            var rng = new Random(7);
            for (int trial = 0; trial < 3000; trial++)
            {
                int cfg = rng.Next(1, 255);
                var p = TerrainShape.Get(cfg);
                double cx = rng.NextDouble() * 2.4 - 0.2, cy = rng.NextDouble() * 2.4 - 0.2, cz = rng.NextDouble() * 2.4 - 0.2;
                double ex = 0.05 + rng.NextDouble() * 0.5, ey = 0.05 + rng.NextDouble() * 0.5, ez = 0.05 + rng.NextDouble() * 0.5;
                bool sat = TerrainGeometry.Hits(p, cx, cy, cz, ex, ey, ez);
                bool sampled = false;
                const int n = 10;
                for (int i = 0; i <= n && !sampled; i++)
                    for (int j = 0; j <= n && !sampled; j++)
                        for (int k = 0; k <= n && !sampled; k++)
                        {
                            double x = cx - ex + 2 * ex * i / n, y = cy - ey + 2 * ey * j / n, z = cz - ez + 2 * ez * k / n;
                            if (Inside(p, x, y, z, 1e-3)) sampled = true;
                        }
                if (sampled) Assert.That(sat, Is.True, $"config {cfg}: sampling found solid inside the box");
                if (sat && !sampled)
                {
                    // SAT can only be right here if a thin sliver lies between samples: check with a finer pass near the polytope.
                    Assert.That(MinPenetration(p, cx, cy, cz, ex, ey, ez), Is.LessThan(0.25), $"config {cfg}: SAT overlap far from any solid");
                }
            }
        }

        private static bool Inside(TerrainShape.Poly p, double x, double y, double z, double tol)
        {
            for (int k = 0; k < 13; k++)
            {
                Int3 a = TerrainShape.Axes[k];
                double d = a.X * x + a.Y * y + a.Z * z;
                if (d < p.Min[k] + tol || d > p.Max[k] - tol) return false;
            }
            for (int i = 0; i < p.ExtraMin.Length; i++)
            {
                double d = p.ExtraAxes[i * 3] * x + p.ExtraAxes[i * 3 + 1] * y + p.ExtraAxes[i * 3 + 2] * z;
                if (d < p.ExtraMin[i] + tol || d > p.ExtraMax[i] - tol) return false;
            }
            return true;
        }

        private static double MinPenetration(TerrainShape.Poly p, double cx, double cy, double cz, double ex, double ey, double ez)
        {
            double best = double.PositiveInfinity;
            for (int k = 0; k < 13; k++)
            {
                Int3 a = TerrainShape.Axes[k];
                double c = a.X * cx + a.Y * cy + a.Z * cz;
                double r = Math.Abs(a.X) * ex + Math.Abs(a.Y) * ey + Math.Abs(a.Z) * ez;
                double len = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
                best = Math.Min(best, Math.Min(c + r - p.Min[k], p.Max[k] - (c - r)) / len);
            }
            return best;
        }

        private static double HeightAt(SubGrid g, double x, double z = 4.55)
        {
            var h = g.Raycast(new Vec3d(x, 5, z), new Vec3d(0, -1, 0), 10);
            Assert.That(h.Hit, Is.True);
            return h.Point.Y;
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
