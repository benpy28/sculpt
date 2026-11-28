using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The North segment, the Train Yard: what its design promises. Loot behind walls stays sealed (the station's
    /// ticket office and strongroom, the goods shed's storerooms, the freight sheds' offices), everything else is
    /// reachable on foot with the doors open (rooftops of every tower included), the road runs under the embankment
    /// to the station, the embankment hides the yard from the south, the cutting and its footbridge are walkable,
    /// and nothing in the yard gives a clear sight line down its whole length.
    /// Reachability uses the bots' walking grid over the segment's square (walk, jump one tile, drop a storey).
    /// </summary>
    public class NorthTests
    {
        private const int Id = 7;
        private static BrushWorld _w;
        private static BreachMapInfo _info;
        private static ITerrain _t;
        private static BotNav _nav;
        private static BrBuild B => BrMap.Template.Segments[Id];

        [OneTimeSetUp]
        public void Build()
        {
            if (_nav != null) return;
            _w = BrMap.BuildBrushes(out _info, out _);
            SubGrid g = BrMap.BuildGround();
            _t = BrMap.Terrain(g, _w);
            // The walking grid over the segment (doors open, as the bots see the map), on a copy of the map.
            BrushWorld nw = BrMap.BuildBrushes(out BreachMapInfo ninfo, out _);
            foreach (int d in ninfo.Block.Doors) nw.Tiles.Set(d, TileState.DoorOpen, nw.Tiles[d].Health, TileCause.Built);
            Rect2 sq = BrLayout.Bounds(Id);
            _nav = BotNav.Build(BrMap.Terrain(g, nw), sq.X0, sq.Z0, sq.X1 - sq.X0, sq.Z1 - sq.Z0, 0.75, topY: BrMap.NavTop,
                                floorY: BrGround.Lowest - 2, columnTop: BrMap.NavColumnTop(nw, g));
        }

        private static int Node(Vec3d feet)
        {
            int n = _nav.Nearest(feet, 4);
            if (n < 0 || Math.Abs(_nav.Feet(n).Y - feet.Y) > 0.6) return -1;
            return n;
        }

        /// <summary>The station plaza, north of the station: the start of every reachability check.</summary>
        private static int Plaza => Node(new Vec3d(B.Anchor.X, BrGround.Base, B.Anchor.Z + 20));

        private static bool Reachable(Vec3d chest)
        {
            // A chest blocks its own spot: stand next to it (any side).
            foreach (var o in new[] { new Vec3d(1.2, 0, 0), new Vec3d(-1.2, 0, 0), new Vec3d(0, 0, 1.2), new Vec3d(0, 0, -1.2) })
            {
                int n = Node(chest + o);
                if (n >= 0 && _nav.SameArea(n, Plaza)) return true;
            }
            return false;
        }

        private static List<BrChestSpot> ChestsIn(string building)
        {
            var list = new List<BrChestSpot>();
            foreach (BrBuilding bd in B.Buildings)
            {
                if (bd.Name != building) continue;
                foreach (BrChestSpot c in B.Chests)
                {
                    Vec3d p = c.Position;
                    if (p.X > bd.Bounds.Min.X && p.X < bd.Bounds.Max.X && p.Z > bd.Bounds.Min.Z && p.Z < bd.Bounds.Max.Z && p.Y >= bd.Bounds.Min.Y - 0.01 && p.Y <= bd.Bounds.Max.Y + 0.01)
                        list.Add(c);
                }
            }
            return list;
        }

        private static (int open, int sealedCount) Count(string building)
        {
            int o = 0, s = 0;
            foreach (BrChestSpot c in ChestsIn(building)) { if (Reachable(c.Position)) o++; else s++; }
            return (o, s);
        }

        [Test]
        public void TheAnchorAndPlazaAreWhereTheContractSays()
        {
            Assert.That(B.Anchor.X, Is.EqualTo(652.8).Within(1e-6));
            Assert.That(B.Anchor.Z, Is.EqualTo(1024.8).Within(1e-6));
            Assert.That(Plaza, Is.GreaterThanOrEqualTo(0), "the plaza is on the walking grid");
            Assert.That(B.Pois.Exists(p => p.Name == "Junction Station"), Is.True);
        }

        [Test]
        public void JunctionStation_SealsItsTicketOfficeAndStrongroom_AndReachesTheRest()
        {
            var (open, sealedCount) = Count("Junction Station");
            TestContext.WriteLine($"station: {open} reachable, {sealedCount} sealed");
            Assert.That(sealedCount, Is.EqualTo(4), "ticket office (2) and strongroom (2): break in");
            Assert.That(open, Is.EqualTo(4), "concourse, parcels office, upstairs office, roof (by the stairs)");
        }

        [Test]
        public void TheGoodsShed_HasFiveSealedStorerooms_AndTheFreightShedsASealedOfficeEach()
        {
            var (open, sealedCount) = Count("Goods shed");
            Assert.That(sealedCount, Is.EqualTo(5), "goods shed storerooms");
            Assert.That(open, Is.EqualTo(3));
            var (fo, fs) = Count("Freight shed");
            Assert.That(fs, Is.EqualTo(5), "one sealed office per freight shed");
            Assert.That(fo, Is.EqualTo(5));
        }

        [Test]
        public void EveryTowerRoof_AndEveryFloorWithLoot_IsReachableByItsStairs()
        {
            foreach (string name in new[] { "Signal tower", "Railway hotel", "Water tower", "Signal box", "Engine shed" })
            {
                var chests = ChestsIn(name);
                Assert.That(chests.Count, Is.GreaterThanOrEqualTo(2), name);
                double roof = double.NegativeInfinity;
                foreach (BrChestSpot c in chests) roof = Math.Max(roof, c.Position.Y);
                foreach (BrChestSpot c in chests)
                {
                    // The engine shed's wagons are sealed boxcars: skip loot standing on the hall floor at track level.
                    if (name == "Engine shed" && c.Position.Y < 2 && c.Position.Z < 976) continue;
                    Assert.That(Reachable(c.Position), Is.True, $"{name}: the chest at {c.Position} (roof {roof:0.0} m)");
                }
            }
        }

        [Test]
        public void TheSegment_RewardsBreaching_ButMostLootIsWalkable()
        {
            int open = 0, sealedCount = 0;
            foreach (BrChestSpot c in B.Chests) { if (Reachable(c.Position)) open++; else sealedCount++; }
            TestContext.WriteLine($"{B.Chests.Count} chests: {open} reachable on foot, {sealedCount} behind walls");
            Assert.That(sealedCount, Is.GreaterThanOrEqualTo(B.Chests.Count / 4), "plenty of loot behind walls");
            Assert.That(open, Is.GreaterThanOrEqualTo(B.Chests.Count / 3), "and plenty you can walk to");
        }

        [Test]
        public void TheRoad_RunsUnderTheEmbankment_ToTheStation_AndTheFootbridgesAreWalkable()
        {
            int south = _nav.Nearest(new Vec3d(NorthSegment.RoadX, BrGround.Base, 900), 4);
            Assert.That(south, Is.GreaterThanOrEqualTo(0));
            Assert.That(_nav.SameArea(south, Plaza), Is.True, "south road to the station");
            // Under the bridge: a standing player fits along the road's middle.
            var c = new MovementConfig();
            for (double z = NorthSegment.EmbZ - 20; z <= NorthSegment.EmbZ + 20; z += 0.5)
            {
                double y = BrMap.Field.HeightAt(NorthSegment.RoadX, z);
                var box = new Aabb(new Vec3d(NorthSegment.RoadX - c.HalfWidth, y + 0.05, z - c.HalfWidth), new Vec3d(NorthSegment.RoadX + c.HalfWidth, y + c.StandHeight, z + c.HalfWidth));
                Assert.That(_t.BoxOverlapsSolid(box), Is.False, $"the underpass is blocked at z {z}");
            }
            double emb = BrGround.Base + NorthSegment.EmbH;
            int deck = Node(new Vec3d((NorthBuild.FootX0 + NorthBuild.FootX1) / 2, emb, 960));
            Assert.That(deck, Is.GreaterThanOrEqualTo(0), "the yard footbridge's deck");
            Assert.That(_nav.SameArea(deck, Plaza), Is.True, "the yard footbridge from the platform");
            int cut = Node(new Vec3d(800, BrGround.Base + NorthSegment.RidgeH, 976));
            Assert.That(cut, Is.GreaterThanOrEqualTo(0), "the footbridge over the cutting");
            Assert.That(_nav.SameArea(cut, Plaza), Is.True, "the cutting's footbridge from the ridge");
            int floor = Node(new Vec3d(835, BrGround.Base, 976));
            Assert.That(floor >= 0 && _nav.SameArea(floor, Plaza), Is.True, "the cutting's floor");
        }

        [Test]
        public void TheEmbankment_HidesTheYardFromTheSouth()
        {
            // Eye height on the open ground south of the embankment, looking north into the yard: the bank is in the way
            // everywhere except through the road's underpass.
            var c = new MovementConfig();
            int open = 0, lines = 0;
            for (double x = NorthSegment.EmbX0 + 20; x <= 760; x += 4)
            {
                if (Math.Abs(x - NorthSegment.RoadX) < NorthSegment.RoadHalf + 2) continue;
                foreach (double dx in new[] { -30.0, 0, 30.0 })
                {
                    double y0 = BrMap.Field.HeightAt(x, 892) + c.StandEye, y1 = BrGround.Base + 1.0;
                    var from = new Vec3d(x, y0, 892);
                    var to = new Vec3d(x + dx, y1, 975);
                    // Lines through the road's underpass are the road (the bridge is the way in): skip them.
                    double xAtBank = from.X + (to.X - from.X) * (NorthSegment.EmbZ - from.Z) / (to.Z - from.Z);
                    if (Math.Abs(xAtBank - NorthSegment.RoadX) < NorthSegment.RoadHalf + 1) continue;
                    Vec3d d = to - from;
                    TerrainHit h = _t.Raycast(from, d * (1 / d.Length), d.Length);
                    lines++;
                    if (!h.Hit) open++;
                }
            }
            TestContext.WriteLine($"{open} of {lines} lines from the south reach a crouched player in the yard");
            Assert.That(open, Is.EqualTo(0));
        }

        [Test]
        public void NoAlleyInTheYard_IsAClearSightLineEndToEnd()
        {
            // Along every alley between the tracks at eye height, from the west throat: something stops the line within
            // 200 m (the engine shed, the footbridge piers, the platforms' ends, the ridge).
            var c = new MovementConfig();
            double[] alleys = { 958, 964, 970, 976, 982, 988, 994, 1000 };
            foreach (double zc in alleys)
                foreach (double z in new[] { zc - 0.8, zc + 0.8 })
                    foreach (int dir in new[] { 1, -1 })
                    {
                        var from = new Vec3d(dir > 0 ? 480 : 760, BrGround.Base + c.StandEye, z);
                        TerrainHit h = _t.Raycast(from, new Vec3d(dir, 0, 0), 300);
                        double len = h.Hit ? (h.Point - from).Length : 300;
                        TestContext.WriteLine($"alley z {z}, {(dir > 0 ? "east" : "west")}: clear for {len:0} m");
                        Assert.That(len, Is.LessThan(200), $"alley z {z}");
                    }
        }

        /// <summary>A detailed picture of the segment (SCULPT_PLAN_DIR set): every tile top-down by kind and height, chests, ground.</summary>
        [Test]
        public void WriteDetailedPlan()
        {
            string dir = Environment.GetEnvironmentVariable("SCULPT_PLAN_DIR");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("set SCULPT_PLAN_DIR to write the plan");
            Directory.CreateDirectory(dir);
            const double px = 0.5;
            Rect2 sq = BrLayout.Bounds(Id);
            int n = (int)Math.Ceiling((sq.X1 - sq.X0) / px);
            var img = new byte[n * n * 3];
            var top = new double[n * n];
            HeightField f = BrMap.Field;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = sq.X0 + (i + 0.5) * px, z = sq.Z0 + (j + 0.5) * px;
                    double h = f.HeightAt(x, z);
                    Vec3d nn = f.NormalAt(x, z);
                    double t = Math.Clamp((h + 2) / 22, 0, 1);
                    double r = 90 + 110 * t, g = 140 + 50 * t, bl = 80 + 30 * t;
                    double light = Math.Clamp(0.6 + 0.6 * (-nn.X * 0.6 + nn.Y * 0.5 + nn.Z * 0.6), 0.35, 1.25);
                    if (nn.Y < f.WalkableNormalY) r = g = bl = 125;
                    Put(img, n, i, j, (r * light, g * light, bl * light));
                    top[j * n + i] = double.NegativeInfinity;
                }
            for (int k = B.TileFirst; k < B.TileEnd; k++)
            {
                Aabb a = _w.Tiles.BoundsOf(k);
                TileKind kind = _w.Tiles[k].Kind;
                double ht = Math.Clamp((a.Max.Y - BrGround.Base) / 30, 0, 1);
                (double, double, double) col = kind switch
                {
                    TileKind.Floor => (150 + 80 * ht, 140 + 80 * ht, 130 + 70 * ht),
                    TileKind.Stair or TileKind.Ramp => (230, 200, 60),
                    TileKind.Door => (120, 70, 30),
                    TileKind.Frame => (40, 50, 80),
                    _ => (150 - 60 * ht + 40, 50, 40),
                };
                for (double z = a.Min.Z; z < a.Max.Z; z += px * 0.5)
                    for (double x = a.Min.X; x < a.Max.X; x += px * 0.5)
                    {
                        int i = (int)((x - sq.X0) / px), j = (int)((z - sq.Z0) / px);
                        if (i < 0 || j < 0 || i >= n || j >= n || top[j * n + i] > a.Max.Y) continue;
                        top[j * n + i] = a.Max.Y;
                        Put(img, n, i, j, col);
                    }
            }
            foreach (BrChestSpot ch in B.Chests)
            {
                bool reach = Reachable(ch.Position);
                int ci = (int)((ch.Position.X - sq.X0) / px), cj = (int)((ch.Position.Z - sq.Z0) / px);
                for (int dj = -2; dj <= 2; dj++)
                    for (int di = -2; di <= 2; di++) Put(img, n, ci + di, cj + dj, reach ? (250, 220, 40) : (60, 230, 255));
            }
            var m = typeof(MapPlan).GetMethod("Png", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            File.WriteAllBytes(Path.Combine(dir, "br_North_detail.png"), (byte[])m.Invoke(null, new object[] { img, n, n }));
        }

        private static void Put(byte[] img, int n, int x, int y, (double r, double g, double b) c)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            int i = ((n - 1 - y) * n + x) * 3;
            img[i] = (byte)Math.Clamp(c.r, 0, 255); img[i + 1] = (byte)Math.Clamp(c.g, 0, 255); img[i + 2] = (byte)Math.Clamp(c.b, 0, 255);
        }
    }
}
