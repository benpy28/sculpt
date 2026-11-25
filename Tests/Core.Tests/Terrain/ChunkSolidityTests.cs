using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The mesher skips a chunk's own 16 dual layers when the chunk and its -x, -y, -z neighbours are
    /// all uniformly solid or all uniformly empty (ChunkMesher.NoSurface). That is only right if such a chunk really has
    /// no visible face: checked here on the duel and breach maps' ground, dual cell by dual cell.
    /// </summary>
    public class ChunkSolidityTests
    {
        private static bool NoSurface(SubGrid g, Int3 o)
        {
            int cx = o.X >> 4, cy = (o.Y - g.MinY) >> 4, cz = o.Z >> 4;
            int want = g.ChunkSolidity(cx, cy, cz);
            if (want < 0) return false;
            for (int y = cy - 1; y <= cy; y++)
                for (int z = cz - 1; z <= cz; z++)
                    for (int x = cx - 1; x <= cx; x++)
                        if (g.ChunkSolidity(x, y, z) != want) return false;
            return true;
        }

        private static int CheckSkippedChunksHaveNoFaces(SubGrid g)
        {
            const int n = SubGrid.ChunkSize;
            int skipped = 0;
            for (int i = 0; i < g.ChunkCount; i++)
            {
                Int3 o = g.ChunkOrigin(g.ChunkCoordFromIndex(i));
                if (!NoSurface(g, o)) continue;
                skipped++;
                for (int y = 0; y < n; y++) // the chunk's own 16 layers (the grid-top layer is always meshed)
                    for (int z = 0; z < n; z++)
                        for (int x = 0; x < n; x++)
                        {
                            int cfg = TerrainShape.ConfigAt(g, o.X + x, o.Y + y, o.Z + z);
                            Assert.That(cfg == 0 || cfg == 255, Is.True, $"chunk {i} cell {x},{y},{z}: cfg {cfg}");
                            if (cfg == 0) continue;
                            TerrainShape.Poly p = TerrainShape.Get(cfg);
                            foreach (var f in p.Faces)
                                Assert.That(TerrainShape.IsFaceVisible(g, o.X + x, o.Y + y, o.Z + z, p, f), Is.False, $"chunk {i}: a visible face");
                        }
            }
            return skipped;
        }

        [Test]
        public void SkippedChunksOnTheDuelAndBreachGround_HaveNoVisibleFace()
        {
            SubGrid duel = DuelMap.BuildGround();
            int a = CheckSkippedChunksHaveNoFaces(duel);
            Assert.That(a, Is.GreaterThan(duel.ChunkCount / 3), "most of a flat map's chunks are skipped");
            SubGrid breach = BreachMap.BuildGround();
            int b = CheckSkippedChunksHaveNoFaces(breach);
            Assert.That(b, Is.GreaterThan(0));
            TestContext.WriteLine($"duel: {a} of {duel.ChunkCount} chunks skipped; breach: {b} of {breach.ChunkCount}");
        }

        [Test]
        public void ChunkSolidity_KnowsTheVirtualChunksRoundTheGrid()
        {
            var g = new SubGrid(2, 2, 2, -16);
            g.FillBaseGround();
            g.Compact();
            Assert.That(g.ChunkSolidity(0, 0, 0), Is.EqualTo(1), "below ground: rock");
            Assert.That(g.ChunkSolidity(0, 1, 0), Is.EqualTo(0), "above ground: air");
            Assert.That(g.ChunkSolidity(0, 2, 0), Is.EqualTo(0), "above the grid: empty");
            Assert.That(g.ChunkSolidity(-1, 2, 0), Is.EqualTo(1), "beside the grid: bedrock");
            Assert.That(g.ChunkSolidity(0, -1, 0), Is.EqualTo(1), "below the grid: bedrock");
            g.Set(new Int3(3, 3, 3), SubId.Natural);
            Assert.That(g.ChunkSolidity(0, 1, 0), Is.EqualTo(-1), "mixed");
        }
    
        [Test]
        public void SameCells_FreshCopiesMatch_AnEditDoesNot()
        {
            SubGrid a = DuelMap.BuildGround(), b = DuelMap.BuildGround();
            Assert.That(a.SameCells(b), Is.True);
            b.Set(new Int3(5, -3, 5), SubId.Empty);
            Assert.That(a.SameCells(b), Is.False);
            Assert.That(a.SameCells(BreachMap.BuildGround()), Is.False, "another map");
        }
    }
}
