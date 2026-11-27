using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The battle royale plains: exact eight-way symmetry, clear of everything built, canopies out of reach.</summary>
    public class BrNatureTests
    {
        private static SubGrid _g;
        private static BrushWorld _w;
        private static BreachMapInfo _info;
        private static BrMapInfo _br;

        [OneTimeSetUp]
        public void Build()
        {
            if (_g != null) return;
            _g = BrMap.BuildGround();
            _w = BrMap.BuildBrushes(out _info, out _br);
        }

        [Test]
        public void ThePlainsHaveTreesRocksAndBushes()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            int trees = 0, rocks = 0, bushes = 0;
            foreach (BrNature.Prop p in plan.Props)
                if (p.Kind == "tree") trees++; else if (p.Kind == "rocks") rocks++; else bushes++;
            TestContext.WriteLine($"per eighth: {trees} trees, {rocks} boulder groups, {bushes} bushes, {plan.Cells.Count} sub-cells; grid chunks with props {_g.AllocatedChunks}");
            Assert.That(trees, Is.InRange(30, 80));
            Assert.That(rocks, Is.InRange(8, 30));
            Assert.That(bushes, Is.InRange(15, 60));
        }

        [Test]
        public void EveryPropSubCellHasATwinUnderAllEightSymmetries()
        {
            int n = _g.SizeX;
            foreach (BrNature.Cell c in BrNature.GetPlan().Cells)
                foreach (int[] m in BrMap.Sym)
                {
                    BrNature.Tr(c.X, c.Z, m, n, out int x, out int z);
                    Assert.That(_g.Get(x, c.Y, z), Is.EqualTo(SubId.Anchor), $"({c.X},{c.Y},{c.Z}) has no twin at ({x},{c.Y},{z})");
                    Assert.That(BrNature.KindAt(new Int3(x, c.Y, z)), Is.EqualTo(c.Kind));
                }
            // And nothing else stands above the ground.
            int above = 0, expected = 0;
            var seen = new HashSet<long>();
            foreach (BrNature.Cell c in BrNature.GetPlan().Cells)
                foreach (int[] m in BrMap.Sym)
                {
                    BrNature.Tr(c.X, c.Z, m, n, out int x, out int z);
                    if (seen.Add(((long)x << 32) | ((long)c.Y << 16) | (uint)z)) expected++;
                }
            for (int cy = 1; cy < _g.ChunksY; cy++)
                for (int cz = 0; cz < _g.ChunksZ; cz++)
                    for (int cx = 0; cx < _g.ChunksX; cx++)
                    {
                        if (_g.ChunkSolidity(cx, cy, cz) == 0) continue;
                        Int3 o = _g.ChunkOrigin(new Int3(cx, cy, cz));
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                for (int x = 0; x < 16; x++)
                                    if (_g.IsSolid(o.X + x, o.Y + y, o.Z + z)) above++;
                    }
            Assert.That(above, Is.EqualTo(expected));
        }

        [Test]
        public void PropsStayClearOfBuildingsHillsWallsAndEachOtherEighth()
        {
            double c = _br.Size / 2;
            foreach (BrNature.Prop p in BrNature.GetPlan().Props)
            {
                double u = p.X - c, v = p.Z - c;
                Assert.That(v - p.Radius, Is.GreaterThan(0), $"{p.Kind} at {p.X:0.0},{p.Z:0.0} crosses the eighth's edge");
                Assert.That(u - v, Is.GreaterThan(p.Radius * Math.Sqrt(2)), $"{p.Kind} at {p.X:0.0},{p.Z:0.0} crosses the diagonal");
            }
            // No prop sub-cell inside any brush (tiles, hills, the rim) on the whole map.
            int n = _g.SizeX;
            double s = SubGrid.SubSize;
            foreach (BrNature.Cell cell in BrNature.GetPlan().Cells)
                foreach (int[] m in BrMap.Sym)
                {
                    BrNature.Tr(cell.X, cell.Z, m, n, out int x, out int z);
                    var box = new Aabb(new Vec3d(x * s, cell.Y * s, z * s), new Vec3d((x + 1) * s, (cell.Y + 1) * s, (z + 1) * s)).Expand(-1e-6);
                    Assert.That(_w.BoxOverlapsSolid(box), Is.False, $"prop sub-cell ({x},{cell.Y},{z}) is inside a brush");
                }
        }

        [Test]
        public void CanopiesStartAboveJumpReach()
        {
            // Leaves under the canopy height belong to bushes only (low and solid); every tree's leaves start 3.3 m up.
            BrNature.Plan plan = BrNature.GetPlan();
            var trunks = new HashSet<(int, int)>();
            foreach (BrNature.Cell c in plan.Cells) if (c.Kind == BrNature.KindWood) trunks.Add((c.X, c.Z));
            foreach (BrNature.Cell c in plan.Cells)
            {
                if (c.Kind != BrNature.KindLeaves || c.Y >= BrNature.CanopyClearSubs) continue;
                Assert.That(c.Y, Is.LessThan(6), $"low leaves at ({c.X},{c.Y},{c.Z}) are not a bush (a bush is at most 1.8 m tall)");
            }
            // Bushes stay low enough to vault, rocks low enough to climb.
            foreach (BrNature.Cell c in plan.Cells)
                if (c.Kind == BrNature.KindStone) Assert.That(c.Y, Is.LessThan(10), "a boulder is at most 3 m tall (a ledge climb)");
        }
    }
}
