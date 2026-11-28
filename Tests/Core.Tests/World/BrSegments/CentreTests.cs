using System;
using System.Linq;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The Old City (the Centre segment) keeps its promises: the citadel is a terrace you only get onto by its ramps (or
    /// by breaking in and climbing), every ramp is a smooth walkable run, every tower's roof (the Old Spire's included),
    /// every stair landing, every bridge, every quarter and the spire's plaza can be walked to from the main roads, the
    /// sealed rooms have no way in, and the Old Spire is the tallest building on the map.
    /// Run with --filter "FullyQualifiedName~CentreTests".
    /// </summary>
    public class CentreTests
    {
        private static double C => BrLayout.MapCentre;

        private CompositeTerrain _t;
        private BotNav _nav;
        private CentreSegment.Promises _p;
        private int _road;
        private System.Collections.Generic.List<Aabb> _buildings;

        [OneTimeSetUp]
        public void Build()
        {
            // One copy of the map with every door open (a door is a way in; sealed rooms have none), for walking and sight.
            BrushWorld open = BrMap.BuildBrushes(out BreachMapInfo info, out _);
            _buildings = info.Block.Buildings;
            _p = CentreSegment.Last;
            foreach (int d in info.Block.Doors) open.Tiles.Set(d, TileState.DoorOpen, open.Tiles[d].Health, TileCause.Built);
            open.Tiles.Changes.Clear();
            SubGrid ground = BrMap.BuildGround();
            _t = BrMap.Terrain(ground, open);
            Rect2 r = BrLayout.Bounds(BrLayout.Centre);
            _nav = BotNav.Build(_t, r.X0, r.Z0, r.X1 - r.X0, r.Z1 - r.Z0, 1.0, topY: BrMap.NavTop, floorY: BrGround.Lowest - 2);
            // On the east main road outside the city wall: where a player walks in from the East segment.
            _road = _nav.Nearest(new Vec3d(C + 190, CentreSegment.TownLevel, C), 3);
            Assert.That(_road, Is.GreaterThanOrEqualTo(0));
        }

        private bool Walkable(Vec3d at, int search = 2)
        {
            int n = _nav.Nearest(at, search);
            return n >= 0 && Math.Abs(_nav.Feet(n).Y - at.Y) < 0.6 && _nav.SameArea(_road, n);
        }

        [Test]
        public void Report()
        {
            BrBuild b = BrMap.Template.Segments[BrLayout.Centre];
            foreach (var kv in _p.Cost.OrderByDescending(k => k.Value.Tiles))
                TestContext.WriteLine($"{kv.Key,-20} {kv.Value.Tiles,7} tiles {kv.Value.Chests,4} chests");
            TestContext.WriteLine($"total {b.TileEnd - b.TileFirst} tiles, {b.Chests.Count} chests, {b.Items.Count} items, {b.Buildings.Count} buildings, " +
                                  $"{_p.Towers.Count} towers, {_p.Sealed.Count} sealed rooms, {_p.Bridges.Count} bridges");
            Assert.That(_p.Towers.Count, Is.EqualTo(9), "the spire and eight towers");
            Assert.That(_p.Bridges.Count, Is.EqualTo(7));
            Assert.That(_p.Sealed.Count, Is.GreaterThan(40));
        }

        [Test]
        public void EveryMainRoadGateAndBreach_LeadsIntoTheTown()
        {
            foreach (var (u, v, what) in new[] { (190.0, 0.0, "east road"), (-190.0, 0.0, "west road"), (0.0, 190.0, "north road"), (0.0, -190.0, "south road") })
                Assert.That(Walkable(new Vec3d(C + u, CentreSegment.TownLevel, C + v), 3), Is.True, what);
            for (int side = 0; side < 4; side++)
                foreach (double m in CentreSegment.WallOpenings[side])
                {
                    double inner = CentreSegment.WallHalf - 2;
                    double u = side == 2 ? -inner : side == 3 ? inner : m, v = side == 0 ? -inner : side == 1 ? inner : m;
                    Assert.That(Walkable(new Vec3d(C + u, CentreSegment.TownLevel, C + v)), Is.True, $"inside the opening at {m} on side {side}");
                }
        }

        [Test]
        public void EveryTowerRoof_EveryStairLanding_AndEveryBridge_CanBeWalkedTo()
        {
            for (int k = 0; k < _p.Towers.Count; k++)
            {
                GreyboxBlock.Building t = _p.Towers[k];
                string name = _p.TowerNames[k];
                for (int s = 0; s < t.Storeys; s++)
                {
                    var landing = new Vec3d(t.Cx(0) + 3.3, t.Y0 + s * GreyboxBlock.StoreyHeight, t.Cz(2) + 3.3);
                    Assert.That(Walkable(landing), Is.True, $"{name}'s stair landing on floor {s}");
                }
                var roof = new Vec3d(t.Cx(t.Mx - 1) + 3.3, t.Top, t.Cz(1) + 3.3);
                Assert.That(Walkable(roof), Is.True, $"{name}'s roof at {roof}");
            }
            foreach (Aabb br in _p.Bridges)
            {
                var mid = new Vec3d((br.Min.X + br.Max.X) / 2, br.Min.Y, (br.Min.Z + br.Max.Z) / 2);
                Assert.That(Walkable(mid, 1), Is.True, $"the bridge at {br.Min}");
            }
        }

        [Test]
        public void TheCitadel_TheSpirePlaza_AndEveryQuarter_CanBeWalkedTo()
        {
            Assert.That(Walkable(new Vec3d(C + 16, CentreSegment.CitadelLevel, C)), Is.True, "the spire's plaza");
            Assert.That(Walkable(new Vec3d(C + 31.5, CentreSegment.CitadelLevel, C + 31.5)), Is.True, "the citadel's streets");
            foreach (var (at, what) in _p.Places) Assert.That(Walkable(at), Is.True, what);
            Assert.That(Walkable(new Vec3d(C + 96.5, CentreSegment.StepLevel, C - 96.5)), Is.True, "the Merchant Steps");
        }

        [Test]
        public void EverySealedRoom_HasNoWayIn()
        {
            foreach (var (room, chest, what) in _p.Sealed)
            {
                // Walking: the room has floor, and it isn't joined to the roads (every door open).
                int n = _nav.Nearest(chest, 1);
                Assert.That(n >= 0 && Math.Abs(_nav.Feet(n).Y - chest.Y) < 0.6, Is.True, $"{what} at {chest} has a floor");
                Assert.That(_nav.SameArea(_road, n), Is.False, $"{what} at {chest} can be walked into");
                // Every line out of it (64 directions round, three heights, three pitches) hits something before it
                // leaves the room.
                Aabb grown = room.Expand(0.6);
                foreach (double h in new[] { 0.4, 1.6, 3.6 })
                    for (int k = 0; k < 64; k++)
                        foreach (double up in new[] { -0.3, 0.0, 0.5 })
                        {
                            double a = k * Math.PI * 2 / 64;
                            var dir = new Vec3d(Math.Cos(a), up, Math.Sin(a)).Normalized;
                            TerrainHit hit = _t.Raycast(new Vec3d(chest.X, chest.Y + h, chest.Z), dir, 40);
                            Assert.That(hit.Hit && grown.Contains(hit.Point, 0), Is.True, $"{what} at {chest}: a line {a * 180 / Math.PI:0} deg, {up:0.0} up from {h} m gets out");
                        }
            }
        }

        [Test]
        public void TheCitadelFaces_AreTooTallToClimb_ExceptAtTheRamps()
        {
            HeightField f = BrMap.Field;
            double e = CentreSegment.CitadelHalf, top = CentreSegment.CitadelLevel;
            int edges = 0;
            for (double t = -e; t <= e; t += 1.5)
                foreach (var (nx, nz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (CentreSegment.Ramps.Any(r => r.Nx == nx && r.Nz == nz && r.Top == top && Math.Abs(t - r.At) < r.Half + 6)) continue;
                    double eu = nx != 0 ? nx * e : -nz * t, ev = nz != 0 ? nz * e : nx * t;
                    Assert.That(f.HeightAt(C + eu - nx * 0.75, C + ev - nz * 0.75), Is.EqualTo(top).Within(1e-6), $"citadel top at {eu:0},{ev:0}");
                    Assert.That(f.HeightAt(C + eu + nx * 6, C + ev + nz * 6), Is.LessThan(top - 6), $"the face below {eu:0},{ev:0} is a drop you can't climb");
                    edges++;
                }
            Assert.That(edges, Is.GreaterThan(200));
        }

        [Test]
        public void EveryRamp_IsASmoothRunFromTheTownToTheTop()
        {
            HeightField f = BrMap.Field;
            foreach (CentreSegment.Ramp r in CentreSegment.Ramps)
            {
                double run = (r.Top - CentreSegment.TownLevel) / Math.Tan(r.Slope * Math.PI / 180);
                double side = Math.Max(0, r.Half - 1.6), prev = double.NaN;
                double tu = -r.Nz, tv = r.Nx;
                for (double a = -3; a <= run + 3; a += 0.5)
                {
                    var (u, v) = CentreSegment.RampPoint(r, a);
                    double h = f.HeightAt(C + u, C + v);
                    if (!double.IsNaN(prev))
                        Assert.That(Math.Abs(prev - h), Is.LessThanOrEqualTo(0.5 * Math.Tan(14 * Math.PI / 180) + 1e-6), $"{r.Name} {a:0.0} m out: no steeper than 14 degrees");
                    prev = h;
                    foreach (double off in new[] { -side, side })
                        Assert.That(Math.Abs(f.HeightAt(C + u + tu * off, C + v + tv * off) - h), Is.LessThan(0.15), $"{r.Name} {a:0.0} m out is level across ({off:0.0} m off its middle)");
                }
                var (bu, bv) = CentreSegment.RampPoint(r, run + 3);
                var (cu, cv) = CentreSegment.RampPoint(r, -3);
                Assert.That(f.HeightAt(C + cu, C + cv), Is.EqualTo(r.Top).Within(1e-6), $"the {r.Name} starts at the top");
                Assert.That(Walkable(new Vec3d(C + bu, f.HeightAt(C + bu, C + bv), C + bv)), Is.True, $"the foot of the {r.Name}");
                Assert.That(Walkable(new Vec3d(C + cu, r.Top, C + cv)), Is.True, $"the top of the {r.Name}");
            }
        }

        [Test]
        public void TheOldSpire_IsTheTallestBuildingOnTheMap_AndNoFootingIsInsideTheWall()
        {
            GreyboxBlock.Building spire = _p.Towers[_p.TowerNames.IndexOf("Old Spire")];
            double tall = spire.Top - spire.Y0;
            foreach (Aabb b in _buildings)
            {
                bool isSpire = Math.Abs(b.Min.X - spire.X0) < 1e-6 && Math.Abs(b.Min.Z - spire.Z0) < 1e-6;
                if (!isSpire) Assert.That(b.Max.Y - b.Min.Y, Is.LessThan(tall), $"the building at {b.Min} is as tall as the Old Spire");
            }
            Assert.That(spire.Storeys, Is.EqualTo(12));
            // A footing banks at 28 degrees: inside the wall it would cut a walkable slope into a rock face.
            Footings foot = BrMap.FootingsUsed;
            for (int k = 0; k < foot.Count; k++)
            {
                Footings.Footing ft = foot[k];
                if (BrLayout.Owner((ft.X0 + ft.X1) / 2, (ft.Z0 + ft.Z1) / 2) != BrLayout.Centre) continue;
                double nearest = Math.Max(Math.Min(Math.Abs(ft.X0 - C), Math.Abs(ft.X1 - C)), Math.Min(Math.Abs(ft.Z0 - C), Math.Abs(ft.Z1 - C)));
                Assert.That(nearest - ft.Apron, Is.GreaterThan(CentreSegment.WallHalf + 5), $"a footing at {ft.X0:0},{ft.Z0:0} reaches inside the city wall");
            }
        }
    }
}
