using System;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>The drawn surface (faces the mesher emits) is closed and matches the collision shape.</summary>
    public class SurfaceMeshTests
    {
        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Sum of area-weighted outward normals and total area over all visible faces in the grid.</summary>
        private static Vec3d VectorArea(SubGrid g, out double area, out double volume)
        {
            Vec3d sum = Vec3d.Zero;
            area = 0;
            volume = 0;
            for (int vy = g.MinY + 1; vy < g.MaxYExclusive; vy++)
                for (int vz = 1; vz < g.SizeZ; vz++)
                    for (int vx = 1; vx < g.SizeX; vx++)
                    {
                        int cfg = TerrainShape.ConfigAt(g, vx, vy, vz);
                        if (cfg == 0) continue;
                        var p = TerrainShape.Get(cfg);
                        Vec3d m = TerrainShape.CellMin(vx, vy, vz);
                        foreach (var f in p.Faces)
                        {
                            if (!TerrainShape.IsFaceVisible(g, vx, vy, vz, p, f)) continue;
                            Vec3d a = Vec3d.Zero;
                            Vec3d v0 = m + f.Verts[0] * TerrainShape.H;
                            for (int i = 1; i + 1 < f.Verts.Length; i++)
                            {
                                Vec3d v1 = m + f.Verts[i] * TerrainShape.H, v2 = m + f.Verts[i + 1] * TerrainShape.H;
                                Vec3d c = Cross(v1 - v0, v2 - v0) * 0.5;
                                a += c;
                                volume += v0.Dot(Cross(v1, v2)) / 6.0; // divergence theorem
                            }
                            sum += a;
                            area += a.Length;
                        }
                    }
            return sum;
        }

        [Test]
        public void FloatingRockSurfaceIsClosed()
        {
            // An empty world (air everywhere in bounds) with irregular floating clumps; bounds read as bedrock below
            // and at the sides, so keep the clumps away from them and only count cells inside.
            var g = new SubGrid(2, 2, 2, 0);
            var rng = new Random(5);
            for (int i = 0; i < 400; i++)
            {
                int x = rng.Next(6, 26), y = rng.Next(6, 26), z = rng.Next(6, 26);
                g.Set(x, y, z, SubId.Natural);
                if (rng.Next(3) == 0) g.Set(x + 1, y, z, SubId.Natural);
            }
            // Fill the outer shell with nothing: clumps never touch bounds, so every visible face belongs to them.
            Vec3d s = VectorArea(g, out double area, out double volume);
            Assert.That(area, Is.GreaterThan(1.0));
            Assert.That(s.Length, Is.LessThan(1e-9 * Math.Max(1, area)) .Or.LessThan(1e-6), "a closed surface's area-weighted normals cancel");
            Assert.That(volume, Is.GreaterThan(0));
        }

        [Test]
        public void SurfaceVolumeMatchesTheSolidTheBoxTestsSee()
        {
            var g = new SubGrid(2, 2, 2, 0);
            g.Fill(new Int3(8, 8, 8), new Int3(13, 10, 12), SubId.Natural); // a 6 x 3 x 5 block: chamfered
            g.Set(14, 8, 8, SubId.Natural);
            VectorArea(g, out _, out double volume);
            // Monte Carlo volume of the solid using point-sized box tests.
            var rng = new Random(9);
            int inside = 0, samples = 40000;
            double x0 = 8 * 0.3 - 0.2, x1 = 15 * 0.3 + 0.2, y0 = 8 * 0.3 - 0.2, y1 = 11 * 0.3 + 0.2, z0 = 8 * 0.3 - 0.2, z1 = 13 * 0.3 + 0.2;
            for (int i = 0; i < samples; i++)
            {
                var p = new Vec3d(x0 + rng.NextDouble() * (x1 - x0), y0 + rng.NextDouble() * (y1 - y0), z0 + rng.NextDouble() * (z1 - z0));
                if (g.BoxOverlapsSolid(new Aabb(p - new Vec3d(1e-5, 1e-5, 1e-5), p + new Vec3d(1e-5, 1e-5, 1e-5)))) inside++;
            }
            double mc = inside / (double)samples * (x1 - x0) * (y1 - y0) * (z1 - z0);
            Assert.That(volume, Is.EqualTo(mc).Within(mc * 0.03), "drawn volume equals collided volume");
            Assert.That(volume, Is.LessThan(6 * 3 * 5 * 0.027 + 0.027), "chamfers cut the corners");
        }
    }
}
