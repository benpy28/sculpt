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
