using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class GeometryTests
    {
        private const double B = VoxelGrid.BlockSize;

        [Test]
        public void FlatSurfaceHeightIsCellTop()
        {
            Assert.That(VoxelGeometry.SurfaceHeight(Block.Solid, new Int3(0, 0, 0), 0.3, 0.3), Is.EqualTo(B).Within(1e-12));
            Assert.That(VoxelGeometry.SurfaceHeight(Block.Empty, new Int3(0, 1, 0), 0.3, 0.3), Is.EqualTo(B).Within(1e-12));
        }

        [TestCase(Dir.East, 0.0, 0.6, 1.2)]
        [TestCase(Dir.East, 1.2, 0.6, 0.0)]
        [TestCase(Dir.East, 0.6, 0.6, 0.6)]
        [TestCase(Dir.West, 0.0, 0.6, 0.0)]
        [TestCase(Dir.West, 0.9, 0.6, 0.9)]
        [TestCase(Dir.North, 0.6, 1.2, 0.0)]
        [TestCase(Dir.South, 0.6, 1.2, 1.2)]
        [TestCase(Dir.East, -5.0, 0.6, 1.2)] // clamped into the cell
        public void RampSurfaceHeight(Dir low, double x, double z, double expected) =>
            Assert.That(VoxelGeometry.SurfaceHeight(Block.Ramp(low), new Int3(0, 0, 0), x, z), Is.EqualTo(expected).Within(1e-9));

        [Test]
        public void TouchingBoxDoesNotOverlap()
        {
            var cell = new Int3(0, 0, 0);
            var onTop = new Aabb(new Vec3d(0.2, B, 0.2), new Vec3d(0.9, B + 1.8, 0.9));
            var beside = new Aabb(new Vec3d(B, 0, 0.2), new Vec3d(B + 0.7, 1.8, 0.9));
            var inside = new Aabb(new Vec3d(0.2, B - 0.01, 0.2), new Vec3d(0.9, B + 1.8, 0.9));
            Assert.That(VoxelGeometry.BoxOverlapsBlock(Block.Solid, cell, onTop), Is.False);
            Assert.That(VoxelGeometry.BoxOverlapsBlock(Block.Solid, cell, beside), Is.False);
            Assert.That(VoxelGeometry.BoxOverlapsBlock(Block.Solid, cell, inside), Is.True);
        }

        [Test]
        public void BoxAboveRampSlopeDoesNotOverlap()
        {
            var cell = new Int3(0, 0, 0);
            var ramp = Block.Ramp(Dir.East); // high at west (x=0), low at east (x=1.2)
            // Box over the east (low) half, feet at 0.65: surface there is at most 0.6.
            var box = new Aabb(new Vec3d(0.6, 0.65, 0.2), new Vec3d(1.2, 2.4, 0.9));
            Assert.That(VoxelGeometry.BoxOverlapsBlock(ramp, cell, box), Is.False);
            // Same feet height over the west half (surface up to 1.2) overlaps.
            var box2 = new Aabb(new Vec3d(0.0, 0.65, 0.2), new Vec3d(0.6, 2.4, 0.9));
            Assert.That(VoxelGeometry.BoxOverlapsBlock(ramp, cell, box2), Is.True);
        }

        [Test]
        public void BoxOverlapsSolidHonoursOverrides()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(1, 0, 1, Block.Solid);
            var box = VoxelGrid.CellBounds(new Int3(1, 0, 1)).Expand(-0.1);
            Assert.That(VoxelGeometry.BoxOverlapsSolid(g, box), Is.True);
            Assert.That(VoxelGeometry.BoxOverlapsSolid(g, box, new[] { new VoxelGeometry.Override(new Int3(1, 0, 1), Block.Empty) }), Is.False);
        }

        [Test]
        public void BoxOutsideMapSidesCountsAsSolid()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            var box = new Aabb(new Vec3d(-0.3, 5, 1), new Vec3d(0.3, 6, 2));
            Assert.That(VoxelGeometry.BoxOverlapsSolid(g, box), Is.True);
        }

        [Test]
        public void RaycastHitsSideFace()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(5, 2, 5, Block.Solid);
            var origin = new Vec3d(1.0, 2.5 * B, 5.5 * B);
            VoxelHit h = VoxelGeometry.Raycast(g, origin, new Vec3d(1, 0, 0), 20);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.Cell, Is.EqualTo(new Int3(5, 2, 5)));
            Assert.That(h.Face, Is.EqualTo(Dir.West));
            Assert.That(h.Point.X, Is.EqualTo(5 * B).Within(1e-9));
            Assert.That(h.Distance, Is.EqualTo(5 * B - 1.0).Within(1e-9));
        }

        [Test]
        public void RaycastHitsTopFaceFromAbove()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(3, 0, 3, Block.Solid);
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(3.5 * B, 5, 3.5 * B), new Vec3d(0, -1, 0), 20);
            Assert.That(h.Hit && h.Cell == new Int3(3, 0, 3) && h.Face == Dir.Up, Is.True);
            Assert.That(h.Point.Y, Is.EqualTo(B).Within(1e-9));
        }

        [Test]
        public void RaycastRespectsMaxDistance()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(10, 0, 0, Block.Solid);
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(0.6, 0.6, 0.6), new Vec3d(1, 0, 0), 5);
            Assert.That(h.Hit, Is.False);
        }

        [Test]
        public void RaycastPassesOverLowSideOfRamp()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(5, 0, 5, Block.Ramp(Dir.West)); // low at west, high at east
            // Horizontal ray at height 0.3 from the west: the slope at the west edge is height 0 so it hits the slope, not the side face.
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(1, 0.3, 5.5 * B), new Vec3d(1, 0, 0), 20);
            Assert.That(h.Hit, Is.True);
            Assert.That(h.HitRampSlope, Is.True);
            Assert.That(h.Face, Is.EqualTo(Dir.Up));
            Assert.That(h.Point.X, Is.EqualTo(5 * B + 0.3).Within(1e-9));
            // A ray from the high side hits the full-height east face.
            VoxelHit h2 = VoxelGeometry.Raycast(g, new Vec3d(10, 1.0, 5.5 * B), new Vec3d(-1, 0, 0), 20);
            Assert.That(h2.Hit && h2.Face == Dir.East && !h2.HitRampSlope, Is.True);
        }

        [Test]
        public void RaycastAboveRampSlopeMisses()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(5, 0, 5, Block.Ramp(Dir.East)); // high at west
            // Straight down over the east edge region at x = 5B + 1.0: surface height 0.2.
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(5 * B + 1.0, 5, 5.5 * B), new Vec3d(0, -1, 0), 20);
            Assert.That(h.Hit && h.Cell == new Int3(5, 0, 5) && h.HitRampSlope, Is.True);
            Assert.That(h.Point.Y, Is.EqualTo(0.2).Within(1e-9));
        }

        [Test]
        public void RaycastStartingInsideHitsAtZero()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(0, 0, 0, Block.Solid);
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(0.6, 0.6, 0.6), new Vec3d(1, 0, 0), 5);
            Assert.That(h.Hit && h.Distance == 0, Is.True);
        }

        [Test]
        public void RaycastDiagonalFindsFirstBlock()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(4, 0, 4, Block.Solid);
            g.Set(6, 0, 6, Block.Solid);
            VoxelHit h = VoxelGeometry.Raycast(g, new Vec3d(0.6, 0.6, 0.6), new Vec3d(1, 0, 1), 30);
            Assert.That(h.Cell, Is.EqualTo(new Int3(4, 0, 4)));
        }
    }
}
