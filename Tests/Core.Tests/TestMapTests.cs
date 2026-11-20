using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class TestMapTests
    {
        private const double B = SubGrid.TileSize;

        [Test]
        public void M2MapSpawnIsClearAndOnTheGround()
        {
            SubGrid g = TestMaps.BuildM2(out Vec3d spawn, out _);
            var c = new MovementConfig();
            Assert.That(g.BoxOverlapsSolid(PlayerMovement.Box(spawn, c.HalfWidth, c.StandHeight)), Is.False);
            Assert.That(PlayerMovement.ProbeGround(g, c, spawn, c.StandHeight, out _), Is.True);
            Assert.That(g.Get(SubGrid.SubAt(spawn) + Int3.Down), Is.EqualTo(SubId.Anchor), "spawn pad is unsculptable");
        }

        [Test]
        public void M2MapHasEveryWallHeight()
        {
            SubGrid g = TestMaps.BuildM2(out _, out _);
            Assert.That(TopAt(g, 6.5 * B, 14.5 * B), Is.EqualTo(1 * B).Within(1e-9));
            Assert.That(TopAt(g, 6.5 * B, 20.5 * B), Is.EqualTo(2 * B).Within(1e-9));
            Assert.That(TopAt(g, 6.5 * B, 26.5 * B), Is.EqualTo(3 * B).Within(1e-9));
            Assert.That(TopAt(g, 9.5 * B, 26.5 * B), Is.EqualTo(0).Within(1e-9), "gap in the 3-level wall");
        }

        private static double TopAt(SubGrid g, double x, double z) => g.Raycast(new Vec3d(x, 20, z), new Vec3d(0, -1, 0), 40).Point.Y;
    }
}
