using System;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>Far ground (map v3 step 4): a coarser field that never rises above the true surface.</summary>
    public class HeightFieldLodTests
    {
        private static HeightField Random(int seed, int n, double amp, double cliffs)
        {
            var r = new Random(seed);
            var f = new HeightField(-n * 0.75 + 3.1, -n * 0.75 - 7.7, 1.5, n, n);
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    double h = amp * (Math.Sin(i * 0.31 + seed) * Math.Cos(j * 0.23) + 0.3 * r.NextDouble());
                    if (r.NextDouble() < cliffs) h += 6 * r.NextDouble(); // spikes and rock faces
                    f.Set(i, j, h);
                }
            f.Finish();
            return f;
        }

        [Test]
        public void TheCellFormula_IsTheFieldsOwnSurface()
        {
            HeightField f = Random(1, 8, 3, 0.2);
            var r = new Random(5);
            for (int k = 0; k < 5000; k++)
            {
                int i = r.Next(f.NX), j = r.Next(f.NZ);
                double u = r.NextDouble(), v = r.NextDouble();
                double h00 = f[i, j], h10 = f[i + 1, j], h11 = f[i + 1, j + 1], h01 = f[i, j + 1];
                double hc = (h00 + h10 + h11 + h01) * 0.25;
                double want = f.HeightAt(f.VertexX(i) + u * f.Cell, f.VertexZ(j) + v * f.Cell);
                Assert.That(HeightFieldLod.At(h00, h10, h11, h01, hc, u, v), Is.EqualTo(want).Within(1e-9));
            }
        }

        [TestCase(2, 0.0), TestCase(4, 0.0), TestCase(8, 0.0), TestCase(2, 0.1), TestCase(4, 0.1), TestCase(8, 0.25)]
        public void TheCoarseGround_NeverRisesAboveTheTrueSurface(int step, double cliffs)
        {
            for (int seed = 0; seed < 6; seed++)
            {
                HeightField f = Random(seed, 32, 4, cliffs);
                HeightField c = HeightFieldLod.Coarse(f, step);
                Assert.That(c.NX * step, Is.EqualTo(f.NX));
                // Every fine vertex and every fine cell centre (where the gap between the two can be largest)...
                for (int j = 0; j < f.NZ; j++)
                    for (int i = 0; i < f.NX; i++)
                    {
                        double x = f.VertexX(i), z = f.VertexZ(j);
                        Assert.That(c.HeightAt(x, z), Is.LessThanOrEqualTo(f.HeightAt(x, z) + 1e-9), $"vertex {i},{j}");
                        x += f.Cell / 2; z += f.Cell / 2;
                        Assert.That(c.HeightAt(x, z), Is.LessThanOrEqualTo(f.HeightAt(x, z) + 1e-9), $"centre {i},{j}");
                    }
                // ...and anywhere at all.
                var r = new Random(seed + 100);
                for (int k = 0; k < 20000; k++)
                {
                    double x = f.OriginX + r.NextDouble() * f.SizeX, z = f.OriginZ + r.NextDouble() * f.SizeZ;
                    Assert.That(c.HeightAt(x, z), Is.LessThanOrEqualTo(f.HeightAt(x, z) + 1e-9));
                }
            }
        }

        [Test]
        public void APlane_IsKeptExactly_AndGentleHillsAreLoweredOnlyALittle()
        {
            HeightField plane = HeightField.FromFunction(0, 0, 1.5, 32, 32, (x, z) => 0.2 * x - 0.1 * z + 3);
            HeightField c = HeightFieldLod.Coarse(plane, 8);
            for (int j = 0; j <= c.NZ; j++)
                for (int i = 0; i <= c.NX; i++) Assert.That(c[i, j], Is.EqualTo(plane[i * 8, j * 8]).Within(1e-5));
            // A 20 m hill 90 m across, seen as 6 m cells: no vertex more than half a metre down.
            HeightField hill = HeightField.FromFunction(0, 0, 1.5, 64, 64, (x, z) => GroundShapes.Hill(x, z, 48, 48, 45, 20));
            HeightField far = HeightFieldLod.Coarse(hill, 4);
            double most = 0;
            for (int j = 0; j <= far.NZ; j++)
                for (int i = 0; i <= far.NX; i++) most = Math.Max(most, hill[i * 4, j * 4] - far[i, j]);
            Assert.That(most, Is.LessThan(0.5));
        }

        [Test]
        public void StepsThatDontDivideTheField_AreRefused()
        {
            var f = new HeightField(0, 0, 1.5, 30, 30);
            Assert.Throws<ArgumentException>(() => HeightFieldLod.Coarse(f, 4));
            Assert.DoesNotThrow(() => HeightFieldLod.Coarse(f, 2));
        }
    }
}
