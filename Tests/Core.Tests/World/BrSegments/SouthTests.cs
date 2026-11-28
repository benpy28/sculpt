using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The South segment's own promises (the market town, <see cref="SouthSegment"/>), on top of the contract every
    /// segment keeps (BrSegmentTests, BrGroundTests, BrMapTests, BrNatureTests).
    /// </summary>
    public class SouthTests
    {
        private const int Id = 1;
        private static BrMap.TemplateBuild T => BrMap.Template;
        private static BrBuild B => T.Segments[Id];

        [Test]
        public void SouthCost_Report()
        {
            _ = T;
            SouthKit k = SouthKit.Last;
            Assert.That(k, Is.Not.Null);
            int tiles = B.TileEnd - B.TileFirst;
            TestContext.WriteLine($"South: {tiles} tiles, {B.Buildings.Count} buildings, {B.Chests.Count} chests, {B.Items.Count} items, {B.Props.Count} props asked");
            foreach (PropRequest q in BrNature.GetPlan().Dropped)
                if (q.Segment == Id) TestContext.WriteLine($"  dropped {q.Shape} at {q.X:0.0}, {q.Z:0.0}");
            foreach (var g in k.Cost.GroupBy(c => c.Name).OrderByDescending(g => g.Sum(c => c.Tiles)))
                TestContext.WriteLine($"  {g.Key}: {g.Count()} x, {g.Sum(c => c.Tiles)} tiles");
        }

        // ---------------------------------------------------------------- walking: what you can and can't walk to

        private static BotNav _nav;
        private static readonly object NavLock = new object();

        /// <summary>The walking grid over the segment (doors open, as bots use it; built from the intact map, no holes).</summary>
        private static BotNav Nav
        {
            get
            {
                lock (NavLock)
                {
                    if (_nav != null) return _nav;
                    BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out _);
                    foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
                    SubGrid g = BrMap.BuildGround();
                    Rect2 sq = BrLayout.Bounds(Id);
                    _nav = BotNav.Build(BrMap.Terrain(g, w), sq.X0, sq.Z0, sq.X1 - sq.X0, sq.Z1 - sq.Z0, 0.75, topY: BrMap.NavTop,
                                        floorY: BrGround.Lowest - 2, columnTop: BrMap.NavColumnTop(w, g));
                    return _nav;
                }
            }
        }

        private static int Spot(Vec3d feet, string what)
        {
            BotNav n = Nav;
            int s = n.Nearest(feet, 2);
            Assert.That(s, Is.GreaterThanOrEqualTo(0), $"{what}: no standing spot near {feet}");
            Assert.That(Math.Abs(n.Nodes[s].Y - feet.Y), Is.LessThan(0.6), $"{what}: the nearest standing spot to {feet} is at {n.Nodes[s].Feet(n)}");
            return s;
        }

        private static readonly string[] SealedWords = { "sealed", "vault", "strongroom", "grain store", "clerk's office", "scale room", "sack store", " shed" };

        [Test]
        public void SouthWalk_EveryRoofTowerAndOpenChest_IsReachableFromTheSquare_EverySealedChestIsNot()
        {
            _ = T;
            BotNav n = Nav;
            SouthKit k = SouthKit.Last;
            int square = Spot(BrLayout.Anchor(Id) + new Vec3d(0, 0, -12), "the square");
            // The roofs (and the top of every stair) you are promised: towers, the market hall, the exchange, the inn.
            var roofs = new (string Name, Vec3d At)[]
            {
                ("clock tower roof", SouthKit.CellMid(new GreyboxBlock.Building(SouthPlan.TowerX, SouthPlan.TowerZ, 3, 3, SouthPlan.TowerStoreys) { Y0 = SouthPlan.Core }, 1, 1, SouthPlan.TowerStoreys)),
                ("clock tower belfry", SouthKit.CellMid(new GreyboxBlock.Building(SouthPlan.TowerX, SouthPlan.TowerZ, 3, 3, SouthPlan.TowerStoreys) { Y0 = SouthPlan.Core }, 0, 1, SouthPlan.TowerStoreys - 1)),
                ("market hall roof", new Vec3d(652.8, SouthPlan.Core + 2 * SouthKit.SH, SouthPlan.HallZ + 9.6)),
            };
            var bad = new List<string>();
            foreach (var r in roofs)
                if (!n.SameArea(square, Spot(r.At, r.Name))) bad.Add($"can't walk to the {r.Name} at {r.At}");
            int open = 0, shut = 0;
            foreach (var (at, what) in k.Placed)
            {
                bool sealedRoom = SealedWords.Any(w => what.Contains(w)) || what.EndsWith(" X") || (what.EndsWith(" H") && at.Y > BrMap.Field.HeightAt(at.X, at.Z) + 2);
                if (what.EndsWith(" garden")) continue; // behind chest-high garden walls: you climb over (the walking grid doesn't)
                int s = Spot(at, what);
                bool reach = n.SameArea(square, s);
                if (sealedRoom && reach) bad.Add($"the sealed '{what}' chest at {at} can be walked to");
                if (!sealedRoom && !reach) bad.Add($"the '{what}' chest at {at} can't be walked to");
                if (sealedRoom) shut++; else open++;
            }
            TestContext.WriteLine($"{open} chests you walk to, {shut} you breach for");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
            Assert.That(shut, Is.GreaterThanOrEqualTo(40), "plenty of loot behind walls");
        }

        [Test, Explicit]
        public void SouthWalk_Debug()
        {
            _ = T;
            BotNav n = Nav;
            int square = n.Nearest(BrLayout.Anchor(Id) + new Vec3d(0, 0, -12), 2);
            foreach (string s in (Environment.GetEnvironmentVariable("SOUTH_PTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = s.Split(',').Select(double.Parse).ToArray();
                int q = n.Nearest(new Vec3d(v[0], v[1], v[2]), 2);
                TestContext.WriteLine($"{s}: node {(q >= 0 ? n.Nodes[q].Feet(n).ToString() : "none")} same area {n.SameArea(square, q)} area {n.AreaOf(q)}");
            }
            BrushWorld w = T.World;
            var near = new List<int>();
            foreach (string s in (Environment.GetEnvironmentVariable("SOUTH_BOX") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = s.Split(',').Select(double.Parse).ToArray();
                var box = new Aabb(new Vec3d(v[0] - 0.35, v[1] + 0.05, v[2] - 0.35), new Vec3d(v[0] + 0.35, v[1] + 1.8, v[2] + 0.35));
                w.BrushesNear(box, near);
                foreach (int o in near)
                    if (w[o].Solid && BrushWorld.Overlaps(w[o], box)) TestContext.WriteLine($"box {s}: {w[o].Kind} {w[o].Bounds.Min}..{w[o].Bounds.Max}");
            }
        }

        [Test]
        public void SouthPads_SeeNoOtherRegion_TheTownBlocksEveryLine()
        {
            var c = new MovementConfig();
            var t = BrMap.Terrain(BrMap.BuildGround(), T.World);
            BrRegion mine = T.Br.Regions.Find(r => r.Segment == Id);
            Rect2 sq = BrLayout.Bounds(Id);
            var bad = new List<string>();
            foreach (Vec3d p in mine.Pads)
            {
                Vec3d eye = p + new Vec3d(0, c.StandEye, 0);
                foreach (BrRegion other in T.Br.Regions)
                {
                    if (other.Segment == Id) continue;
                    foreach (Vec3d q in other.Pads)
                        foreach (double up in new[] { c.StandHeight - 0.15, c.StandHeight * 0.6, 0.35 })
                        {
                            Vec3d d = q + new Vec3d(0, up, 0) - eye;
                            double len = d.Length;
                            TerrainHit hit = t.Raycast(eye, d / len, len);
                            Vec3d at = eye + d / len * hit.Distance;
                            if (!hit.Hit || !sq.Contains(at.X, at.Z)) bad.Add($"pad {p} to {other.Name} pad {q}: {(hit.Hit ? $"first stopped at {at}, outside the South" : "clear")}");
                        }
                }
            }
            Assert.That(bad, Is.Empty, string.Join("\n", bad.Take(10)));
        }

        /// <summary>Dev tool: SCULPT_PLAN_DIR=dir writes south_plan.png, a floor plan of the segment (0.25 m a pixel).</summary>
        [Test]
        public void SouthPlan_Picture()
        {
            string dir = Environment.GetEnvironmentVariable("SCULPT_PLAN_DIR");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("set SCULPT_PLAN_DIR to write the plan");
            Directory.CreateDirectory(dir);
            HeightField f = BrMap.Field;
            BrushWorld w = T.World;
            Rect2 sq = BrLayout.Bounds(Id);
            const double px = 0.25;
            int n = (int)Math.Ceiling(BrLayout.SegSize / px);
            var img = new byte[n * n * 3];
            void Put(int i, int j, (double r, double g, double b) c)
            {
                if (i < 0 || j < 0 || i >= n || j >= n) return;
                int o = ((n - 1 - j) * n + i) * 3;
                img[o] = (byte)Math.Clamp(c.r, 0, 255); img[o + 1] = (byte)Math.Clamp(c.g, 0, 255); img[o + 2] = (byte)Math.Clamp(c.b, 0, 255);
            }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = sq.X0 + (i + 0.5) * px, z = sq.Z0 + (j + 0.5) * px;
                    if (!f.Covers(x, z)) { Put(i, j, (20, 20, 24)); continue; }
                    double h = f.HeightAt(x, z);
                    Vec3d nn = f.NormalAt(x, z);
                    double t = Math.Clamp((h + 2) / 16, 0, 1);
                    double light = Math.Clamp(0.6 + 0.5 * (-nn.X * 0.6 + nn.Y * 0.5 + nn.Z * 0.6), 0.4, 1.2);
                    (double, double, double) c = (110 + 110 * t, 150 + 50 * t, 90 - 30 * t);
                    if (nn.Y < f.WalkableNormalY) c = (120, 120, 120);
                    if (Math.Abs(x % 50) < px || Math.Abs(z % 50) < px) light *= 0.85;
                    if (!B.Area.Contains(x, z)) light *= 0.7;
                    Put(i, j, (c.Item1 * light, c.Item2 * light, c.Item3 * light));
                }
            // Building footprints, darker the taller.
            foreach (BrBuilding bd in B.Buildings)
            {
                double storeys = (bd.Bounds.Max.Y - bd.Bounds.Min.Y) / GreyboxBlock.StoreyHeight;
                double k = Math.Clamp(1 - storeys / 12, 0.15, 0.9);
                for (double z = bd.Bounds.Min.Z; z < bd.Bounds.Max.Z; z += px)
                    for (double x = bd.Bounds.Min.X; x < bd.Bounds.Max.X; x += px)
                        Put((int)((x - sq.X0) / px), (int)((z - sq.Z0) / px), (230 * k + 20, 215 * k + 20, 200 * k + 20));
            }
            foreach (Rect2 r in B.Reserved)
                for (double z = Math.Max(r.Z0, sq.Z0); z < Math.Min(r.Z1, sq.Z1); z += px)
                    for (double x = Math.Max(r.X0, sq.X0); x < Math.Min(r.X1, sq.X1); x += px)
                    {
                        int i = (int)((x - sq.X0) / px), j = (int)((z - sq.Z0) / px);
                        if ((i + j) % 6 == 0) Put(i, j, (90, 170, 255));
                    }
            foreach (var l in B.Lanes)
                for (double t = 0; t <= 1; t += 0.0005)
                    Put((int)((l.From.X + (l.To.X - l.From.X) * t - sq.X0) / px), (int)((l.From.Z + (l.To.Z - l.From.Z) * t - sq.Z0) / px), (90, 170, 255));
            // Ground-floor plan: the bottom row of tiles standing on the ground (walls, doors, stairs).
            for (int id = 0; id < w.Tiles.Count; id++)
            {
                Aabb a = w.Tiles.BoundsOf(id);
                if (BrLayout.Owner((a.Min.X + a.Max.X) / 2, (a.Min.Z + a.Max.Z) / 2) != Id) continue;
                double g = f.HeightAt((a.Min.X + a.Max.X) / 2, (a.Min.Z + a.Max.Z) / 2);
                TileKind kind = w.Tiles[id].Kind;
                bool low = a.Min.Y < g + 0.5;
                (double, double, double) c;
                if (kind == TileKind.Stair || kind == TileKind.Ramp) c = low ? (240, 200, 60) : (200, 170, 70);
                else if (kind == TileKind.Door) c = (150, 90, 30);
                else if (kind == TileKind.Floor) continue;
                else if (!low) continue;
                else c = kind == TileKind.Frame ? (40, 40, 60) : (35, 30, 30);
                for (double z = a.Min.Z; z < a.Max.Z; z += px * 0.5)
                    for (double x = a.Min.X; x < a.Max.X; x += px * 0.5)
                        Put((int)((x - sq.X0) / px), (int)((z - sq.Z0) / px), c);
            }
            foreach (BrChestSpot ch in T.Br.Chests)
            {
                if (BrLayout.Owner(ch.Position.X, ch.Position.Z) != Id) continue;
                double g = f.HeightAt(ch.Position.X, ch.Position.Z);
                var c = ch.Position.Y > g + 1 ? (255.0, 120.0, 220.0) : (255.0, 220.0, 30.0);
                int ci = (int)((ch.Position.X - sq.X0) / px), cj = (int)((ch.Position.Z - sq.Z0) / px);
                for (int dj = -3; dj <= 3; dj++) for (int di = -3; di <= 3; di++) Put(ci + di, cj + dj, c);
            }
            foreach (BrRegion r in T.Br.Regions)
                foreach (Vec3d p in r.Pads)
                {
                    int ci = (int)((p.X - sq.X0) / px), cj = (int)((p.Z - sq.Z0) / px);
                    for (int dj = -4; dj <= 4; dj++) for (int di = -4; di <= 4; di++) Put(ci + di, cj + dj, (40, 110, 255));
                }
            {
                Vec3d a = BrLayout.Anchor(Id);
                for (double t = 0; t < Math.PI * 2; t += 0.002) Put((int)((a.X + Math.Cos(t) * BrLayout.HomeRadius - sq.X0) / px), (int)((a.Z + Math.Sin(t) * BrLayout.HomeRadius - sq.Z0) / px), (255, 100, 100));
            }
            var m = typeof(MapPlan).GetMethod("Png", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            File.WriteAllBytes(Path.Combine(dir, "south_plan.png"), (byte[])m.Invoke(null, new object[] { img, n, n }));
        }
    }
}
