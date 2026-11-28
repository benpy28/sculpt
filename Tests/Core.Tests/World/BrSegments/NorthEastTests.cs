using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The North East segment's own promises (Hilltop Fort, <see cref="NorthEastSegment"/>): everything designed was
    /// built, the cliff is still a cliff once every footing is cut, the fort's walls are too tall to climb with the gate
    /// the only way in at ground level, the keep's, lookout's and lift tower's roofs are reachable, every sealed room is
    /// sealed, and the lift tower's bridge lands on the summit.
    /// </summary>
    public class NorthEastTests
    {
        private static BrMap.TemplateBuild T => BrMap.Template;
        private static NorthEastSegment Seg => (NorthEastSegment)BrSegments.All[8];
        private static BrBuild B => T.Segments[8];

        [Test]
        public void EverythingDesigned_WasBuilt()
        {
            _ = T;
            Assert.That(Seg.Skipped, Is.Empty, string.Join("\n", Seg.Skipped));
            var dropped = new List<string>();
            foreach (PropRequest q in BrNature.GetPlan().Dropped) if (q.Segment == 8) dropped.Add($"{q.Shape} at {q.X:0},{q.Z:0}");
            Assert.That(dropped, Is.Empty, string.Join(", ", dropped));
            var names = new List<string>();
            foreach (BrPoi p in B.Pois) names.Add(p.Name);
            Assert.That(names, Is.SupersetOf(new[] { "Hillgate", "Hilltop Fort", "Underhill", "Cliffside", "Rim Farm", "West Road", "Lookout Ridge" }));
            TestContext.WriteLine($"{B.Buildings.Count} buildings, {B.Chests.Count} chests, {B.Items.Count} items, {B.TileEnd - B.TileFirst} tiles");
        }

        [Test]
        public void TheHill_IsWalkableAllRoundButTheSouth_WhereTheCliffStaysACliff()
        {
            HeightField f = BrMap.Field;
            // Across the fort's width, every column south of the summit drops at least 20 m within 16 m: no footing filled it.
            for (double x = NorthEastSegment.Fort.X0 - 8; x <= NorthEastSegment.Fort.X1; x += 1.5)
            {
                double lip = NorthEastSegment.Lip(x);
                Assert.That(f.HeightAt(x, lip + 3), Is.EqualTo(NorthEastSegment.SummitY).Within(0.05), $"the summit at x {x:0.0}");
                double drop = 0;
                for (double z = lip; z >= lip - 16; z -= 0.5) drop = Math.Max(drop, NorthEastSegment.SummitY - f.HeightAt(x, z));
                Assert.That(drop, Is.GreaterThan(20), $"the cliff at x {x:0.0}");
            }
            // The west, north and east slopes are walkable (under 35 degrees) on the way up.
            foreach (var (x0, z0, dx, dz) in new[] { (995.0, 1112.0, 1.0, 0.0), (1117.0, 1235.0, 0.0, -1.0), (1225.0, 1128.0, -1.0, 0.0) })
                for (double s = 0; s < 80; s += 1)
                {
                    double x = x0 + dx * s, z = z0 + dz * s;
                    double rise = Math.Abs(f.HeightAt(x + dx, z + dz) - f.HeightAt(x, z));
                    Assert.That(rise, Is.LessThan(Math.Tan(35 * Math.PI / 180)), $"the climb at {x:0},{z:0}");
                }
        }

        [Test]
        public void TheFortWalls_AreTooTallToClimb()
        {
            // Every tile of the fort's wall line rises the full four rows; nothing but the gate breaks it.
            BrushWorld w = T.World;
            Rect2 fort = NorthEastSegment.Fort;
            double y = NorthEastSegment.SummitY, top = y + NorthEastSegment.FortWallRows * 1.5;
            for (double s = 0.5; s < fort.X1 - fort.X0; s += 1)
            {
                Assert.That(Solid(w, fort.X0 + s, top - 0.2, fort.Z0 + 0.15), Is.True, "south wall");
                Assert.That(Solid(w, fort.X0 + s, top - 0.2, fort.Z1 - 0.15), Is.True, "north wall");
            }
            for (double s = 0.5; s < fort.Z1 - fort.Z0; s += 1)
            {
                double z = fort.Z0 + s;
                Assert.That(Solid(w, fort.X1 - 0.15, top - 0.2, z), Is.True, "east wall");
                bool gate = z > NorthEastSegment.GateZ0 && z < NorthEastSegment.GateZ1;
                Assert.That(Solid(w, fort.X0 + 0.15, y + 1, z), Is.Not.EqualTo(gate), $"west wall at z {z:0.0}");
            }
            Assert.That(top - y, Is.GreaterThan(new MovementConfig().MantleMaxHeight + 0.5), "taller than a climb");
        }

        private static bool Solid(BrushWorld w, double x, double y, double z) =>
            w.BoxOverlapsSolid(new Aabb(new Vec3d(x - 0.05, y - 0.05, z - 0.05), new Vec3d(x + 0.05, y + 0.05, z + 0.05)));

        // ---------------------------------------------------------------- reachability (walking grid, doors open)

        private static BotNav _nav;

        /// <summary>A walking grid over the segment's east and north (fort, Cliffside, ridge), doors open, intact walls.</summary>
        private static BotNav Nav
        {
            get
            {
                if (_nav != null) return _nav;
                BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out _);
                foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
                SubGrid g = BrMap.BuildGround();
                Rect2 a = BrLayout.Buildable(8);
                _nav = BotNav.Build(BrMap.Terrain(g, w), a.X0, a.Z0, a.X1 - a.X0, a.Z1 - a.Z0, 0.75, topY: BrMap.NavTop, floorY: BrGround.Lowest - 2,
                                    columnTop: BrMap.NavColumnTop(w, g));
                return _nav;
            }
        }

        private static int Spot(double x, double y, double z)
        {
            int n = Nav.Nearest(new Vec3d(x, y, z), 2);
            Assert.That(n, Is.GreaterThanOrEqualTo(0), $"a walkable spot near {x:0.0},{y:0.0},{z:0.0}");
            Assert.That(Math.Abs(Nav.Nodes[n].Y - y), Is.LessThan(0.8), $"the spot near {x:0.0},{y:0.0},{z:0.0} is on the right floor (found {Nav.Nodes[n].Y:0.00})");
            return n;
        }

        [Test]
        public void EveryPlace_CanBeWalkedTo_FromHillgate_AndEveryRoofTooFromItsStairs()
        {
            Vec3d a = BrLayout.Anchor(8);
            int home = Spot(a.X + 6, BrGround.Base, a.Z);
            double s = NorthEastSegment.SummitY;
            var goals = new (string, double, double, double)[]
            {
                ("the fort's courtyard", 1112, s, 1130),
                ("in front of the gate", 1086, s, 1119),
                ("the keep's roof", 1132, s + 6 * 4.8, 1126),
                ("the keep's fifth floor", 1135, s + 5 * 4.8, 1113),
                ("a gate tower's roof", 1100, s + 3 * 4.8, 1137),
                ("the lookout's roof", 965, double.NaN, 1150),
                ("Underhill's main street", 1000, BrGround.Base, 1006),
                ("Cliffside under the cliff", 1160, BrGround.Base, 1045),
                ("the lift tower's roof", 1121, BrGround.Base + 7 * 4.8, 1046),
                ("the north wall walk", 1115, s + NorthEastSegment.WalkHeight, 1145),
                ("the east wall walk", 1147, s + NorthEastSegment.WalkHeight, 1110),
                ("the south wall walk", 1112, s + NorthEastSegment.WalkHeight, 1093),
                ("half way up the Cleft", NorthEastSegment.CleftX, double.NegativeInfinity, 1085),
                ("the Cleft's landing", NorthEastSegment.CleftX, double.NegativeInfinity, 1095),
                ("a north slope cottage", 1022 + 6.3 + 3.3, double.NegativeInfinity, 1204 + 3.3),
                ("the north watch post's roof", 1180, double.NaN, 1206),
            };
            foreach (var (name, x, y0, z) in goals)
            {
                double y = y0;
                if (double.IsNaN(y)) y = T.Segments[8].Buildings.Find(bd => bd.Bounds.Min.X < x && bd.Bounds.Max.X > x && bd.Bounds.Min.Z < z && bd.Bounds.Max.Z > z).Bounds.Max.Y;
                if (double.IsNegativeInfinity(y)) y = BrMap.Field.HeightAt(x, z);
                int n = Spot(x, y, z);
                Assert.That(Nav.SameArea(home, n), Is.True, $"{name} can be reached from Hillgate");
            }
        }

        [Test]
        public void EverySealedRoom_HasNoWayIn()
        {
            Vec3d a = BrLayout.Anchor(8);
            int home = Spot(a.X + 6, BrGround.Base, a.Z);
            double s = NorthEastSegment.SummitY;
            // Middle of each sealed cell (x, floor, z).
            var rooms = new (string, double, double, double)[]
            {
                ("the inn's cellar", a.X - 7 - 12.9 + 3.3, BrGround.Base, a.Z - 19.05 + 3 * 6.3 + 3.3),
                ("the smithy's coal store", a.X - 5.34 + 3.3, BrGround.Base, a.Z - 7.04 - 19.2 + 3.3),
                ("the stables' tack room", a.X - 5.34 + 3.3, BrGround.Base, a.Z + 8.16 + 6.3 + 3.3),
                ("the granary", a.X - 23.84 + 6.6, BrGround.Base, a.Z + 22.66 + 6.6),
                ("the keep's vault", 1121 + 2 * 6.3 + 3.3, s, 1110 + 2 * 6.3 + 3.3),
                ("the armoury's vault", 1133 + 6.6, s, 1094.5 + 6.3 + 3.3),
                ("the chapel's crypt", 1107 + 6.3 + 3.3, s, 1094.5 + 3.3),
                ("the well house", 1137 + 3.3, s, 1133 + 3.3),
            };
            foreach (var (name, x, y, z) in rooms)
            {
                int n = Spot(x, y, z);
                Assert.That(Nav.SameArea(home, n), Is.False, $"{name} is sealed");
            }
        }

        [Test]
        public void TheLiftBridge_LandsOnTheSummit_InFrontOfTheFort()
        {
            Aabb bridge = default;
            bool found = false;
            foreach (Aabb br in T.Info.Block.Bridges)
                if (br.Min.X > 1100 && br.Min.X < 1130 && br.Max.Z > 1070) { bridge = br; found = true; }
            Assert.That(found, Is.True);
            HeightField f = BrMap.Field;
            double x = (bridge.Min.X + bridge.Max.X) / 2;
            Assert.That(f.HeightAt(x, bridge.Max.Z - 0.5), Is.EqualTo(NorthEastSegment.SummitY).Within(0.05), "its end is over the summit");
            double drop = bridge.Max.Y - NorthEastSegment.SummitY;
            Assert.That(drop, Is.InRange(1.0, 5.0), "a short drop down onto it");
            Assert.That(bridge.Max.Z, Is.LessThan(NorthEastSegment.Fort.Z0 - 6), "it lands outside the fort's south wall");
        }
        [Test]
        public void TheCleft_IsAWalkableClimbBetweenRockWalls()
        {
            HeightField f = BrMap.Field;
            double x = NorthEastSegment.CleftX, top = 0;
            for (double z = NorthEastSegment.CleftZ0 - 3; z < NorthEastSegment.CleftEnd - 1; z += 1)
            {
                double rise = f.HeightAt(x, z + 1) - f.HeightAt(x, z);
                Assert.That(Math.Abs(rise), Is.LessThan(Math.Tan(35 * Math.PI / 180)), $"the Cleft's floor at z {z:0}");
                top = Math.Max(top, f.HeightAt(x, z));
            }
            Assert.That(top, Is.EqualTo(NorthEastSegment.CleftLanding).Within(0.05), "it climbs to the landing under the stair");
            // Half way up, rock walls stand at least 8 m over the floor on both sides.
            double zm = NorthEastSegment.CleftZ0 + 30, floor = f.HeightAt(x, zm);
            Assert.That(f.HeightAt(x - 8, zm) - floor, Is.GreaterThan(8), "west wall");
            Assert.That(f.HeightAt(x + 8, zm) - floor, Is.GreaterThan(8), "east wall");
        }

        [Test]
        public void TheWallWalks_LeaveChestHighCoverOverTheWalls()
        {
            BrushWorld w = T.World;
            double walk = NorthEastSegment.SummitY + NorthEastSegment.WalkHeight;
            Rect2 fort = NorthEastSegment.Fort;
            // Standing on the north walk: solid floor under the feet, the wall's top 1.5 m above them.
            foreach (double x in new[] { 1095.0, 1120.0, 1145.0 })
            {
                Assert.That(Solid(w, x, walk - 0.1, fort.Z1 - 1.0), Is.True, $"walk floor at x {x}");
                Assert.That(Solid(w, x, walk + 1.0, fort.Z1 - 0.15), Is.True, $"cover at x {x}");
                Assert.That(Solid(w, x, walk + 1.4, fort.Z1 - 0.15), Is.False, $"open over the cover at x {x}");
            }
        }
    }
}
