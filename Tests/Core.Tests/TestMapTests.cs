using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class TestMapTests
    {
        [Test]
        public void M2MapSpawnIsClearAndOnTheGround()
        {
            VoxelGrid g = TestMaps.BuildM2(out Vec3d spawn, out _);
            var c = new MovementConfig();
            Assert.That(VoxelGeometry.BoxOverlapsSolid(g, PlayerMovement.Box(spawn, c.HalfWidth, c.StandHeight)), Is.False);
            Assert.That(PlayerMovement.ProbeGround(g, c, spawn, c.StandHeight, out _), Is.True);
            Assert.That(g.Get(VoxelGrid.CellAt(spawn) + Int3.Down).Kind, Is.EqualTo(BlockKind.Anchor), "spawn pad is unsculptable");
        }

        [Test]
        public void M2MapHasEveryWallHeight()
        {
            VoxelGrid g = TestMaps.BuildM2(out _, out _);
            Assert.That(g.TopOfStack(new Int3(6, 0, 14)).Y, Is.EqualTo(0));
            Assert.That(g.TopOfStack(new Int3(6, 0, 20)).Y, Is.EqualTo(1));
            Assert.That(g.TopOfStack(new Int3(6, 0, 26)).Y, Is.EqualTo(2));
            Assert.That(g.Get(9, 0, 26).IsEmpty, Is.True, "gap in the 3-level wall");
        }

        [Test]
        public void M2MapRampsOnlySitOnColumnTops()
        {
            VoxelGrid g = TestMaps.BuildM2(out _, out _);
            for (int y = g.MinY; y < g.MaxYExclusive - 1; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                        if (g.Get(x, y, z).IsRamp)
                            Assert.That(g.Get(x, y + 1, z).IsEmpty, Is.True, $"ramp at {x},{y},{z} has something on it");
        }

        [Test]
        public void M2MapHasNoFloatingBlocks()
        {
            VoxelGrid g = TestMaps.BuildM2(out _, out _);
            var s = new SculptSystem(g, new SculptConfig());
            for (int z = 0; z < g.SizeZ; z++)
                for (int x = 0; x < g.SizeX; x++)
                    s.NotifyCellChanged(new Int3(x, 0, z));
            g.ClearChanges();
            for (int i = 0; i < 10; i++) s.Step();
            var changes = new System.Collections.Generic.List<Int3>();
            g.DrainChanges(changes);
            Assert.That(changes, Is.Empty, "nothing should fall at map start");
        }
    }
}
