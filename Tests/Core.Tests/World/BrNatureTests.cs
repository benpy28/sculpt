using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>The battle royale plains: library shapes placed exactly, each in its own segment, clear of everything built,
    /// canopies out of reach.</summary>
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

        private static long Key(int x, int y, int z) => ((long)(x + 8192) << 32) | ((long)(y + 1024) << 16) | (uint)(z + 8192);

        [Test]
        public void ThePlainsHaveTreesRocksAndBushes_InEverySegment()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            var perSegment = new int[BrLayout.Count];
            int trees = 0, rocks = 0, bushes = 0;
            foreach (BrNature.Prop p in plan.Props)
            {
                if (p.Kind == "tree") trees++; else if (p.Kind == "rocks") rocks++; else bushes++;
                perSegment[p.Segment]++;
            }
            TestContext.WriteLine($"{trees} trees, {rocks} boulder groups, {bushes} bushes, {plan.CellCount} sub-cells, {plan.Library.Count} shapes; per segment {string.Join(" ", perSegment)}; grid chunks with props {_g.AllocatedChunks}; dropped requests {plan.Dropped.Count}");
            Assert.That(trees, Is.GreaterThan(50));
            Assert.That(rocks, Is.GreaterThan(20));
            Assert.That(bushes, Is.GreaterThan(30));
            for (int id = 0; id < BrLayout.Count; id++) Assert.That(perSegment[id], Is.InRange(1, BrLayout.MaxProps), BrLayout.Names[id]);
        }

        [Test]
        public void TheGridHoldsExactlyThePropsCells_AndTheirKinds()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            var expected = new HashSet<long>();
            foreach (BrNature.Prop p in plan.Props)
                foreach (BrNature.Cell c in plan.CellsOf(p))
                {
                    Assert.That(expected.Add(Key(c.X, c.Y, c.Z)), Is.True, "two props share a sub-cell");
                    Assert.That(_g.Get(c.X, c.Y, c.Z), Is.EqualTo(SubId.Anchor));
                    Assert.That(BrNature.KindAt(new Int3(c.X, c.Y, c.Z)), Is.EqualTo(c.Kind));
                }
            int solid = 0;
            for (int cy = 0; cy < _g.ChunksY; cy++)
                for (int cz = 0; cz < _g.ChunksZ; cz++)
                    for (int cx = 0; cx < _g.ChunksX; cx++)
                    {
                        if (_g.ChunkSolidity(cx, cy, cz) == 0) continue;
                        Int3 o = _g.ChunkOrigin(new Int3(cx, cy, cz));
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                for (int x = 0; x < 16; x++)
                                    if (_g.IsSolid(o.X + x, o.Y + y, o.Z + z)) solid++;
                    }
            Assert.That(solid, Is.EqualTo(expected.Count), "nothing else is in the grid (the height map is the ground)");
        }

        [Test]
        public void EveryOrientation_KeepsAShapesCellsAndFootprint()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            double s = SubGrid.SubSize;
            foreach (BrNature.Variant v in plan.Library)
                foreach (int[] m in BrMap.Sym)
                {
                    var seen = new HashSet<long>();
                    foreach (BrNature.Cell c in v.Cells)
                    {
                        BrNature.Orient(c.X, c.Z, m, out int x, out int z);
                        Assert.That(seen.Add(Key(x, c.Y, z)), Is.True, "an orientation folds two cells together");
                        double fx = Math.Max(Math.Abs(x), Math.Abs(x + 1)) * s, fz = Math.Max(Math.Abs(z), Math.Abs(z + 1)) * s;
                        Assert.That(Math.Sqrt(fx * fx + fz * fz), Is.LessThanOrEqualTo(v.Radius));
                        // Centres turn about the origin exactly like the map's own symmetries.
                        double ux = c.X + 0.5, uz = c.Z + 0.5;
                        Assert.That(x + 0.5, Is.EqualTo(m[0] * ux + m[1] * uz).Within(1e-12));
                        Assert.That(z + 0.5, Is.EqualTo(m[2] * ux + m[3] * uz).Within(1e-12));
                    }
                }
        }

        [Test]
        public void EveryTrunk_ReachesTheGround_AndTreesGrowOnHillsides()
        {
            // The lowest trunk cell of every tree is under the ground at its foot; and some trees stand where the ground
            // under their canopy rises more than a metre (walkable hillsides are no longer bare).
            BrNature.Plan plan = BrNature.GetPlan();
            HeightField f = BrMap.Field;
            double s = SubGrid.SubSize;
            int onSlopes = 0;
            foreach (BrNature.Prop p in plan.Props)
            {
                if (p.Kind != "tree") continue;
                int bottom = int.MaxValue;
                double groundAtFoot = double.PositiveInfinity, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (BrNature.Cell c in plan.CellsOf(p))
                {
                    if (c.Kind == BrNature.KindWood && c.Y < bottom) bottom = c.Y;
                    double g = f.HeightAt((c.X + 0.5) * s, (c.Z + 0.5) * s);
                    lo = Math.Min(lo, g); hi = Math.Max(hi, g);
                }
                foreach (BrNature.Cell c in plan.CellsOf(p))
                    if (c.Kind == BrNature.KindWood && c.Y == bottom) 
                        for (int k = 0; k < 5; k++)
                            groundAtFoot = Math.Min(groundAtFoot, f.HeightAt((c.X + (k == 4 ? 0.5 : k % 2)) * s, (c.Z + (k == 4 ? 0.5 : k / 2)) * s));
                Assert.That(bottom * s, Is.LessThanOrEqualTo(groundAtFoot), $"tree at {p.X:0.0},{p.Z:0.0}: the trunk floats");
                if (hi - lo > 1.0) onSlopes++;
            }
            TestContext.WriteLine($"{onSlopes} trees on hillsides");
            Assert.That(onSlopes, Is.GreaterThan(3));
        }

        [Test]
        public void PropsStayInTheirSegment_ClearOfEverythingBuilt()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            double s = SubGrid.SubSize;
            foreach (BrNature.Prop p in plan.Props)
            {
                Rect2 area = BrLayout.Buildable(p.Segment);
                Assert.That(area.Contains(p.X, p.Z, p.Radius), Is.True, $"{p.Kind} at {p.X:0.0},{p.Z:0.0} reaches out of {BrLayout.Names[p.Segment]}");
                foreach (BrNature.Cell c in plan.CellsOf(p))
                {
                    double dx = (c.X + 0.5) * s - p.SX * s, dz = (c.Z + 0.5) * s - p.SZ * s;
                    Assert.That(Math.Sqrt(dx * dx + dz * dz), Is.LessThanOrEqualTo(p.Radius), $"{p.Kind} at {p.X:0.0},{p.Z:0.0} reaches past its footprint");
                    var box = new Aabb(new Vec3d(c.X * s, c.Y * s, c.Z * s), new Vec3d((c.X + 1) * s, (c.Y + 1) * s, (c.Z + 1) * s)).Expand(-1e-6);
                    Assert.That(_w.BoxOverlapsSolid(box), Is.False, $"prop sub-cell ({c.X},{c.Y},{c.Z}) is inside a brush");
                }
            }
        }

        [Test]
        public void CanopiesStartAboveJumpReach_OnTheHillsToo()
        {
            // Every tree's leaves start 3.3 m over the highest ground under them (a jump reaches 3.1 m); bushes stay low
            // enough to vault and boulders low enough to climb, over the lowest ground under them.
            BrNature.Plan plan = BrNature.GetPlan();
            HeightField f = BrMap.Field;
            double s = SubGrid.SubSize;
            foreach (BrNature.Prop p in plan.Props)
            {
                double lowest = double.PositiveInfinity;
                foreach (BrNature.Cell c in plan.CellsOf(p)) lowest = Math.Min(lowest, f.MaxOver(c.X * s, c.Z * s, (c.X + 1) * s, (c.Z + 1) * s));
                foreach (BrNature.Cell c in plan.CellsOf(p))
                {
                    double under = f.MaxOver(c.X * s, c.Z * s, (c.X + 1) * s, (c.Z + 1) * s);
                    if (p.Kind == "tree" && c.Kind == BrNature.KindLeaves)
                        Assert.That(c.Y * s - under, Is.GreaterThanOrEqualTo(3.3 - 1e-6), $"tree at {p.X:0.0},{p.Z:0.0}: leaves within jump reach");
                    if (p.Kind == "bush") Assert.That((c.Y + 1) * s - lowest, Is.LessThanOrEqualTo(1.8 + BrNature.MaxRiseBush + 1e-6), $"bush at {p.X:0.0},{p.Z:0.0} too tall");
                    if (p.Kind == "rocks") Assert.That((c.Y + 1) * s - lowest, Is.LessThanOrEqualTo(3.0 + BrNature.MaxRiseRocks + 1e-6), $"boulder at {p.X:0.0},{p.Z:0.0} too tall");
                }
            }
        }
    }
}
