using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The contract every battle royale segment builds to (<see cref="BrLayout"/>): it stays in its square, keeps the
    /// lanes and the spawn plot clear, puts exactly the home POI's chests round its anchor, and keeps to its budgets.
    /// One case per segment: run yours with --filter "FullyQualifiedName~BrSegmentTests&amp;Name~(NorthEast)".
    /// </summary>
    public class BrSegmentTests
    {
        private static BrMap.TemplateBuild T => BrMap.Template;

        public static IEnumerable<TestCaseData> Segments()
        {
            for (int id = 0; id < BrLayout.Count; id++) yield return new TestCaseData(id).SetArgDisplayNames(BrLayout.Stems[id]);
        }

        public static IEnumerable<TestCaseData> OuterSegments()
        {
            for (int id = 0; id < BrLayout.Count; id++)
                if (id != BrLayout.Centre) yield return new TestCaseData(id).SetArgDisplayNames(BrLayout.Stems[id]);
        }

        private static bool Inside(Rect2 area, Aabb b) =>
            b.Min.X >= area.X0 - 1e-6 && b.Min.Z >= area.Z0 - 1e-6 && b.Max.X <= area.X1 + 1e-6 && b.Max.Z <= area.Z1 + 1e-6;

        [Test]
        public void TheLayout_IsAThreeByThreeGrid_WithOneRoadPortPerInnerBorder_AndAnchorsOnARing()
        {
            Assert.That(BrLayout.SegSize * BrLayout.Across, Is.EqualTo(BrMap.Size).Within(1e-9));
            Assert.That(BrLayout.Ports.Length, Is.EqualTo(12));
            for (int id = 0; id < BrLayout.Count; id++)
            {
                Assert.That(BrSegments.All[id].Id, Is.EqualTo(id), $"segment {id} is listed in its place");
                Rect2 b = BrLayout.Bounds(id);
                Assert.That(BrLayout.Owner(b.CX, b.CZ), Is.EqualTo(id));
                if (id == BrLayout.Centre) continue;
                Vec3d a = BrLayout.Anchor(id);
                Assert.That(BrLayout.Owner(a.X, a.Z), Is.EqualTo(id), $"{BrLayout.Names[id]}'s anchor is in it");
                Assert.That(BrLayout.Buildable(id).Contains(a.X, a.Z, BrLayout.HomeRadius - BrLayout.BorderKeepOut - 1e-6), Is.True, "its home plaza fits in it");
            }
            foreach (BrLayout.Port p in BrLayout.Ports)
            {
                Assert.That(BrLayout.Owner(p.LaneIn(p.A).CX, p.LaneIn(p.A).CZ), Is.EqualTo(p.A));
                Assert.That(BrLayout.Owner(p.LaneIn(p.B).CX, p.LaneIn(p.B).CZ), Is.EqualTo(p.B));
                Assert.That(BrLayout.Play.Contains(p.X, p.Z, 50), Is.True, "the port is well inside the play area");
            }
        }

        [TestCaseSource(nameof(Segments))]
        public void StaysInItsSquare(int id)
        {
            BrBuild b = T.Segments[id];
            BrushWorld w = T.World;
            Rect2 area = b.Area;
            for (int t = b.TileFirst; t < b.TileEnd; t++)
                Assert.That(Inside(area, w.Tiles.BoundsOf(t)), Is.True, $"tile {t} at {w.Tiles.BoundsOf(t).Min} is outside {b.Name}'s buildable area {area}");
            for (int k = b.BrushFirst; k < b.BrushEnd; k++)
                Assert.That(Inside(area, w[k].Bounds), Is.True, $"brush {k} at {w[k].Bounds.Min} is outside {b.Name}'s buildable area");
            for (int k = b.FootingFirst; k < b.FootingEnd; k++)
            {
                Footings.Footing f = T.Footings[k];
                Assert.That(area.Contains(f.X0 - f.Apron, f.Z0 - f.Apron) && area.Contains(f.X1 + f.Apron, f.Z1 + f.Apron), Is.True, $"footing {k} at {f.X0:0.0},{f.Z0:0.0} (with its apron) is outside {b.Name}'s buildable area");
            }
            foreach (BrChestSpot c in b.Chests) Assert.That(area.Contains(c.Position.X, c.Position.Z, 0.5), Is.True, $"chest at {c.Position}");
            foreach (GroundItem g in b.Items) Assert.That(area.Contains(g.Position.X, g.Position.Z, 0.5), Is.True, $"item at {g.Position}");
            foreach (BrPoi p in b.Pois) Assert.That(BrLayout.Owner(p.Centre.X, p.Centre.Z), Is.EqualTo(id), $"POI {p.Name}");
            Assert.That(b.BuildingEnd - b.BuildingFirst, Is.EqualTo(b.Buildings.Count), "every building goes up through BrBuild.Raise (so it has a look)");
        }

        [TestCaseSource(nameof(Segments))]
        public void KeepsTheLanesAndTheSpawnPlotClear(int id)
        {
            BrBuild b = T.Segments[id];
            BrushWorld w = T.World;
            bool Hits(Rect2 r)
            {
                for (int t = b.TileFirst; t < b.TileEnd; t++) if (r.Overlaps(Rect2.Of(w.Tiles.BoundsOf(t)))) return true;
                for (int k = b.BrushFirst; k < b.BrushEnd; k++) if (r.Overlaps(Rect2.Of(w[k].Bounds))) return true;
                return false;
            }
            foreach (Rect2 r in b.Reserved)
            {
                Assert.That(Hits(r), Is.False, $"{b.Name} builds in the reserved area {r} (a port lane or the spawn plot)");
                for (int k = b.FootingFirst; k < b.FootingEnd; k++)
                {
                    Footings.Footing f = T.Footings[k];
                    Assert.That(r.Overlaps(new Rect2(f.X0 - f.Apron, f.Z0 - f.Apron, f.X1 + f.Apron, f.Z1 + f.Apron)), Is.False, $"a footing at {f.X0:0.0},{f.Z0:0.0} reaches into {r}");
                }
                foreach (BrChestSpot c in b.Chests) Assert.That(r.Contains(c.Position.X, c.Position.Z), Is.False, $"a chest at {c.Position} in {r}");
            }
            foreach (var l in b.Lanes)
            {
                for (int t = b.TileFirst; t < b.TileEnd; t++)
                    Assert.That(BrBuild.LaneTouches(l.From, l.To, l.Half, Rect2.Of(w.Tiles.BoundsOf(t))), Is.False, $"tile {t} at {w.Tiles.BoundsOf(t).Min} stands in a pad lane");
                for (int k = b.FootingFirst; k < b.FootingEnd; k++)
                {
                    Footings.Footing f = T.Footings[k];
                    Assert.That(BrBuild.LaneTouches(l.From, l.To, l.Half, new Rect2(f.X0 - f.Apron, f.Z0 - f.Apron, f.X1 + f.Apron, f.Z1 + f.Apron)), Is.False, $"a footing at {f.X0:0.0},{f.Z0:0.0} touches a pad lane");
                }
            }
        }

        [TestCaseSource(nameof(OuterSegments))]
        public void HasExactlyTheHomeChests_RoundItsAnchor(int id)
        {
            Vec3d a = BrLayout.Anchor(id);
            int n = 0;
            foreach (BrChestSpot c in T.Br.Chests)
                if (Math.Sqrt(Sq(c.Position.X - a.X) + Sq(c.Position.Z - a.Z)) <= BrLayout.HomeRadius) n++;
            Assert.That(n, Is.EqualTo(BrLayout.HomeChests), $"{BrLayout.Names[id]}'s home POI");
            BrRegion r = T.Br.Regions.Find(x => x.Segment == id);
            Assert.That(r.Poi, Is.GreaterThanOrEqualTo(0), $"{BrLayout.Names[id]} names its home POI \"{BrSegments.All[id].HomePoiName}\"");
        }

        [TestCaseSource(nameof(Segments))]
        public void KeepsToItsBudgets(int id)
        {
            BrBuild b = T.Segments[id];
            int tiles = b.TileEnd - b.TileFirst, props = 0;
            BrNature.Plan plan = BrNature.GetPlan();
            foreach (BrNature.Prop p in plan.Props) if (p.Segment == id) props++;
            int dropped = 0;
            foreach (PropRequest q in plan.Dropped) if (q.Segment == id) dropped++;
            TestContext.WriteLine($"{b.Name}: {tiles} tiles, {b.Buildings.Count} buildings, {b.Chests.Count} chests, {b.Items.Count} items, {props} props, {b.Pois.Count} POIs");
            Assert.That(tiles, Is.LessThanOrEqualTo(id == BrLayout.Centre ? BrLayout.MaxTilesCentre : BrLayout.MaxTiles), "tiles");
            Assert.That(b.Chests.Count, Is.InRange(BrLayout.MinChests, id == BrLayout.Centre ? BrLayout.MaxChestsCentre : BrLayout.MaxChests), "chests");
            Assert.That(b.Items.Count, Is.LessThanOrEqualTo(BrLayout.MaxItems), "items");
            Assert.That(props, Is.LessThanOrEqualTo(BrLayout.MaxProps), "props");
            Assert.That(dropped, Is.EqualTo(0), "every prop it asked for fits (move the ones that don't)");
        }

        [Test]
        public void TheGround_IsTheSameEveryTimeAndTheSegmentsArePure()
        {
            // The field is built on every core: a segment's ground must not depend on order or shared state.
            var rng = new Random(5);
            for (int k = 0; k < 2000; k++)
            {
                double x = rng.NextDouble() * BrMap.Size, z = rng.NextDouble() * BrMap.Size;
                Assert.That(BrMap.Natural(x, z), Is.EqualTo(BrMap.Natural(x, z)));
            }
        }

        [Test]
        public void SixBuildsAtOnce_AreIdentical()
        {
            // The game builds the server's and the client's copies of the map on two threads at once: a segment that keeps
            // build state in a static or a field of its (shared) object breaks or mixes them (Old City did, 2026-11-28).
            var builds = new BrMap.TemplateBuild[6];
            System.Threading.Tasks.Parallel.For(0, builds.Length, k => builds[k] = BrMap.Build(null));
            for (int k = 1; k < builds.Length; k++)
            {
                Assert.That(builds[k].World.Tiles.Count, Is.EqualTo(builds[0].World.Tiles.Count));
                Assert.That(builds[k].World.Count, Is.EqualTo(builds[0].World.Count));
                Assert.That(builds[k].Br.Chests.Count, Is.EqualTo(builds[0].Br.Chests.Count));
                for (int c = 0; c < builds[0].Br.Chests.Count; c++)
                    Assert.That(builds[k].Br.Chests[c].Position, Is.EqualTo(builds[0].Br.Chests[c].Position));
                for (int t = 0; t < builds[0].World.Tiles.Count; t += 97)
                    Assert.That(builds[k].World.Tiles.BoundsOf(t).Min, Is.EqualTo(builds[0].World.Tiles.BoundsOf(t).Min));
            }
        }

        private static double Sq(double v) => v * v;
    }
}
