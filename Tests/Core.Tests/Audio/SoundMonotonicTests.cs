using System;
using NUnit.Framework;
using Sculpt.Core.Audio;

namespace Sculpt.Core.Tests.Audio
{
    /// <summary>
    /// More walls or more solid between you and a sound never make it louder or brighter (bug: a loud sound with too much
    /// solid on the line fell back to a fixed -12 dB, 1.8 kHz, so a shot behind 4 walls sounded better than behind 1 at
    /// -16 dB, 900 Hz). Checked as a property, on the pure rule and on real nested walls.
    /// </summary>
    public class SoundMonotonicTests
    {
        [Test]
        public void ThroughSound_NeverGetsLouderOrBrighter_WithMoreWallsOrSolid()
        {
            var c = new SoundConfig();
            foreach (double range in new[] { 12.0, 34, 60, 61, 190, 220 })
            {
                double prevWallDb = double.PositiveInfinity, prevWallHz = double.PositiveInfinity;
                for (int w = 0; w <= 12; w++)
                {
                    double prevDb = double.PositiveInfinity, prevHz = double.PositiveInfinity;
                    for (int si = 0; si <= 120; si++)
                    {
                        double s = si * 0.05;
                        Eval(w, s, range, c, out double db, out double hz);
                        Assert.That(db, Is.LessThanOrEqualTo(prevDb + 1e-12), $"range {range} walls {w}: more solid ({s:0.00} m) got louder");
                        Assert.That(hz, Is.LessThanOrEqualTo(prevHz + 1e-9), $"range {range} walls {w}: more solid ({s:0.00} m) got brighter");
                        prevDb = db; prevHz = hz;
                        // Same solid, one wall more: never louder or brighter.
                        if (w > 0)
                        {
                            Eval(w - 1, s, range, c, out double db0, out double hz0);
                            Assert.That(db, Is.LessThanOrEqualTo(db0 + 1e-12), $"range {range}: {w} walls louder than {w - 1} ({s:0.00} m)");
                            Assert.That(hz, Is.LessThanOrEqualTo(hz0 + 1e-9), $"range {range}: {w} walls brighter than {w - 1} ({s:0.00} m)");
                        }
                    }
                    // Plain 0.3 m walls, one more each time.
                    Eval(w, 0.3 * w, range, c, out double dbw, out double hzw);
                    Assert.That(dbw, Is.LessThanOrEqualTo(prevWallDb + 1e-12), $"range {range}: {w} plain walls louder than {w - 1}");
                    Assert.That(hzw, Is.LessThanOrEqualTo(prevWallHz + 1e-9), $"range {range}: {w} plain walls brighter than {w - 1}");
                    prevWallDb = dbw; prevWallHz = hzw;
                    if (range > c.SearchRange) Assert.That(dbw, Is.GreaterThan(double.NegativeInfinity), "a loud sound is always heard");
                }
            }
        }

        /// <summary>What a blocked sound (no way round) sounds like with this many walls and metres of solid on the line:
        /// the same steps as <see cref="SoundPaths"/> (possible under the limits, else the loud floor for a loud sound).
        /// Silent is minus infinity dB and 0 Hz.</summary>
        private static void Eval(int walls, double solid, double range, SoundConfig c, out double db, out double hz)
        {
            var r = new SoundRoute { DirectDistance = 8, Walls = walls, SolidMetres = solid };
            r.ThroughPossible = walls <= c.MaxWalls && solid <= c.MaxSolidMetres;
            r.ThroughDb = -(c.WallLossDb * walls + c.SolidLossDbPerMetre * solid);
            if (!r.ThroughPossible && range > c.SearchRange) { r.ThroughPossible = true; r.Obstructed = true; r.ThroughDb = -c.LoudFloorLossDb; }
            SoundRoute m = SoundPaths.Mix(r, Vec3d.Zero, new Vec3d(0, 0, 1), range, c);
            db = m.Audible ? m.GainDb : double.NegativeInfinity;
            hz = m.Audible ? m.CutoffHz : 0;
        }

        /// <summary>
        /// Real geometry: sealed nested boxes (walls and roof, on solid ground) round a source, so there is no way round
        /// and the line out crosses one wall per box. Shells 0..k-1 are built for k walls; the ears stay put. For quiet and
        /// loud sounds, thin and thick walls, at several distances: more walls is never louder or brighter, and a loud
        /// sound stays audible however many walls there are.
        /// </summary>
        [Test]
        public void NestedSealedShells_MoreWalls_NeverLouderOrBrighter()
        {
            var centre = new Vec3d(19, 1.6, 19);
            var ears = new[] { new Vec3d(31.2, 1.6, 19.4), new Vec3d(35.5, 1.6, 20.1), new Vec3d(33, 1.6, 26) };
            foreach (double thick in new[] { 0.3, 0.6, 1.0 })
            {
                foreach (double range in new[] { 34.0, 220 })
                {
                    var prevDb = new double[ears.Length];
                    var prevHz = new double[ears.Length];
                    for (int e = 0; e < ears.Length; e++) { prevDb[e] = double.PositiveInfinity; prevHz[e] = double.PositiveInfinity; }
                    for (int k = 0; k <= 6; k++)
                    {
                        var paths = new SoundPaths(Shells(k, thick, centre));
                        for (int e = 0; e < ears.Length; e++)
                        {
                            SoundRoute r = paths.Route(ears[e], centre, range);
                            double db = r.Audible ? r.GainDb : double.NegativeInfinity, hz = r.Audible ? r.CutoffHz : 0;
                            string what = $"{k} walls of {thick} m, range {range}, ears {e}: {r.Way} {db:0.0} dB {hz:0} Hz";
                            if (k > 0) Assert.That(r.Way, Is.Not.EqualTo(SoundWay.Direct).And.Not.EqualTo(SoundWay.Around), what);
                            Assert.That(db, Is.LessThanOrEqualTo(prevDb[e] + 1e-9), what + ": louder than with fewer walls");
                            Assert.That(hz, Is.LessThanOrEqualTo(prevHz[e] + 1e-9), what + ": brighter than with fewer walls");
                            if (range > paths.Config.SearchRange)
                                Assert.That(r.Audible, Is.True, what + ": a loud sound is always heard");
                            prevDb[e] = db; prevHz[e] = hz;
                        }
                    }
                }
            }
        }

        private static CompositeTerrain Shells(int count, double thick, Vec3d c)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            for (int i = 0; i < count; i++)
            {
                double a = 1.5 + i * 1.6, h = 3 + i * 1.6;
                double x0 = c.X - a - thick, x1 = c.X + a + thick, z0 = c.Z - a - thick, z1 = c.Z + a + thick;
                w.AddBox(new Vec3d(x0, 0, z0), new Vec3d(x1, h, c.Z - a), BrushKind.Frame);
                w.AddBox(new Vec3d(x0, 0, c.Z + a), new Vec3d(x1, h, z1), BrushKind.Frame);
                w.AddBox(new Vec3d(x0, 0, c.Z - a), new Vec3d(c.X - a, h, c.Z + a), BrushKind.Frame);
                w.AddBox(new Vec3d(c.X + a, 0, c.Z - a), new Vec3d(x1, h, c.Z + a), BrushKind.Frame);
                w.AddBox(new Vec3d(x0, h, z0), new Vec3d(x1, h + thick, z1), BrushKind.Frame);
            }
            return new CompositeTerrain(g, w);
        }
    }
}
