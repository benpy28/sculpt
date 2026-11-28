using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The West segment's own promises (the Construction Site, <see cref="WestSegment"/>): its sealed rooms really have
    /// no way in and each holds loot, every POI, every tower roof and every scaffold bridge can be walked to from the
    /// spawn region (doors open, as bots and players can), no sealed room can, and the pit is dug as designed.
    /// </summary>
    public class WestTests
    {
        private static WestSegment Seg => (WestSegment)BrSegments.All[3];
        private static BotNav _nav;
        private static Vec3d _pad;

        private static void BuildNav()
        {
            if (_nav != null) return;
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            w.Tiles.Changes.Clear();
            SubGrid g = BrMap.BuildGround();
            Rect2 r = BrLayout.Bounds(3);
            _nav = BotNav.Build(BrMap.Terrain(g, w), r.X0, r.Z0, r.X1 - r.X0, r.Z1 - r.Z0, 1.0, topY: BrMap.NavTop,
                                floorY: BrGround.Lowest - 2, columnTop: BrMap.NavColumnTop(w, g));
            _pad = br.Regions.Find(x => x.Segment == 3).Pads[0];
        }

        [Test]
        public void SealedRooms_HaveNoWayIn_AndHoldAChest()
        {
            var t = BrMap.Template; // builds the map (and fills the segment's records)
            Assert.That(Seg.Sealed.Count, Is.GreaterThanOrEqualTo(30), "plenty of breach loot");
            foreach (WestKit.SealedRoom room in Seg.Sealed)
            {
                GreyboxBlock.Building bd = room.Building;
                int s = room.Storey;
                var cells = new HashSet<(int, int)>(room.Cells);
                foreach (var (i, j) in room.Cells)
                {
                    void Side(WallKind k, string where) => Assert.That(k, Is.EqualTo(WallKind.Wall), $"{room.Name}: the {where} side of cell ({i}, {j}) is {k}");
                    if (!cells.Contains((i, j - 1))) Side(bd.AlongX[s, j, i], "south");
                    if (!cells.Contains((i, j + 1))) Side(bd.AlongX[s, j + 1, i], "north");
                    if (!cells.Contains((i - 1, j))) Side(bd.AlongZ[s, i, j], "west");
                    if (!cells.Contains((i + 1, j))) Side(bd.AlongZ[s, i + 1, j], "east");
                    Assert.That(bd.Hole[s, i, j], Is.False, $"{room.Name}: a hole over cell ({i}, {j})");
                    if (s > 0) Assert.That(bd.Hole[s - 1, i, j], Is.False, $"{room.Name}: a hole under cell ({i}, {j})");
                }
                double y = bd.Y0 + s * GreyboxBlock.StoreyHeight;
                bool loot = t.Segments[3].Chests.Exists(c => Math.Abs(c.Position.Y - y) < 0.01 && InCells(bd, cells, c.Position));
                Assert.That(loot, Is.True, $"{room.Name} holds a chest");
            }
        }

        private static bool InCells(GreyboxBlock.Building bd, HashSet<(int, int)> cells, Vec3d p)
        {
            foreach (var (i, j) in cells)
                if (p.X > bd.Cx(i) + GreyboxBlock.Column && p.X < bd.Cx(i + 1) && p.Z > bd.Cz(j) + GreyboxBlock.Column && p.Z < bd.Cz(j + 1)) return true;
            return false;
        }

        [Test]
        public void EveryPoi_TowerRoof_AndBridge_CanBeWalkedToFromSpawn_ButNoSealedRoom()
        {
            BuildNav();
            int start = _nav.Nearest(_pad);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "the pad is on the walking grid");
            var goals = new List<(string, Vec3d)>(Seg.Goals);
            foreach (WestKit.Lookout l in Seg.Lookouts) goals.Add((l.Name, l.At));
            foreach (WestKit.Span sp in Seg.Spans) goals.Add(($"bridge at {sp.Deck.Min}", new Vec3d((sp.Deck.Min.X + sp.Deck.Max.X) / 2, sp.Deck.Max.Y, (sp.Deck.Min.Z + sp.Deck.Max.Z) / 2)));
            Assert.That(Seg.Lookouts.Count, Is.GreaterThanOrEqualTo(9));
            Assert.That(Seg.Spans.Count, Is.EqualTo(7));
            var bad = new List<string>();
            foreach (var (name, at) in goals)
            {
                int n = _nav.Nearest(at, 2);
                if (n < 0 || Math.Abs(_nav.Feet(n).Y - at.Y) > 0.6) bad.Add($"{name} at {at}: no walking spot there");
                else if (!_nav.SameArea(start, n)) bad.Add($"{name} at {at}: can't be reached from the spawn");
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
            foreach (WestKit.SealedRoom room in Seg.Sealed)
            {
                GreyboxBlock.Building bd = room.Building;
                var (i, j) = room.Cells[0];
                var inside = new Vec3d(bd.Cx(i) + GreyboxBlock.Column + 1.2, bd.Y0 + room.Storey * GreyboxBlock.StoreyHeight, bd.Cz(j) + GreyboxBlock.Column + 1.2);
                int n = _nav.Nearest(inside, 0);
                Assert.That(n < 0 || !_nav.SameArea(start, n) || Math.Abs(_nav.Feet(n).Y - inside.Y) > 0.6, Is.True, $"{room.Name} can be walked into");
            }
        }

        [Test]
        public void ItsItemsAndChests_LieInTheClear()
        {
            BrushWorld w = BrMap.BuildBrushes(out _, out _);
            ITerrain t = BrMap.Terrain(BrMap.BuildGround(), w);
            BrBuild b = BrMap.Template.Segments[3];
            var bad = new List<string>();
            foreach (GroundItem g in b.Items)
                if (t.BoxOverlapsSolid(new Aabb(g.Position + new Vec3d(-0.2, 0.01, -0.2), g.Position + new Vec3d(0.2, 0.3, 0.2)))) bad.Add($"{g.Kind} at {g.Position}");
            foreach (BrChestSpot c in b.Chests)
            {
                Vec3d p = ItemRules.Settle(t, c.Position + new Vec3d(0, 0.5, 0));
                if (Math.Abs(p.Y - c.Position.Y) > 0.01 || t.BoxOverlapsSolid(new Aabb(p + new Vec3d(-0.4, 0.02, -0.4), p + new Vec3d(0.4, 0.6, 0.4)))) bad.Add($"chest at {c.Position} (settles at {p})");
            }
            Assert.That(bad, Is.Empty, string.Join("; ", bad));
        }

        [Test]
        public void ThePit_IsDugAsDesigned()
        {
            double floor = BrGround.Base - WestSegment.PitDepth;
            Rect2 p = WestSegment.Pit;
            Assert.That(BrGround.Natural(p.CX - 10, p.CZ), Is.EqualTo(floor).Within(1e-9), "the floor");
            Assert.That(BrGround.Natural(p.X0 - 2, p.CZ), Is.EqualTo(BrGround.Base).Within(1e-9), "the rim is level");
            // The ramp: 15 degrees from the north rim down to the floor.
            double x = (WestSegment.RampX0 + p.X1) / 2 - 1, slope = Math.Tan(WestSegment.RampSlope * Math.PI / 180);
            for (double z = p.Z1 - 1; z > p.Z1 - WestSegment.PitDepth / slope + 1; z -= 1)
                Assert.That(BrGround.Natural(x, z) - BrGround.Natural(x, z - 1), Is.EqualTo(slope).Within(1e-6), $"the ramp at z {z}");
            Assert.That(BrGround.Natural(x, p.Z1 - WestSegment.PitDepth / slope - 2), Is.EqualTo(floor).Within(1e-9), "the ramp reaches the floor");
        }
    }
}
