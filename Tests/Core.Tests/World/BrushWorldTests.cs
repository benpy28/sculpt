using System;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class BrushWorldTests
    {
        private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
            new Aabb(new Vec3d(x0, y0, z0), new Vec3d(x1, y1, z1));

        [Test]
        public void BoxesOverlapOnlyInside_TouchingIsNotOverlapping()
        {
            var w = new BrushWorld();
            w.AddBox(new Vec3d(0, 0, 0), new Vec3d(2, 2, 2), BrushKind.Wall);
            Assert.That(w.BoxOverlapsSolid(Box(1, 1, 1, 3, 3, 3)), Is.True);
            Assert.That(w.BoxOverlapsSolid(Box(2, 0, 0, 3, 2, 2)), Is.False, "face contact");
            Assert.That(w.BoxOverlapsSolid(Box(0, 2, 0, 2, 3, 2)), Is.False, "standing on top");
            Assert.That(w.BoxOverlapsSolid(Box(2.001, 0, 0, 3, 2, 2)), Is.False);
        }

        [Test]
        public void RampIsSolidOnlyUnderItsSlope()
        {
            var w = new BrushWorld();
            w.AddRamp(new Vec3d(0, 0, 0), new Vec3d(4, 2, 4), Dir.East, BrushKind.Ramp); // 0 m at x=0, 2 m at x=4
            Assert.That(w.BoxOverlapsSolid(Box(0.1, 0.2, 1, 0.3, 1, 2)), Is.False, "above the low end");
            Assert.That(w.BoxOverlapsSolid(Box(3.5, 1.0, 1, 3.7, 1.5, 2)), Is.True, "under the high end");
            // A box whose bottom sits exactly on the slope at its high side touches but does not overlap.
            Assert.That(w.BoxOverlapsSolid(Box(1.6, 1.0, 1, 2.0, 2.0, 2)), Is.False);
            Assert.That(w.BoxOverlapsSolid(Box(1.6, 0.99, 1, 2.0, 2.0, 2)), Is.True);
        }

        [Test]
        public void GroundContactGivesSlopeNormalOnRampsAndUpOnBoxes()
        {
            var w = new BrushWorld();
            w.AddRamp(new Vec3d(0, 0, 0), new Vec3d(4, 2, 4), Dir.North, BrushKind.Stairs);
            w.AddBox(new Vec3d(10, 0, 0), new Vec3d(12, 1, 2), BrushKind.Floor);
            Assert.That(w.GroundContact(Box(1, 1.0, 1.6, 2, 3, 2.0), 0.02, out Vec3d n), Is.True);
            Assert.That(n.Z, Is.LessThan(-0.4), "slope normal leans toward the low end (south)");
            Assert.That(n.Y, Is.EqualTo(4 / Math.Sqrt(20)).Within(1e-9));
            Assert.That(w.GroundContact(Box(10.5, 1.0, 0.5, 11, 3, 1), 0.02, out n), Is.True);
            Assert.That(n.Y, Is.EqualTo(1));
            Assert.That(w.GroundContact(Box(10.5, 1.5, 0.5, 11, 3, 1), 0.02, out _), Is.False, "too high above");
        }

        [Test]
        public void RaysHitTheNearestFaceAndRampSlope()
        {
            var w = new BrushWorld();
            int wall = w.AddBox(new Vec3d(5, 0, -1), new Vec3d(6, 3, 1), BrushKind.Wall);
            w.AddBox(new Vec3d(8, 0, -1), new Vec3d(9, 3, 1), BrushKind.Wall);
            TerrainHit h = w.Raycast(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 50);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.Distance, Is.EqualTo(5).Within(1e-9));
            Assert.That(h.Normal.X, Is.EqualTo(-1));
            Assert.That(h.Brush, Is.EqualTo(wall + 1));

            var r = new BrushWorld();
            r.AddRamp(new Vec3d(0, 0, 0), new Vec3d(4, 4, 4), Dir.East, BrushKind.Ramp); // 45 degrees
            TerrainHit down = r.Raycast(new Vec3d(2, 10, 2), new Vec3d(0, -1, 0), 50);
            Assert.That(down.Hit, Is.True);
            Assert.That(down.Point.Y, Is.EqualTo(2).Within(1e-9));
            Assert.That(down.Normal.X, Is.LessThan(-0.7));
            Assert.That(r.Raycast(new Vec3d(-1, 0.5, 2), new Vec3d(1, 0, 0), 0.5).Hit, Is.False, "too short");
        }

        [Test]
        public void GoneBrushesNeitherCollideNorStopBullets()
        {
            var w = new BrushWorld();
            int id = w.AddBox(new Vec3d(5, 0, -1), new Vec3d(6, 3, 1), BrushKind.Wall);
            w.SetSolid(id, false);
            Assert.That(w.BoxOverlapsSolid(Box(5.2, 0, -0.5, 5.8, 2, 0.5)), Is.False);
            Assert.That(w.Raycast(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 50).Hit, Is.False);
        }

        [Test]
        public void LongRaysAcrossTheMapFindTheirTarget()
        {
            var w = new BrushWorld();
            w.AddBox(new Vec3d(140, 0, 140), new Vec3d(141, 3, 141), BrushKind.Wall);
            Vec3d o = new Vec3d(1, 1.5, 1);
            Vec3d d = new Vec3d(140.5, 1.5, 140.5) - o;
            TerrainHit h = w.Raycast(o, d, 300);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.Distance, Is.LessThan(d.Length));
        }

        [Test]
        public void AOneTileHoleFitsASlidingPlayerButNotAStandingOne()
        {
            // A 1.5 m wall with one 1.5 m tile missing at the bottom: exact, no chamfers shaving the hole.
            var w = new BrushWorld();
            w.AddBox(new Vec3d(0, 1.5, 0), new Vec3d(1.5, 4.5, 0.3), BrushKind.Wall);
            var c = new MovementConfig();
            Aabb sliding = PlayerMovement.Box(new Vec3d(0.75, 0, 0.15), c.HalfWidth, c.CrouchHeight);
            Aabb standing = PlayerMovement.Box(new Vec3d(0.75, 0, 0.15), c.HalfWidth, c.StandHeight);
            Assert.That(w.BoxOverlapsSolid(sliding), Is.False);
            Assert.That(w.BoxOverlapsSolid(standing), Is.True);
        }

        [Test]
        public void CompositeTakesTheNearerHitAndEitherSolid()
        {
            var a = new BrushWorld();
            var b = new BrushWorld();
            a.AddBox(new Vec3d(10, 0, -1), new Vec3d(11, 3, 1), BrushKind.Wall);
            b.AddBox(new Vec3d(5, 0, -1), new Vec3d(6, 3, 1), BrushKind.Wall);
            var t = new CompositeTerrain(a, b);
            Assert.That(t.Raycast(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 50).Distance, Is.EqualTo(5).Within(1e-9));
            Assert.That(t.BoxOverlapsSolid(Box(10.2, 0, -0.5, 10.8, 1, 0.5)), Is.True);
            Assert.That(t.BoxOverlapsSolid(Box(5.2, 0, -0.5, 5.8, 1, 0.5)), Is.True);
            Assert.That(t.BoxOverlapsSolid(Box(7, 0, -0.5, 8, 1, 0.5)), Is.False);
        }

        [Test]
        public void CourseSitsOnTheLatticeAndInsideTheMap()
        {
            var brushes = BreachMap.BuildBrushes(out BreachMapInfo info);
            foreach (Brush b in brushes.Brushes)
            {
                if (b.Kind == BrushKind.Marker) continue;
                double top = b.Bounds.Max.Y / SubGrid.SubSize;
                Assert.That(Math.Abs(top - Math.Round(top)), Is.LessThan(1e-9), $"top {b.Bounds.Max.Y} off the 0.3 m lattice");
                Assert.That(b.Bounds.Min.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(b.Bounds.Max.X, Is.LessThanOrEqualTo(info.SizeX));
                Assert.That(b.Bounds.Max.Z, Is.LessThanOrEqualTo(info.SizeZ));
            }
        }
    }
}
