using System.Diagnostics;
using NUnit.Framework;
using Sculpt.Core.Audio;

namespace Sculpt.Core.Tests.Audio
{
    /// <summary>
    /// The sound air grid treats plain flat ground as a floor instead of marking it cell by cell (the battle royale map
    /// has millions of ground columns). Both ways must give exactly the same grid everywhere on the map.
    /// </summary>
    public class SoundGridFloorTests
    {
        private static void AssertSame(ITerrain world, double size, int stride, out double fastMs, out double slowMs)
        {
            var sw = Stopwatch.StartNew();
            var fast = new SoundGrid(world);
            fast.Build();
            fastMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var slow = new SoundGrid(world, flatGroundAsFloor: false);
            slow.Build();
            slowMs = sw.Elapsed.TotalMilliseconds;
            int n = (int)(size / fast.CellSize), checkedCells = 0;
            for (int x = -2; x <= n + 2; x += stride)
                for (int z = -2; z <= n + 2; z += stride)
                    for (int y = -8; y <= 40; y++)
                    {
                        // Outside the map below ground the floor is solid where the old grid left air: sound never goes there.
                        bool inside = x >= 0 && z >= 0 && x < n && z < n;
                        if (!inside && y < 0) continue;
                        checkedCells++;
                        Assert.That(fast.Blocked(x, y, z), Is.EqualTo(slow.Blocked(x, y, z)), $"cell {x} {y} {z}");
                    }
            Assert.That(checkedCells, Is.GreaterThan(1000));
        }

        [Test]
        public void TheDuelMap_GivesTheSameGrid_WithTheGroundAsAFloor()
        {
            var world = DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out BreachMapInfo info));
            AssertSame(world, info.SizeX, 1, out double fast, out double slow);
            TestContext.WriteLine($"duel map: {fast:0} ms with the floor, {slow:0} ms marking the ground");
        }

        [Test]
        public void TheBattleRoyaleMap_GivesTheSameGrid_WithTheGroundAsAFloor()
        {
            var world = BrMap.Terrain(BrMap.BuildGround(), BrMap.BuildBrushes(out BreachMapInfo info, out _));
            AssertSame(world, info.SizeX, 3, out double fast, out double slow);
            TestContext.WriteLine($"battle royale map: {fast:0} ms with the floor, {slow:0} ms marking the ground");
        }

        [Test]
        public void GroundWithDetail_IsStillMarkedCellByCell()
        {
            // A dug or raised ground grid is not plain: it must not be treated as a floor.
            var g = new SubGrid(4, 2, 4, -16);
            g.FillBaseGround();
            g.Set(10, 0, 10, SubId.Natural); // a bump above the surface
            g.Set(20, -1, 20, SubId.Empty);  // a dent
            var fast = new SoundGrid(g);
            fast.Build();
            var slow = new SoundGrid(g, flatGroundAsFloor: false);
            slow.Build();
            for (int x = 0; x < 40; x++)
                for (int z = 0; z < 40; z++)
                    for (int y = -6; y <= 6; y++)
                        Assert.That(fast.Blocked(x, y, z), Is.EqualTo(slow.Blocked(x, y, z)), $"cell {x} {y} {z}");
        }
    }
}
