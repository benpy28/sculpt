using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The battle royale map's smooth ground: seams without steps or creases, level under every building, two
    /// slope bands, the same ground in front of every pad.</summary>
    public class BrGroundTests
    {
        private static HeightField F => BrMap.Field;

        [Test]
        public void EverySeam_MeetsWithoutAStepOrACrease()
        {
            double s = BrLayout.SegSize, e = 0.01;
            int lines = 0;
            for (int k = 1; k < BrLayout.Across; k++)
                for (double t = BrLayout.Play.X0; t <= BrLayout.Play.X1; t += 1.7)
                {
                    double b = k * s;
                    // A border along z (x = b) and one along x (z = b).
                    foreach (bool alongZ in new[] { true, false })
                    {
                        double H(double d) => alongZ ? BrMap.Natural(b + d, t) : BrMap.Natural(t, b + d);
                        double h0 = H(0), hl = H(-e), hr = H(e);
                        Assert.That(Math.Abs(hr - hl), Is.LessThan(0.005), $"a step at the seam {(alongZ ? "x" : "z")} = {b:0.0}, {t:0.0}");
                        double gl = (h0 - H(-0.5)) / 0.5, gr = (H(0.5) - h0) / 0.5;
                        Assert.That(Math.Abs(gr - gl), Is.LessThan(0.05), $"a crease at the seam {(alongZ ? "x" : "z")} = {b:0.0}, {t:0.0}");
                        Assert.That(h0, Is.EqualTo(BrGround.Shared(alongZ ? b : t, alongZ ? t : b)).Within(1e-6), "the seam is the shared ground");
                        lines++;
                    }
                }
            Assert.That(lines, Is.GreaterThan(1000));
        }

        [Test]
        public void TheGround_StaysInItsRange_AndNoFootingNearAnInnerBorder()
        {
            HeightField f = F;
            Assert.That(f.MinHeight, Is.GreaterThanOrEqualTo(BrGround.Lowest - 1e-6));
            Assert.That(f.MaxHeight, Is.LessThanOrEqualTo(BrGround.Highest + 1e-6));
            Footings foot = BrMap.FootingsUsed;
            for (int k = 0; k < foot.Count; k++)
            {
                Footings.Footing a = foot[k];
                var r = new Rect2(a.X0 - a.Apron, a.Z0 - a.Apron, a.X1 + a.Apron, a.Z1 + a.Apron);
                int id = BrLayout.Owner(r.CX, r.CZ);
                Rect2 area = BrLayout.Buildable(id);
                Assert.That(r.X0 >= area.X0 - 1e-6 && r.Z0 >= area.Z0 - 1e-6 && r.X1 <= area.X1 + 1e-6 && r.Z1 <= area.Z1 + 1e-6, Is.True,
                    $"footing {k} {r} reaches out of {BrLayout.Names[id]}'s buildable area {area}");
            }
        }

        [Test]
        public void EveryFooting_StaysExactlyLevel_UnderItsBuildingAndApron()
        {
            HeightField f = F;
            Footings foot = BrMap.FootingsUsed;
            int checkedPoints = 0;
            for (int k = 0; k < foot.Count; k++)
            {
                Footings.Footing a = foot[k];
                // Inset a little from the apron's edge: the ground cells straddling it lean into the bank.
                double g = a.Apron - f.Cell * 1.5;
                double step = Math.Max(1.1, Math.Min(a.X1 - a.X0, a.Z1 - a.Z0) / 12);
                for (double z = a.Z0 - g; z <= a.Z1 + g; z += step)
                    for (double x = a.X0 - g; x <= a.X1 + g; x += step)
                    {
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
                    double x = f.VertexX(i), z = f.VertexZ(j);
                    if (bad.Count < 12) bad.Add($"{BrLayout.Names[BrLayout.Owner(x, z)]} ({x:0},{z:0}) {Math.Acos(m) * 180 / Math.PI:0}deg");
                }
            Assert.That(faces, Is.GreaterThan(1000), "the rim has rock faces");
            Assert.That(between, Is.EqualTo(0), string.Join(", ", bad));
        }

        [Test]
        public void EveryPad_HasTheSameGroundBetweenItAndItsAnchor()
        {
            BrMap.BuildBrushes(out _, out BrMapInfo br);
            double[] first = null;
            foreach (BrRegion r in br.Regions)
            {
                Assert.That(r.Anchor.Y, Is.EqualTo(BrGround.Base));
                foreach (Vec3d pad in r.Pads)
                {
                    Assert.That(pad.Y, Is.EqualTo(BrGround.Base), "every pad stands at the base level");
                    // The ground as built (footings and all) along the straight line: the same for every pad of every region.
                    var prof = new double[81];
                    for (int k = 0; k <= 80; k++)
                    {
                        double t = k / 80.0;
                        prof[k] = F.HeightAt(pad.X + (r.Anchor.X - pad.X) * t, pad.Z + (r.Anchor.Z - pad.Z) * t);
                    }
                    if (first == null) { first = prof; continue; }
                    for (int k = 0; k <= 80; k++) Assert.That(prof[k], Is.EqualTo(first[k]).Within(0.03), $"{r.Name}, step {k} of the walk to its home POI");
                }
            }
        }

        [Test]
        public void TheRimIsThere()
        {
            double c = BrMap.Size / 2;
            foreach ((double dx, double dz) in new[] { (630.0, 100.0), (-630.0, -40.0), (60.0, 630.0), (-200.0, -630.0) })
                Assert.That(F.HeightAt(c + dx, c + dz), Is.EqualTo(BrGround.CliffTop).Within(0.01), "the rim");
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
