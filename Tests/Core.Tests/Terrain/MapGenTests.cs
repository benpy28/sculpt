using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class MapGenTests
    {
        [Test]
        public void SameSeedSameMapDifferentSeedDifferentMap()
        {
            var a = MapGen.Generate(7, out _);
            var b = MapGen.Generate(7, out _);
            var c = MapGen.Generate(8, out _);
            Assert.That(a.Hash(), Is.EqualTo(b.Hash()));
            Assert.That(a.Hash(), Is.Not.EqualTo(c.Hash()));
        }

        [TestCase(1)]
        [TestCase(42)]
        [TestCase(1234)]
        public void MapIsFairAndPlayable(int seed)
        {
            var g = MapGen.Generate(seed, out MapInfo info);
            int n = g.SizeX;
            // Two-fold rotational symmetry, exactly.
            for (int y = g.MinY; y < g.MaxYExclusive; y += 3)
                for (int z = 0; z < n; z += 3)
                    for (int x = 0; x < n; x += 3)
                        Assert.That(g.Get(x, y, z), Is.EqualTo(g.Get(n - 1 - x, y, n - 1 - z)), $"asymmetry at {x},{y},{z}");

            var c = new MovementConfig();
            foreach (var team in info.TeamSpawns)
            {
                Assert.That(team.Count, Is.EqualTo(5));
                foreach (var s in team)
                {
                    Assert.That(g.BoxOverlapsSolid(PlayerMovement.Box(s, c.HalfWidth, c.StandHeight)), Is.False, "spawn clear");
                    Assert.That(PlayerMovement.ProbeGround(g, c, s, c.StandHeight, out _), Is.True, "spawn on the ground");
                    Assert.That(g.Get(SubGrid.SubAt(s) + Int3.Down), Is.EqualTo(SubId.Anchor), "spawn pad is unsculptable");
                }
            }
            Assert.That(g.AllocatedChunks, Is.LessThan(g.ChunkCount), "uniform chunks stay sparse");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1234)]
        public void TreesAreSolidSymmetricAndLeaveHeadroom(int seed)
        {
            var g = MapGen.Generate(seed, out MapInfo info);
            int n = g.SizeX;
            TestContext.WriteLine($"seed {seed}: {info.Trees.Count} trees");
            Assert.That(info.Trees.Count, Is.GreaterThanOrEqualTo(8), "a light forest, not a lone tree");
            Assert.That(info.Trees.Count % 2, Is.Zero, "every tree has its rotated twin");

            var treeCells = new HashSet<Int3>();
            foreach (var t in info.Trees)
            {
                Assert.That(t.Wood.Count, Is.GreaterThan(0));
                Assert.That(t.Leaves.Count, Is.GreaterThan(50));
                foreach (var c in t.Wood) treeCells.Add(c);
                foreach (var c in t.Leaves) treeCells.Add(c);
            }
            foreach (var c in treeCells)
            {
                // Solid and unsculptable: collides and stops bullets, so what you see is what you hit.
                Assert.That(g.Get(c), Is.EqualTo(SubId.Anchor), $"tree cell {c} is an anchor");
                Assert.That(treeCells.Contains(new Int3(n - 1 - c.X, c.Y, n - 1 - c.Z)), Is.True, $"tree cell {c} has a twin");
            }

            foreach (var t in info.Trees)
                foreach (var c in t.Leaves)
                {
                    // Headroom: below every leaf, the first non-tree terrain is at least 2.7 m down.
                    int y = c.Y - 1;
                    while (y > g.MinY && (g.Get(c.X, y, c.Z) == SubId.Empty || treeCells.Contains(new Int3(c.X, y, c.Z)))) y--;
                    Assert.That(c.Y - (y + 1), Is.GreaterThanOrEqualTo(9), $"leaf {c} too low over the ground");
                }

            // Away from spawns and the plaza.
            foreach (var team in info.TeamSpawns)
                foreach (var s in team)
                    foreach (var t in info.Trees)
                    {
                        double dx = (t.X + t.Width * 0.5) * SubGrid.SubSize - s.X, dz = (t.Z + t.Width * 0.5) * SubGrid.SubSize - s.Z;
                        Assert.That(dx * dx + dz * dz, Is.GreaterThan(5.0 * 5.0), "tree clear of spawn");
                    }
        }

        [Test]
        public void OnlyTreesAndPillarsRiseAboveTheBorderWall()
        {
            // Above 4 levels the only solids are the landmark pillars and the listed tree cells (no stray anchors).
            var g = MapGen.Generate(5, out MapInfo info);
            var treeCells = new HashSet<Int3>();
            foreach (var t in info.Trees) { foreach (var c in t.Wood) treeCells.Add(c); foreach (var c in t.Leaves) treeCells.Add(c); }
            int n = g.SizeX;
            for (int z = 2; z < n - 2; z++)
                for (int x = 2; x < n - 2; x++)
                    for (int y = 8 * SubGrid.TileSubs / 2; y < g.MaxYExclusive; y++)
                    {
                        ushort id = g.Get(x, y, z);
                        if (id == SubId.Anchor && y < 6 * SubGrid.TileSubs) continue; // pillars
                        if (id != SubId.Empty) Assert.That(treeCells.Contains(new Int3(x, y, z)), Is.True, $"unexpected solid at {x},{y},{z}");
                    }
        }

        [Test]
        public void NothingFallsAtMapStart()
        {
            var g = MapGen.Generate(3, out _);
            var s = new SculptSystem(g, new SculptConfig());
            for (int z = 0; z < g.SizeZ; z += 2)
                for (int x = 0; x < g.SizeX; x += 2)
                    for (int y = -12; y < 24; y += 2)
                        if (g.Get(x, y, z) == SubId.Natural) s.NotifyCellChanged(new Int3(x, y - 1, z));
            ulong before = g.Hash();
            for (int i = 0; i < 5; i++) s.Step();
            Assert.That(g.Hash(), Is.EqualTo(before));
            Assert.That(s.Pieces.Count, Is.Zero);
        }

        [Test]
        public void TestCourseHasNothingLoose()
        {
            var g = TestMaps.BuildM2(out _, out _);
            var s = new SculptSystem(g, new SculptConfig());
            for (int z = 0; z < g.SizeZ; z += 2)
                for (int x = 0; x < g.SizeX; x += 2)
                    for (int y = -12; y < 32; y += 2)
                        if (g.Get(x, y, z) == SubId.Natural) s.NotifyCellChanged(new Int3(x, y - 1, z));
            ulong before = g.Hash();
            for (int i = 0; i < 5; i++) s.Step();
            Assert.That(g.Hash(), Is.EqualTo(before));
        }
    }
}
