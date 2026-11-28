using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// The North West segment (Farmstead and Radio Mast Hill) keeps its promises: every prop it asks for fits, the
    /// sealed rooms are sealed and the open ones open, every roof it promises can be climbed to by stairs, the summit
    /// compound and the crag can be walked up to, the Holloway is a real sunken lane, and the silo deck is a step out
    /// of the elevator. Plus a detailed plan picture of the segment (SCULPT_PLAN_DIR).
    /// </summary>
    public class NorthWestTests
    {
        private const int Id = 6;
        private static BrMap.TemplateBuild T => BrMap.Template;
        private static BrBuild B => T.Segments[Id];

        [Test]
        public void EveryPropItAsksFor_Fits()
        {
            BrNature.Plan plan = BrNature.GetPlan();
            var dropped = new List<string>();
            foreach (PropRequest q in plan.Dropped)
                if (q.Segment == Id) dropped.Add($"{q.Shape} at {q.X:0.0},{q.Z:0.0}");
            int mine = 0;
            foreach (BrNature.Prop p in plan.Props) if (p.Segment == Id) mine++;
            TestContext.WriteLine($"{mine} props, {B.Props.Count} asked for");
            Assert.That(dropped, Is.Empty, string.Join("\n", dropped));
        }

        [Test]
        public void NoTwoOfItsBuildingsOverlap()
        {
            var list = new List<BrBuilding>(B.Buildings);
            var bad = new List<string>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (list[i].Bounds.Expand(-1e-6).Overlaps(list[j].Bounds.Expand(-1e-6))) bad.Add($"{list[i].Name} at {list[i].Bounds.Min} and {list[j].Name} at {list[j].Bounds.Min}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        private static ITerrain _terrain;
        private static ITerrain Terrain => _terrain ??= OpenDoors();

        /// <summary>The map with every door open (a shut door is a way in: press E).</summary>
        private static ITerrain OpenDoors()
        {
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out _);
            foreach (int d in info.Block.Doors) w.Tiles.Set(d, TileState.DoorOpen, w.Tiles[d].Health, TileCause.Built);
            return BrMap.Terrain(BrMap.BuildGround(), w);
        }

        private static BrBuilding Named(string name) => B.Buildings.Find(x => x.Name == name);

        /// <summary>
        /// Flood fill on one floor: a body (0.6 m square, <paramref name="lo"/>..<paramref name="hi"/> over the floor)
        /// moving in 0.35 m steps from <paramref name="from"/>; true if it gets out of <paramref name="box"/> (grown 0.5 m).
        /// </summary>
        private static bool Escapes(Vec3d from, Aabb box, double lo, double hi)
        {
            const double step = 0.35, half = 0.3;
            double x0 = box.Min.X - 3, z0 = box.Min.Z - 3;
            int nx = (int)Math.Ceiling((box.Max.X - box.Min.X + 6) / step), nz = (int)Math.Ceiling((box.Max.Z - box.Min.Z + 6) / step);
            var seen = new bool[nx, nz];
            var queue = new Queue<(int, int)>();
            int si = (int)((from.X - x0) / step), sj = (int)((from.Z - z0) / step);
            queue.Enqueue((si, sj));
            seen[si, sj] = true;
            while (queue.Count > 0)
            {
                var (i, j) = queue.Dequeue();
                double x = x0 + (i + 0.5) * step, z = z0 + (j + 0.5) * step;
                if (x < box.Min.X - 0.5 || x > box.Max.X + 0.5 || z < box.Min.Z - 0.5 || z > box.Max.Z + 0.5) return true;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int a = i + di, c = j + dj;
                    if (a < 0 || c < 0 || a >= nx || c >= nz || seen[a, c]) continue;
                    double px = x0 + (a + 0.5) * step, pz = z0 + (c + 0.5) * step;
                    if (Terrain.BoxOverlapsSolid(new Aabb(new Vec3d(px - half, from.Y + lo, pz - half), new Vec3d(px + half, from.Y + hi, pz + half)))) continue;
                    seen[a, c] = true;
                    queue.Enqueue((a, c));
                }
            }
            return false;
        }

        [Test]
        public void SealedLootIsSealed_AndOpenGroundFloorLootCanBeWalkedTo()
        {
            List<Vec3d> sealedSpots = NwBuild.SealedChests(B);
            Assert.That(sealedSpots.Count, Is.GreaterThanOrEqualTo(B.Chests.Count / 4), "a good share of the loot is behind walls");
            int open = 0, shut = 0;
            var bad = new List<string>();
            foreach (BrChestSpot ch in B.Chests)
            {
                bool sealed_ = sealedSpots.Exists(s => (s - ch.Position).Length < 1e-6);
                BrBuilding? inside = null;
                foreach (BrBuilding bd in B.Buildings)
                    if (ch.Position.X > bd.Bounds.Min.X && ch.Position.X < bd.Bounds.Max.X && ch.Position.Z > bd.Bounds.Min.Z && ch.Position.Z < bd.Bounds.Max.Z
                        && ch.Position.Y >= bd.Bounds.Min.Y - 0.01 && ch.Position.Y < bd.Bounds.Max.Y) inside = bd;
                if (inside == null) { Assert.That(sealed_, Is.False, $"a sealed chest at {ch.Position} is in no building"); continue; }
                Aabb box = inside.Value.Bounds;
                bool ground = Math.Abs(ch.Position.Y - box.Min.Y) < 0.05;
                if (sealed_)
                {
                    shut++;
                    // Neither walking out (doorways) nor climbing out (windows: 1.5..3 m over the floor) gets out.
                    if (Escapes(ch.Position, box, 0.05, 1.75)) bad.Add($"sealed chest in {inside.Value.Name} at {ch.Position} can be walked to");
                    if (Escapes(ch.Position, box, 1.6, 2.9)) bad.Add($"sealed chest in {inside.Value.Name} at {ch.Position} has a window");
                }
                else if (ground)
                {
                    open++;
                    if (!Escapes(ch.Position, box, 0.05, 1.75) && !Escapes(ch.Position, box, 1.6, 2.9)) bad.Add($"open chest in {inside.Value.Name} at {ch.Position} is shut in");
                }
            }
            TestContext.WriteLine($"{shut} sealed chests, {open} open ground-floor chests checked");
            Assert.That(bad, Is.Empty, string.Join("\n", bad));
        }

        [TestCase("Radio mast", 8)]
        [TestCase("Grain elevator", 7)]
        [TestCase("Water tower", 6)]
        [TestCase("Bell tower", 4)]
        [TestCase("Transmitter hall", 2)]
        [TestCase("Greyfield farmhouse", 2)]
        [TestCase("Hollins farmhouse", 2)]
        [TestCase("The Plough inn", 2)]
        [TestCase("Greyfield great barn", 2)]
        [TestCase("Hollins red barn", 2)]
        [TestCase("Cider barn", 2)]
        [TestCase("Pond mill", 2)]
        public void ItsTallBuildings_HaveStairsAllTheWayToAWalledRoof(string name, int storeys)
        {
            BrBuilding bd = Named(name);
            Assert.That(bd.Name, Is.EqualTo(name), $"{name} is built");
            Aabb box = bd.Bounds;
            Assert.That(box.Max.Y - box.Min.Y, Is.EqualTo(storeys * GreyboxBlock.StoreyHeight).Within(1e-6));
            for (int s = 0; s < storeys; s++)
            {
                double y0 = box.Min.Y + s * GreyboxBlock.StoreyHeight;
                bool flight = T.Info.Block.Climbs.Exists(c => c.Kind == BrushKind.Stairs && Math.Abs(c.Bounds.Min.Y - y0) < 1e-6
                    && Math.Abs(c.Bounds.Max.Y - (y0 + GreyboxBlock.StoreyHeight)) < 1e-6 && box.Contains(c.Bounds.Min + new Vec3d(0.01, 0.01, 0.01), 0));
                Assert.That(flight, Is.True, $"{name}: a flight up from storey {s}");
            }
            // A parapet on the roof: tiles standing on the roof at every edge.
            double top = box.Max.Y, mx = (box.Min.X + box.Max.X) / 2, mz = (box.Min.Z + box.Max.Z) / 2;
            foreach (Vec3d p in new[] { new Vec3d(box.Min.X + 0.15, top + 0.7, mz), new Vec3d(box.Max.X - 0.15, top + 0.7, mz), new Vec3d(mx, top + 0.7, box.Min.Z + 0.15), new Vec3d(mx, top + 0.7, box.Max.Z - 0.15) })
                Assert.That(T.World.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.05, 0.05, 0.05), p + new Vec3d(0.05, 0.05, 0.05))), Is.True, $"{name}: parapet at {p}");
        }

        private static MotorState Run(Vec3d at, double yaw, double seconds, Func<MotorState, bool> until = null)
        {
            var c = new MovementConfig();
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 30 * SimTime.Per60; k++) PlayerMovement.Step(Terrain, c, ref s, new MoveInput { Yaw = yaw });
            bool held = false;
            int ticks = (int)(seconds * SimTime.TicksPerSecond);
            for (int k = 0; k < ticks; k++)
            {
                bool jump = k > 10 * SimTime.Per60 && (held || (s.Grounded && s.HorizontalSpeed < 2));
                PlayerMovement.Step(Terrain, c, ref s, new MoveInput { Yaw = yaw, MoveZ = 1, SprintHeld = true, JumpPressed = jump && !held, JumpHeld = jump });
                held = jump && s.Mode != MoveMode.Ground;
                if (until != null && until(s)) break;
            }
            return s;
        }

        private static Vec3d OnGround(double x, double z) => new Vec3d(x, BrMap.Field.HeightAt(x, z) + 0.05, z);

        [Test]
        public void TheSummitCompound_CanBeRunUpTo_FromTheHillsFoot_AndInThroughItsGate()
        {
            BrBuilding hall = Named("Transmitter hall");
            MotorState end = Run(OnGround(197.5, 1034), 0, 16, s => s.Grounded && s.Position.Z > 1100);
            TestContext.WriteLine($"ended at {end.Position}");
            Assert.That(end.Position.Y, Is.EqualTo(hall.Bounds.Min.Y).Within(0.05), "on the summit's level ground");
            Assert.That(end.Position.Z, Is.GreaterThan(1100), "through the south gate into the yard");
        }

        [Test]
        public void TheCrag_CanBeWalkedDownOntoFromTheSummit()
        {
            Vec3d start = OnGround(160, 1125), goal = new Vec3d(135, 0, 1158);
            var crag = new Rect2(NorthWestSegment.CragX0, NorthWestSegment.CragZ0, NorthWestSegment.CragX1, NorthWestSegment.CragZ1);
            MotorState end = Run(start, Math.Atan2(goal.X - start.X, goal.Z - start.Z), 8,
                s => s.Grounded && crag.Contains(s.Position.X, s.Position.Z, 2) && Math.Abs(s.Position.Y - NorthWestSegment.CragTop) < 0.3);
            TestContext.WriteLine($"ended at {end.Position}");
            Assert.That(end.Position.Y, Is.EqualTo(NorthWestSegment.CragTop).Within(0.3), "on the crag shelf");
            Assert.That(crag.Contains(end.Position.X, end.Position.Z), Is.True);
        }

        [Test]
        public void TheHolloway_IsASunkenLane_WithItsBanksAboveYourHead()
        {
            HeightField f = BrMap.Field;
            for (double x = 280; x <= 390; x += 10)
            {
                double z = 0, best = double.MaxValue;
                for (double q = 1040; q <= 1080; q += 0.25) { double d = NorthWestSegment.LaneCut(x, q); if (-d < best) { best = -d; z = q; } }
                double floor = f.HeightAt(x, z), north = f.HeightAt(x, z + 16), south = f.HeightAt(x, z - 16);
                Assert.That(Math.Min(north, south) - floor, Is.GreaterThan(x < 300 ? 1.5 : 2.0), $"the lane at x {x:0} is sunk (floor {floor:0.00}, banks {south:0.00} / {north:0.00})");
            }
        }

        [Test]
        public void TheElevatorsFifthFloorWindows_StepOutOntoTheSiloDeck()
        {
            BrBuilding elev = Named("Grain elevator");
            double deck = elev.Bounds.Min.Y + 4 * GreyboxBlock.StoreyHeight;
            var silos = B.Buildings.FindAll(x => x.Name == "Silo" && Math.Abs(x.Bounds.Min.X - elev.Bounds.Max.X) < 20 && Math.Abs(x.Bounds.Min.Z - elev.Bounds.Min.Z) < 20);
            Assert.That(silos.Count, Is.EqualTo(6));
            foreach (BrBuilding s in silos) Assert.That(s.Bounds.Max.Y, Is.EqualTo(deck).Within(1e-6), "every silo roof is level with the fifth floor");
            Assert.That(silos.Exists(s => Math.Abs(s.Bounds.Min.X - elev.Bounds.Max.X) < 1e-6), Is.True, "the deck touches the elevator");
            // The window: open at sill-to-lintel height on the east face of floor 4, over the deck.
            var p = new Vec3d(elev.Bounds.Max.X - 0.15, deck + 2.2, elev.Bounds.Min.Z + 0.3 + 3.15);
            Assert.That(T.World.BoxOverlapsSolid(new Aabb(p - new Vec3d(0.3, 0.5, 0.5), p + new Vec3d(0.3, 0.5, 0.5))), Is.False, "the window is open");
        }

        [Test, Explicit]
        public void DebugFootingsNear()
        {
            Footings foot = BrMap.FootingsUsed;
            for (int k = 0; k < foot.Count; k++)
            {
                Footings.Footing a = foot[k];
                if (Footings.RectGap(a.X0, a.Z0, a.X1, a.Z1, 118, 1000, 128, 1010) < 25)
                    TestContext.WriteLine($"{k}: {a.X0:0.0},{a.Z0:0.0}..{a.X1:0.0},{a.Z1:0.0} level {a.Level:0.00} apron {a.Apron}");
            }
            HeightField f = BrMap.Field;
            for (double z = 1000; z <= 1010; z += 1.5) TestContext.WriteLine($"z {z}: " + string.Join(" ", new[] { 118.0, 120, 122, 124, 126, 128 }.Select(x => f.HeightAt(x, z).ToString("0.000"))));
        }

        // ---------------------------------------------------------------- the plan picture

        [Test]
        public void WritePlan()
        {
            string dir = Environment.GetEnvironmentVariable("SCULPT_PLAN_DIR");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("set SCULPT_PLAN_DIR to write the plan");
            Directory.CreateDirectory(dir);
            double px = 0.4;
            Rect2 sq = BrLayout.Bounds(Id);
            string crop = Environment.GetEnvironmentVariable("SCULPT_PLAN_CROP"); // "x0,z0,size" for a close-up
            if (!string.IsNullOrEmpty(crop))
            {
                string[] c = crop.Split(',');
                double cs = double.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture);
                sq = new Rect2(double.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture), 0, 0);
                sq = new Rect2(sq.X0, sq.Z0, sq.X0 + cs, sq.Z0 + cs);
                px = cs / 1000;
            }
            int n = (int)Math.Ceiling((sq.X1 - sq.X0) / px);
            var img = new byte[n * n * 3];
            HeightField f = BrMap.Field;
            Footings foot = BrMap.FootingsUsed;
            void Put(double x, double z, (int r, int g, int b) c)
            {
                int i = (int)((x - sq.X0) / px), r = (int)((sq.Z1 - z) / px);
                if (i < 0 || r < 0 || i >= n || r >= n) return;
                int k = (r * n + i) * 3;
                img[k] = (byte)Math.Clamp(c.r, 0, 255); img[k + 1] = (byte)Math.Clamp(c.g, 0, 255); img[k + 2] = (byte)Math.Clamp(c.b, 0, 255);
            }
            for (int r = 0; r < n; r++)
                for (int i = 0; i < n; i++)
                {
                    double x = sq.X0 + (i + 0.5) * px, z = sq.Z1 - (r + 0.5) * px;
                    if (!f.Covers(x, z)) { Put(x, z, (20, 20, 24)); continue; }
                    double h = f.HeightAt(x, z);
                    Vec3d nn = f.NormalAt(x, z);
                    double t = Math.Clamp((h + 4) / 38, 0, 1);
                    double cr = 70 + 150 * t, cg = 130 + 70 * Math.Sin(t * Math.PI), cb = 90 - 50 * t;
                    double light = Math.Clamp(0.55 + 0.6 * (-nn.X * 0.6 + nn.Y * 0.5 + nn.Z * 0.6), 0.35, 1.25);
                    if (nn.Y < f.WalkableNormalY) { cr = cg = cb = 125; }
                    else if (foot.OnLevelGroundFast(x, z) && Math.Abs(h - Math.Round(h / 0.3) * 0.3) < 1e-4) { cr = 205; cg = 198; cb = 180; }
                    // Contours every 3 m.
                    if (Math.Abs(h / 3 - Math.Round(h / 3)) < 0.03) { cr *= 0.85; cg *= 0.85; cb *= 0.85; }
                    if (!B.Area.Contains(x, z)) { cr *= 0.75; cg *= 0.75; cb *= 0.75; }
                    Put(x, z, ((int)(cr * light), (int)(cg * light), (int)(cb * light)));
                }
            foreach (Rect2 q in B.Reserved)
                for (double z = q.Z0; z <= q.Z1; z += px)
                    for (double x = q.X0; x <= q.X1; x += px)
                        if ((int)((x + z) / px) % 6 == 0) Put(x, z, (90, 90, 140));
            foreach (var l in B.Lanes)
                for (double s = 0; s <= 1; s += 0.001)
                    Put(l.From.X + (l.To.X - l.From.X) * s, l.From.Z + (l.To.Z - l.From.Z) * s, (90, 170, 255));
            Vec3d a = B.Anchor;
            for (double s = 0; s < Math.PI * 2; s += 0.002)
                Put(a.X + Math.Cos(s) * BrLayout.HomeRadius, a.Z + Math.Sin(s) * BrLayout.HomeRadius, (255, 120, 220));

            // Tiles: floors faint, upper walls lighter, ground-floor walls dark, doors brown, stairs yellow.
            BrushWorld w = T.World;
            var order = new[] { TileKind.Floor, TileKind.Frame, TileKind.Wall, TileKind.Door, TileKind.Stair, TileKind.Ramp };
            foreach (TileKind kind in order)
                for (int k = 0; k < w.Tiles.Count; k++)
                {
                    if (w.Tiles[k].Kind != kind) continue;
                    Aabb bd = w.Tiles.BoundsOf(k);
                    if (!sq.Contains(bd.Min.X, bd.Min.Z) && !sq.Contains(bd.Max.X, bd.Max.Z)) continue;
                    double g = f.HeightAt((bd.Min.X + bd.Max.X) / 2, (bd.Min.Z + bd.Max.Z) / 2);
                    bool low = bd.Min.Y < g + 2.0 && bd.Max.Y > g + 0.5;
                    (int, int, int) c = kind switch
                    {
                        TileKind.Floor => (172, 150, 140),
                        TileKind.Frame => (60, 60, 80),
                        TileKind.Door => (150, 90, 30),
                        TileKind.Stair => (240, 210, 40),
                        TileKind.Ramp => (240, 210, 40),
                        _ => low ? (70, 25, 20) : (150, 90, 80),
                    };
                    if (kind == TileKind.Floor && bd.Min.Y > g + 6) c = (190, 170, 160);
                    if (kind == TileKind.Wall && !low) continue;
                    if (kind == TileKind.Frame && !low) continue;
                    for (double z = bd.Min.Z; z <= bd.Max.Z + 1e-6; z += px * 0.5)
                        for (double x = bd.Min.X; x <= bd.Max.X + 1e-6; x += px * 0.5) Put(x, z, c);
                }
            BrNature.Plan plan = BrNature.GetPlan();
            foreach (BrNature.Prop p in plan.Props)
            {
                if (p.Segment != Id) continue;
                (int, int, int) c = p.Shape == BrNature.Shape.Bush ? (110, 200, 70) : p.Shape == BrNature.Shape.Rocks ? (150, 150, 150) : (20, 90, 30);
                double rr = p.Shape == BrNature.Shape.Bush || p.Shape == BrNature.Shape.Rocks ? p.Radius : 1.0;
                for (double s = 0; s < Math.PI * 2; s += 0.05) Put(p.X + Math.Cos(s) * p.Radius, p.Z + Math.Sin(s) * p.Radius, c);
                for (double dz = -rr; dz <= rr; dz += px) for (double dx = -rr; dx <= rr; dx += px) if (dx * dx + dz * dz <= rr * rr) Put(p.X + dx, p.Z + dz, c);
            }
            foreach (PropRequest q in plan.Dropped)
                if (q.Segment == Id)
                    for (double d = -2; d <= 2; d += px * 0.5) { Put(q.X + d, q.Z + d, (255, 0, 255)); Put(q.X + d, q.Z - d, (255, 0, 255)); }
            var sealedSpots = NwBuild.SealedChests(B);
            foreach (BrChestSpot ch in B.Chests)
            {
                bool sealed_ = sealedSpots.Exists(s => (s - ch.Position).Length < 1e-6);
                for (double dz = -0.8; dz <= 0.8; dz += px * 0.5)
                    for (double dx = -0.8; dx <= 0.8; dx += px * 0.5) Put(ch.Position.X + dx, ch.Position.Z + dz, sealed_ ? (255, 60, 40) : (255, 230, 0));
            }
            foreach (GroundItem it in B.Items)
                for (double dz = -0.6; dz <= 0.6; dz += px * 0.5)
                    for (double dx = -0.6; dx <= 0.6; dx += px * 0.5) Put(it.Position.X + dx, it.Position.Z + dz, (0, 220, 255));
            var m = typeof(MapPlan).GetMethod("Png", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            File.WriteAllBytes(Path.Combine(dir, string.IsNullOrEmpty(crop) ? "nw_plan.png" : "nw_crop.png"), (byte[])m.Invoke(null, new object[] { img, n, n }));
        }
    }
}
