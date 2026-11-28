using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The South West segment's (the Factory's) own promises: every roof, gallery and the water tank is a walk from the
    /// home plaza (stairs, the roof bridge, the door off the boiler roof, the stair onto the containers), and every
    /// sealed room really has no way in. Checked on the bots' walking grid with every door open.
    /// </summary>
    public class SouthWestTests
    {
        private static BotNav _nav;
        /// <summary>The walking grid finds at most 12 floors down a column (BotNav.FindFloors), so it can't see the whole
        /// twelve-storey stack: spots above this are checked on the stairs instead (BrMapTests runs every flight).</summary>
        private const double NavTop = 41;

        private static BotNav Nav()
        {
            if (_nav != null) return _nav;
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out _);
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            Rect2 a = BrLayout.Buildable(0);
            ITerrain t = BrMap.Terrain(BrMap.BuildGround(), w);
            _nav = BotNav.Build(t, a.X0 - 4, a.Z0 - 4, a.X1 - a.X0 + 8, a.Z1 - a.Z0 + 8, 1.0, topY: NavTop, floorY: BrGround.Lowest - 2);
            return _nav;
        }

        private static int At(BotNav nav, Vec3d p)
        {
            int n = nav.Nearest(p, 2);
            if (n < 0 || Math.Abs(nav.Feet(n).Y - p.Y) > 0.4) return -1;
            return n;
        }

        private static int Home(BotNav nav) => At(nav, BrLayout.Anchor(0) + new Vec3d(-8, 0, -2));

        [Test]
        public void EveryRoofGalleryAndTheTank_IsAWalkFromTheHomePlaza()
        {
            BotNav nav = Nav();
            int home = Home(nav);
            Assert.That(home, Is.GreaterThanOrEqualTo(0), "the plaza is on the grid");
            Assert.That(SouthWestSegment.HighSpots.Count, Is.GreaterThanOrEqualTo(15));
            var bad = new List<string>();
            foreach (var (name, p) in SouthWestSegment.HighSpots)
            {
                if (p.Y > NavTop - 2) continue;
                int n = At(nav, p);
                if (n < 0) bad.Add($"{name} at {p}: no walking spot there");
                else if (!nav.SameArea(home, n)) bad.Add($"{name} at {p}: no way there from the plaza");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        [Test]
        public void TheSealedRooms_HaveNoWayIn()
        {
            BotNav nav = Nav();
            int home = Home(nav);
            Assert.That(SouthWestSegment.SealedChests.Count, Is.GreaterThanOrEqualTo(8));
            var bad = new List<string>();
            foreach (Vec3d p in SouthWestSegment.SealedChests)
            {
                int n = At(nav, p);
                if (n < 0) bad.Add($"sealed room at {p}: no walking spot inside");
                else if (nav.SameArea(home, n)) bad.Add($"sealed room at {p} can be walked into");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        [Test]
        public void TheSkylineIsTallButUnderTheCap()
        {
            double top = 0;
            BrBuild b = BrMap.Template.Segments[0];
            foreach (BrBuilding bd in b.Buildings) top = Math.Max(top, bd.Bounds.Max.Y);
            TestContext.WriteLine($"tallest: {top:0.0} m");
            Assert.That(top, Is.GreaterThan(45), "the boiler stack is the factory's landmark (ten storeys: only the Old Spire has twelve)");
            Assert.That(top, Is.LessThan(85));
        }
    }
}
