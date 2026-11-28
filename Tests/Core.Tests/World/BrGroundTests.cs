using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The battle royale map's smooth ground (map v3 step 4): fair, level under every building, two slope bands.</summary>
    public class BrGroundTests
    {
        private static HeightField F => BrMap.Field;

        [Test]
        public void TheGround_IsTheSameInEveryEighth()
        {
            HeightField f = F;
            int n = f.NX;
            Assert.That(f.NZ, Is.EqualTo(n));
            for (int j = 0; j <= n; j += 3)
                for (int i = 0; i <= n; i += 2)
                {
                    double h = f[i, j];
                    Assert.That(f[n - i, j], Is.EqualTo(h));
                    Assert.That(f[i, n - j], Is.EqualTo(h));
                    Assert.That(f[j, i], Is.EqualTo(h));
                    Assert.That(f[n - j, n - i], Is.EqualTo(h));
                }
        }

        [Test]
        public void TheNaturalGround_HasNoCreaseWhereTheEighthsMeet_AwayFromTheRim()
        {
            // Mirror-even across the eighth's edges: the slope across an edge is the same on both sides.
            for (double u = 5; u < BrMap.RimStart; u += 7)
            {
                double across0 = BrGround.Eighth(u, 0.75) - BrGround.Eighth(u, 0);
                Assert.That(Math.Abs(across0), Is.LessThan(0.05), $"edge midline at {u}");
                double d = 0.75 / Math.Sqrt(2);
                double across1 = BrGround.At(u + d, u - d) - BrGround.At(u - d, u + d);
                Assert.That(Math.Abs(across1), Is.LessThan(1e-9), $"diagonal at {u}");
            }
        }

        [Test]
        public void EveryFooting_StaysExactlyLevel_UnderItsBuildingAndApron()
        {
            HeightField f = F;
            Footings foot = BrMap.FootingsUsed;
            double c = BrMap.Size / 2;
            int checkedPoints = 0;
            for (int k = 0; k < foot.Count; k++)
            {
                Footings.Footing a = foot[k];
                // Inset a little from the apron's edge: the ground cells straddling it lean into the bank.
                double g = a.Apron - f.Cell * 1.5;
                for (double z = a.Z0 - g; z <= a.Z1 + g; z += 1.1)
                    for (double x = a.X0 - g; x <= a.X1 + g; x += 1.1)
                    {
                        if (z - c > x - c || z < c) continue; // the eighth's own half (its twins are equal, tested)
                        Assert.That(f.HeightAt(x, z), Is.EqualTo(a.Level).Within(1e-4), $"footing {k} at {x:0.0},{z:0.0}");
                        checkedPoints++;
                    }
            }
            Assert.That(checkedPoints, Is.GreaterThan(10000));
        }

        [Test]
        public void EveryBuilding_StandsOnLevelGroundAtItsGroundFloor()
        {
            BrMap.BuildBrushes(out BreachMapInfo info, out _);
            HeightField f = F;
            foreach (Aabb b in info.Block.Buildings)
                for (double z = b.Min.Z; z <= b.Max.Z; z += 1.3)
                    for (double x = b.Min.X; x <= b.Max.X; x += 1.3)
                        Assert.That(f.HeightAt(x, z), Is.EqualTo(b.Min.Y).Within(1e-4), $"building at {b.Min}");
        }

        [Test]
        public void SlopesKeepToTwoBands_WalkableHillsAndRockFaces()
        {
            HeightField f = F;
            double hill = Math.Cos(35 * Math.PI / 180), face = Math.Cos(55 * Math.PI / 180);
            int n = f.NX, between = 0, faces = 0;
            bool Steep(int i, int j)
            {
                if (i < 0 || j < 0 || i >= n || j >= n) return false;
                return f.CellMinNormalY(i, j) < face;
            }
            var bad = new List<string>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double m = f.CellMinNormalY(i, j);
                    if (m < face) { faces++; continue; }
                    if (m >= hill) continue;
                    // Half way: only at a rock face's lip or foot, or a short kink (no more than a metre over what a
                    // 35 degree slope rises across the cell: where a ramp's side face tapers out at its foot).
                    double lo = Math.Min(Math.Min(f[i, j], f[i + 1, j]), Math.Min(f[i, j + 1], f[i + 1, j + 1]));
                    double hi = Math.Max(Math.Max(f[i, j], f[i + 1, j]), Math.Max(f[i, j + 1], f[i + 1, j + 1]));
                    if (hi - lo < 2.0) continue;
                    bool byFace = false;
                    for (int dj = -1; dj <= 1 && !byFace; dj++)
                        for (int di = -1; di <= 1 && !byFace; di++) byFace = Steep(i + di, j + dj);
                    if (byFace) continue;
                    between++;
                    if (bad.Count < 12) bad.Add($"({f.VertexX(i) - BrMap.Size / 2:0},{f.VertexZ(j) - BrMap.Size / 2:0}) {Math.Acos(m) * 180 / Math.PI:0}deg");
                }
            Assert.That(faces, Is.GreaterThan(1000), "the plateau, quarry and rim have rock faces");
            Assert.That(between, Is.EqualTo(0), string.Join(", ", bad));
        }

        [Test]
        public void EveryPad_HasTheSameGroundBetweenItAndItsPoi()
        {
            BrMap.BuildBrushes(out _, out BrMapInfo br);
            double y = br.Regions[0].Pads[0].Y;
            foreach (BrRegion r in br.Regions)
            {
                BrPoi poi = br.Pois[r.Poi];
                Assert.That(poi.Centre.Y, Is.EqualTo(y), "every POI stands as high as every pad");
                double[] first = null;
                foreach (Vec3d pad in r.Pads)
                {
                    Assert.That(pad.Y, Is.EqualTo(y));
                    // The natural ground along the straight line (the footings are each building's own).
                    var prof = new double[41];
                    for (int k = 0; k <= 40; k++)
                    {
                        double t = k / 40.0;
                        prof[k] = BrMap.Natural(pad.X + (poi.Centre.X - pad.X) * t, pad.Z + (poi.Centre.Z - pad.Z) * t);
                    }
                    if (first == null) { first = prof; continue; }
                    for (int k = 0; k <= 40; k++) Assert.That(prof[k], Is.EqualTo(first[k]).Within(0.02), $"region {r.Index}, step {k}");
                }
            }
        }

        [Test]
        public void TheRiverBed_TheQuarry_ThePlateauAndTheFortHill_AreThere()
        {
            double c = BrMap.Size / 2;
            HeightField f = F;
            Assert.That(f.HeightAt(c, c), Is.EqualTo(BrGround.PlateauTop).Within(0.01), "the plateau");
            Assert.That(f.HeightAt(c + 300, c), Is.LessThan(-1.5), "the river bed");
            Assert.That(f.HeightAt(c + BrGround.QuarryU, c), Is.EqualTo(BrGround.QuarryFloor).Within(0.01), "the quarry floor");
            Assert.That(BrGround.At(BrGround.FortAt, BrGround.FortAt), Is.GreaterThan(BrGround.FortHeight), "the fort hill");
            Assert.That(f.HeightAt(c + 630, c + 100), Is.EqualTo(BrGround.CliffTop).Within(0.01), "the rim");
        }
    }

    /// <summary>The circle on the 1,306 m map (map v3 step 4: 8 phases, closed at 15 minutes, never faster than 2.5 m/s).</summary>
    public class BrCircleTests
    {
        [Test]
        public void EightPhases_ClosedAtFifteenMinutes_NoShrinkFasterThanARelaxedRun()
        {
            var cfg = new Sculpt.Core.Net.BrConfig();
            Assert.That(cfg.Radius.Length, Is.EqualTo(8));
            Assert.That(cfg.WaitSeconds.Length, Is.EqualTo(8));
            Assert.That(cfg.ShrinkSeconds.Length, Is.EqualTo(8));
            Assert.That(cfg.Dps.Length, Is.EqualTo(8));
            double total = 0, from = cfg.StartRadius;
            for (int k = 0; k < 8; k++)
            {
                total += cfg.WaitSeconds[k] + cfg.ShrinkSeconds[k];
                double to = cfg.Radius[k];
                Assert.That(to, Is.LessThan(from));
                // The edge closes in at the radius change over the shrink time.
                double edge = (from - to) * 1.0 / cfg.ShrinkSeconds[k];
                Assert.That(edge, Is.LessThanOrEqualTo(2.5), $"phase {k} shrinks at {edge:0.00} m/s");
                from = to;
            }
            Assert.That(cfg.Radius[7], Is.EqualTo(0), "a match always ends");
            Assert.That(total, Is.EqualTo(15 * 60).Within(1), "the circle is closed 15 minutes in");
            Assert.That(cfg.StartRadius, Is.GreaterThanOrEqualTo(BrMap.RimStart * Math.Sqrt(2)), "the first circle covers the play area's corners");
        }
    }
}
