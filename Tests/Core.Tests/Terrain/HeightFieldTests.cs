using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Audio;

namespace Sculpt.Core.Tests
{
    /// <summary>The smooth height-map ground (map v3 step 3): shape, collision, bullets, slopes, rock faces, sound, bots.</summary>
    public class HeightFieldTests
    {
        private static readonly double East = Math.PI / 2;

        /// <summary>Rolling random ground, 1.5 m cells, heights up to a few metres.</summary>
        private static HeightField Bumpy(int seed, int n = 24, double amp = 3)
        {
            var r = new Random(seed);
            var f = new HeightField(-n * 0.75, -n * 0.75, 1.5, n, n);
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++) f.Set(i, j, r.NextDouble() * amp);
            f.Finish();
            return f;
        }

        /// <summary>Flat at 0 west of x0, then a plane rising east at the given slope (degrees).</summary>
        private static HeightField Slope(double degrees, double x0 = 0, double size = 120)
        {
            double k = Math.Tan(degrees * Math.PI / 180);
            int n = (int)(size / 1.5);
            return HeightField.FromFunction(-size / 2, -size / 2, 1.5, n, n, (x, z) => x <= x0 ? 0 : k * (x - x0));
        }

        // ---------------------------------------------------------------- the shape

        [Test]
        public void TheSurfaceIsContinuousAcrossEveryEdgeAndDiagonal()
        {
            HeightField f = Bumpy(1);
            var r = new Random(2);
            for (int k = 0; k < 4000; k++)
            {
                double x = f.OriginX + r.NextDouble() * f.SizeX, z = f.OriginZ + r.NextDouble() * f.SizeZ;
                const double e = 1e-7;
                double h = f.HeightAt(x, z);
                Assert.That(f.HeightAt(x + e, z), Is.EqualTo(h).Within(1e-5));
                Assert.That(f.HeightAt(x, z + e), Is.EqualTo(h).Within(1e-5));
            }
            // Exactly on a vertex: the vertex's height; on a cell centre: the mean of the corners.
            Assert.That(f.HeightAt(f.VertexX(3), f.VertexZ(5)), Is.EqualTo(f[3, 5]).Within(1e-9));
            double mean = (f[3, 5] + f[4, 5] + f[4, 6] + f[3, 6]) / 4;
            Assert.That(f.HeightAt(f.VertexX(3) + 0.75, f.VertexZ(5) + 0.75), Is.EqualTo(mean).Within(1e-6));
        }

        [Test]
        public void MaxOver_IsTheTrueHighestPoint()
        {
            for (int seed = 0; seed < 6; seed++)
            {
                HeightField f = Bumpy(seed);
                var r = new Random(100 + seed);
                for (int k = 0; k < 300; k++)
                {
                    double x0 = f.OriginX + r.NextDouble() * (f.SizeX - 4), z0 = f.OriginZ + r.NextDouble() * (f.SizeZ - 4);
                    double w = r.Next(4) == 0 ? 0 : r.NextDouble() * 3, d = r.Next(4) == 0 ? 0 : r.NextDouble() * 3;
                    double exact = f.MaxOver(x0, z0, x0 + w, z0 + d);
                    double sampled = double.NegativeInfinity;
                    const int s = 60;
                    for (int a = 0; a <= s; a++)
                        for (int b = 0; b <= s; b++)
                            sampled = Math.Max(sampled, f.HeightAt(x0 + w * a / s, z0 + d * b / s));
                    Assert.That(exact, Is.GreaterThanOrEqualTo(sampled - 1e-6), "never below any point of the surface");
                    // A fine sample gets within one sample step's rise of the true top.
                    Assert.That(exact, Is.LessThanOrEqualTo(sampled + 3 * 4 * (Math.Max(w, d) / s + 1e-9) + 1e-6));
                }
            }
        }

        [Test]
        public void Symmetric_EveryVertexEqualsItsSevenTwins()
        {
            HeightField f = HeightField.Symmetric(10, 20, 1.5, 40, (u, v) => 5 * Math.Sin(u * 0.07) + 2 * Math.Cos(v * 0.13) + 0.01 * u * v);
            int n = f.NX, c = n / 2;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    int x = i - c, z = j - c;
                    double h = f[i, j];
                    foreach (var (a, b) in new[] { (x, z), (-x, z), (x, -z), (-x, -z), (z, x), (-z, x), (z, -x), (-z, -x) })
                        Assert.That(f[a + c, b + c], Is.EqualTo(h), "exact twin");
                }
            // The surface between vertices is symmetric too (the four-triangle split has no handedness).
            var r = new Random(5);
            for (int k = 0; k < 2000; k++)
            {
                double x = (r.NextDouble() * 2 - 1) * 55, z = (r.NextDouble() * 2 - 1) * 55;
                double h = f.HeightAt(10 + x, 20 + z);
                Assert.That(f.HeightAt(10 - x, 20 + z), Is.EqualTo(h).Within(1e-5));
                Assert.That(f.HeightAt(10 + z, 20 + x), Is.EqualTo(h).Within(1e-5));
                Assert.That(f.HeightAt(10 - z, 20 - x), Is.EqualTo(h).Within(1e-5));
            }
        }

        // ---------------------------------------------------------------- collision

        [Test]
        public void ABoxTouchingTheGroundDoesNotOverlap_ALittleLowerDoes()
        {
            HeightField f = Bumpy(3);
            var r = new Random(4);
            for (int k = 0; k < 500; k++)
            {
                double x = (r.NextDouble() * 2 - 1) * 14, z = (r.NextDouble() * 2 - 1) * 14;
                double top = f.MaxOver(x - 0.35, z - 0.35, x + 0.35, z + 0.35);
                var on = new Aabb(new Vec3d(x - 0.35, top, z - 0.35), new Vec3d(x + 0.35, top + 1.8, z + 0.35));
                Assert.That(f.BoxOverlapsSolid(on), Is.False);
                Assert.That(f.BoxOverlapsSolid(on.Translate(new Vec3d(0, -0.001, 0))), Is.True);
                Assert.That(f.BoxOverlapsSolid(on.Translate(new Vec3d(0, -30, 0))), Is.True, "deep under it is solid");
            }
        }

        [Test]
        public void GroundContact_GivesTheSlopeNormal_AndNoGroundOnRockFaces()
        {
            HeightField gentle = Slope(20, x0: -100);
            var box = new Aabb(new Vec3d(-0.35, 0, -0.35), new Vec3d(0.35, 1.8, 0.35));
            double top = gentle.MaxOver(-0.35, -0.35, 0.35, 0.35);
            Aabb on = box.Translate(new Vec3d(0, top, 0));
            Assert.That(gentle.GroundContact(on, 0.02, out Vec3d n), Is.True);
            Assert.That(Math.Acos(n.Y) * 180 / Math.PI, Is.EqualTo(20).Within(1e-4));
            Assert.That(n.X, Is.LessThan(0), "points down the slope");
            Assert.That(gentle.SteepUnder(on, 0.02, out _), Is.False);

            HeightField steep = Slope(65, x0: -100);
            top = steep.MaxOver(-0.35, -0.35, 0.35, 0.35);
            on = box.Translate(new Vec3d(0, top, 0));
            Assert.That(steep.GroundContact(on, 0.02, out _), Is.False, "65 degrees is a rock face");
            Assert.That(steep.SteepUnder(on, 0.02, out Vec3d face), Is.True);
            Assert.That(face.Y, Is.EqualTo(Math.Cos(65 * Math.PI / 180)).Within(1e-5));
        }

        // ---------------------------------------------------------------- bullets

        [Test]
        public void Raycast_HitsTheFirstPointOnTheSurface()
        {
            for (int seed = 0; seed < 4; seed++)
            {
                HeightField f = Bumpy(seed + 10);
                var r = new Random(seed);
                int hits = 0;
                for (int k = 0; k < 600; k++)
                {
                    var o = new Vec3d((r.NextDouble() * 2 - 1) * 17, 2 + r.NextDouble() * 6, (r.NextDouble() * 2 - 1) * 17);
                    if (o.Y <= f.HeightAt(o.X, o.Z)) continue;
                    var d = new Vec3d(r.NextDouble() * 2 - 1, -r.NextDouble() * 0.8, r.NextDouble() * 2 - 1).Normalized;
                    TerrainHit h = f.Raycast(o, d, 60);
                    // March the same ray finely: nothing under the surface before the hit, and the hit is on it.
                    double first = double.PositiveInfinity;
                    for (double t = 0; t <= 60; t += 0.002)
                    {
                        Vec3d p = o + d * t;
                        if (!f.Covers(p.X, p.Z)) break; // left the ground's edge
                        if (p.Y <= f.HeightAt(p.X, p.Z)) { first = t; break; }
                    }
                    if (double.IsPositiveInfinity(first)) { Assert.That(h.Hit, Is.False, $"miss {o} {d}"); continue; }
                    // (Rays that leave the grid never come back: the grid is convex.)
                    hits++;
                    Assert.That(h.Hit, Is.True);
                    Assert.That(h.Ground, Is.True);
                    Assert.That(h.Distance, Is.EqualTo(first).Within(0.003));
                    Assert.That(h.Point.Y, Is.EqualTo(f.HeightAt(h.Point.X, h.Point.Z)).Within(1e-6), $"o {o.X:R} {o.Y:R} {o.Z:R} d {d.X:R} {d.Y:R} {d.Z:R} t {h.Distance} first {first} seed {seed}");
                    Assert.That(h.Normal.Y, Is.GreaterThan(0));
                }
                Assert.That(hits, Is.GreaterThan(200));
            }
        }

        [Test]
        public void Raycast_StraightDownFindsTheHeight_AndUpwardRaysMiss()
        {
            HeightField f = Bumpy(7);
            var r = new Random(8);
            for (int k = 0; k < 500; k++)
            {
                double x = (r.NextDouble() * 2 - 1) * 17, z = (r.NextDouble() * 2 - 1) * 17;
                TerrainHit h = f.Raycast(new Vec3d(x, 20, z), new Vec3d(0, -1, 0), 40);
                Assert.That(h.Hit, Is.True);
                Assert.That(h.Point.Y, Is.EqualTo(f.HeightAt(x, z)).Within(1e-6));
                Assert.That(f.Raycast(new Vec3d(x, f.HeightAt(x, z) + 0.01, z), new Vec3d(0.3, 1, 0.2), 40).Hit, Is.False);
            }
        }

        [Test]
        public void ABulletOverAHillTopIsStoppedByTheHill_AndTheBuildingsStillCount()
        {
            // A 4 m hill between two players 40 m apart.
            HeightField f = HeightField.FromFunction(-60, -60, 1.5, 80, 80, (x, z) => Math.Max(0, 4 - Math.Abs(x) * 0.4));
            var walls = new BrushWorld();
            walls.AddBox(new Vec3d(-30, 0, 10), new Vec3d(-29.7, 3, 14), BrushKind.Wall);
            ITerrain t = new CompositeTerrain(f, walls);
            Vec3d a = new Vec3d(-20, 1.6, 0), b = new Vec3d(20, 1.6, 0);
            TerrainHit h = t.Raycast(a, b - a, (b - a).Length);
            Assert.That(h.Hit && h.Ground, Is.True);
            Assert.That(h.Point.X, Is.LessThan(0));
            // Over the top: clear.
            Assert.That(t.Raycast(new Vec3d(-20, 5, 0), new Vec3d(1, 0, 0), 40).Hit, Is.False);
            // The wall is still hit (and reported as a brush).
            TerrainHit w = t.Raycast(new Vec3d(-35, 1.5, 12), new Vec3d(1, 0, 0), 10);
            Assert.That(w.Hit && !w.Ground && w.Brush > 0, Is.True);
        }

        // ---------------------------------------------------------------- movement

        private static MotorState Standing(HeightField t, MovementConfig c, double x, double z, double yaw)
        {
            var s = MotorState.At(new Vec3d(x, t.MaxOver(x - 0.4, z - 0.4, x + 0.4, z + 0.4) + 1, z));
            s.Tick = 1000;
            s.Mode = MoveMode.Air;
            for (int k = 0; k < 600 && !s.Grounded; k++) PlayerMovement.Step(t, c, ref s, new MoveInput { Yaw = yaw });
            for (int k = 0; k < 20; k++) PlayerMovement.Step(t, c, ref s, new MoveInput { Yaw = yaw });
            Assert.That(s.Grounded, Is.True, "landed");
            return s;
        }

        [Test]
        public void YouSprintUpAndDownAWalkableHill()
        {
            var c = new MovementConfig();
            foreach (double deg in new[] { 15.0, 30.0, 40.0 })
            {
                HeightField f = Slope(deg, x0: 0);
                MotorState s = Standing(f, c, -10, 0, East);
                for (int k = 0; k < 4 * SimTime.TicksPerSecond; k++)
                    PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
                Assert.That(s.Position.X, Is.GreaterThan(15), $"{deg} degrees: kept climbing");
                Assert.That(s.Position.Y, Is.EqualTo(f.HeightAt(s.Position.X, 0)).Within(0.8), "on the slope, not floating off it");
                Assert.That(s.Grounded, Is.True);
                // And back down without leaving the ground for long.
                int air = 0;
                for (int k = 0; k < 3 * SimTime.TicksPerSecond; k++)
                {
                    PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = -East, MoveZ = 1, SprintHeld = true });
                    if (!s.Grounded) air++;
                }
                Assert.That(air, Is.LessThan(SimTime.TicksPerSecond / 2), $"{deg} degrees: ran down it");
            }
        }

        [Test]
        public void ASlideSpeedsUpDownhill_AndSlowsUphill()
        {
            var c = new MovementConfig();
            double Slid(HeightField f, double startX, double yaw)
            {
                MotorState s = Standing(f, c, startX, 0, yaw);
                for (int k = 0; k < SimTime.TicksPerSecond; k++) PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = yaw, MoveZ = 1, SprintHeld = true });
                PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = yaw, MoveZ = 1, SprintHeld = true, CrouchPressed = true, CrouchHeld = true });
                Assert.That(s.Mode, Is.EqualTo(MoveMode.Slide));
                for (int k = 0; k < SimTime.TicksPerSecond; k++) PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = yaw, MoveZ = 1, CrouchHeld = true });
                return s.HorizontalSpeed;
            }
            HeightField flat = Slope(0);
            HeightField hill = Slope(20, x0: -100);
            double onFlat = Slid(flat, 0, East);
            double down = Slid(hill, 40, -East);
            double up = Slid(hill, -40, East);
            Assert.That(down, Is.GreaterThan(onFlat + 2), "downhill builds speed");
            Assert.That(up, Is.LessThan(onFlat - 1), "uphill costs speed");
        }

        [Test]
        public void ARockFaceStopsYou_YouNeverWalkUpIt()
        {
            var c = new MovementConfig();
            foreach (double deg in new[] { 55.0, 65.0, 75.0 })
            {
                HeightField f = Slope(deg, x0: 0);
                MotorState s = Standing(f, c, -6, 0, East);
                double maxY = s.Position.Y;
                for (int k = 0; k < 3 * SimTime.TicksPerSecond; k++)
                {
                    PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
                    maxY = Math.Max(maxY, s.Position.Y);
                }
                Assert.That(maxY, Is.LessThan(c.MaxStepUp + 0.05), $"{deg} degrees: never climbs it");
                Assert.That(s.Position.X, Is.LessThan(0.6), "stopped at its foot");
            }
        }

        [Test]
        public void YouSlideOffARockFace_YouNeverStandOnIt()
        {
            var c = new MovementConfig();
            HeightField f = Slope(65, x0: 0);
            // Dropped onto the face 6 m up.
            double x = 4;
            var s = MotorState.At(new Vec3d(x, f.HeightAt(x + 0.35, 0) + 1.5, 0));
            s.Tick = 1000;
            s.Mode = MoveMode.Air;
            for (int k = 0; k < 3 * SimTime.TicksPerSecond; k++) PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = East });
            Assert.That(s.Position.Y, Is.LessThan(0.5), "slid all the way down");
            Assert.That(s.Grounded, Is.True, "standing on the flat at the foot");
        }

        [Test]
        public void AJumpOnAHillsideLandsOnIt()
        {
            var c = new MovementConfig();
            HeightField f = Slope(25, x0: -100);
            MotorState s = Standing(f, c, 0, 0, East);
            PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true, JumpHeld = true });
            Assert.That(s.Mode, Is.EqualTo(MoveMode.Air));
            int k = 0;
            while (s.Mode == MoveMode.Air && k++ < 2 * SimTime.TicksPerSecond) PlayerMovement.Step(f, c, ref s, new MoveInput { Yaw = East, MoveZ = 1 });
            Assert.That(s.Grounded, Is.True);
            Assert.That(s.Position.X, Is.GreaterThan(1), "kept moving up the hill");
        }

        // ---------------------------------------------------------------- sound and sight

        [Test]
        public void SoundGrid_MatchesTheSurfaceLineByLine()
        {
            HeightField f = Bumpy(21, n: 16, amp: 4);
            var g = new SoundGrid(f);
            Assert.That(g.Supported, Is.True);
            var r = new Random(22);
            int checkedLines = 0, mixed = 0;
            double cs = g.CellSize, q = cs / 4;
            for (int k = 0; k < 3000; k++)
            {
                int x = r.Next(-20, 20), z = r.Next(-20, 20), y = r.Next(-2, 10);
                ushort b = g.Blocked(x, y, z);
                if (b != 0 && b != SoundGrid.AllBlocked) mixed++;
                for (int a = 0; a < 3; a++)
                    for (int l = 0; l < 4; l++)
                    {
                        double ou = (l & 1) == 0 ? q : 3 * q, ov = (l & 2) == 0 ? q : 3 * q;
                        Vec3d p0, p1;
                        if (a == 0) { p0 = new Vec3d((x + 0.5) * cs, y * cs + ou, z * cs + ov); p1 = p0 + new Vec3d(cs, 0, 0); }
                        else if (a == 1) { p0 = new Vec3d(x * cs + ou, (y + 0.5) * cs, z * cs + ov); p1 = p0 + new Vec3d(0, cs, 0); }
                        else { p0 = new Vec3d(x * cs + ou, y * cs + ov, (z + 0.5) * cs); p1 = p0 + new Vec3d(0, 0, cs); }
                        // Brute force: sample the line against the surface (off the grid there is no ground).
                        bool hit = false;
                        double margin = double.PositiveInfinity;
                        for (int s = 0; s <= 200; s++)
                        {
                            Vec3d p = p0 + (p1 - p0) * (s / 200.0);
                            double h = f.HeightAt(p.X, p.Z);
                            if (double.IsNegativeInfinity(h)) continue;
                            margin = Math.Min(margin, Math.Abs(p.Y - h));
                            if (p.Y <= h) hit = true;
                        }
                        if (margin < 0.02) continue; // grazing: either answer is fine
                        if (p0.Y < g.FloorY) continue;
                        bool blocked = ((b >> (4 * a + l)) & 1) != 0;
                        Assert.That(blocked, Is.EqualTo(hit), $"cell {x},{y},{z} axis {a} line {l}");
                        checkedLines++;
                    }
            }
            Assert.That(checkedLines, Is.GreaterThan(20000));
            Assert.That(mixed, Is.GreaterThan(50), "cells at the surface were tested");
        }

        [Test]
        public void SightOverAHill_IsBlockedOnlyByTheHill()
        {
            HeightField f = HeightField.FromFunction(-60, -60, 1.5, 80, 80, (x, z) => Math.Max(0, 4 - Math.Abs(x) * 0.4));
            var g = new SoundGrid(f);
            Assert.That(g.LineOpen(new Vec3d(-20, 1.6, 0), new Vec3d(20, 1.6, 0)), Is.False, "the hill is between them");
            Assert.That(g.LineOpen(new Vec3d(-20, 6, 0), new Vec3d(20, 6, 0)), Is.True, "over the top");
            Assert.That(g.LineOpen(new Vec3d(-30, 1.6, -5), new Vec3d(-15, 1.6, 5)), Is.True, "both on the same side");
        }

        // ---------------------------------------------------------------- bots

        [Test]
        public void TheBotWalkingGridWalksUpAHillside_NotUpARockFace()
        {
            // West: a 30 degree hillside up to a 9 m plateau; east of the plateau a 65 degree rock face down to the flat.
            double k30 = Math.Tan(30 * Math.PI / 180), k65 = Math.Tan(65 * Math.PI / 180);
            HeightField f = HeightField.FromFunction(0, 0, 1.5, 40, 16, (x, z) =>
                x < 6 ? 0 : x < 6 + 9 / k30 ? (x - 6) * k30 : x < 30 ? 9 : Math.Max(0, 9 - (x - 30) * k65));
            BotNav nav = BotNav.Build(f, 0, 0, 60, 24, 1.0, topY: 20);
            int bottom = nav.Nearest(new Vec3d(2, 0, 12)), top = nav.Nearest(new Vec3d(25, 9, 12)), east = nav.Nearest(new Vec3d(45, 0, 12), 3);
            Assert.That(bottom, Is.GreaterThanOrEqualTo(0));
            Assert.That(nav.Feet(top).Y, Is.EqualTo(9).Within(0.1));
            Assert.That(nav.SameArea(bottom, top), Is.True, "the hillside is a walk");
            var path = new List<int>();
            Assert.That(nav.FindPath(bottom, top, path), Is.True);
            foreach (int p in path) Assert.That(nav.Feet(p).X, Is.LessThan(31), "up the hillside, not the rock face");
        }
    }
}

namespace Sculpt.Core.Tests
{
    public class HillsMapTests
    {
        private static double Deg(Vec3d n) => System.Math.Acos(n.Y) * 180 / System.Math.PI;

        [Test]
        public void SlopesAreHillsOrRockFaces_InBetweenOnlyAtARockFacesEdge()
        {
            HeightField f = HillsMap.Field;
            int faces = 0, between = 0;
            for (int j = 0; j < f.NZ; j++)
                for (int i = 0; i < f.NX; i++)
                    for (int t = 0; t < 4; t++)
                    {
                        double d = Deg(f.TriangleNormal(i, j, t));
                        if (d >= 55) faces++;
                        if (d <= 36 || d >= 55) continue;
                        between++;
                        // Allowed at the lip or foot of a rock face (a cell can't be half one, half the other), or as a
                        // small kink under a metre (the low end of a ramp's side).
                        double lo = System.Math.Min(System.Math.Min(f[i, j], f[i + 1, j]), System.Math.Min(f[i, j + 1], f[i + 1, j + 1]));
                        double hi = System.Math.Max(System.Math.Max(f[i, j], f[i + 1, j]), System.Math.Max(f[i, j + 1], f[i + 1, j + 1]));
                        bool nearFace = hi - lo <= 1.0;
                        for (int b = System.Math.Max(0, j - 2); b <= System.Math.Min(f.NZ - 1, j + 2) && !nearFace; b++)
                            for (int a = System.Math.Max(0, i - 2); a <= System.Math.Min(f.NX - 1, i + 2) && !nearFace; a++)
                                for (int u = 0; u < 4; u++)
                                    if (Deg(f.TriangleNormal(a, b, u)) >= 55) nearFace = true;
                        Assert.That(nearFace, Is.True, $"a {d:0.0} degree slope out on open ground at {f.VertexX(i)}, {f.VertexZ(j)}");
                    }
            Assert.That(faces, Is.GreaterThan(500), "the mesa has rock faces");
            TestContext.Out.WriteLine($"rock face triangles {faces}, in-between {between}");
        }

        [Test]
        public void SpawnsStand_AndTheHillHouseStandsOnItsFooting()
        {
            HillsMap.BuildBrushes(out BreachMapInfo info);
            ITerrain t = HillsMap.Terrain(HillsMap.BuildGround(), HillsMap.BuildBrushes(out _));
            var c = new MovementConfig();
            foreach (Vec3d p in info.TeamSpawns[0])
            {
                Assert.That(HillsMap.Field.HeightAt(p.X, p.Z), Is.EqualTo(0).Within(1e-6));
                Assert.That(t.BoxOverlapsSolid(new Aabb(p + new Vec3d(-0.35, 0, -0.35), p + new Vec3d(0.35, 1.8, 0.35))), Is.False);
            }
            // Inside the hill house the floor is the footing, level to the centimetre.
            double x0 = HillsMap.FootX0 + 0.5, x1 = HillsMap.FootX0 + 12.5, z0 = HillsMap.FootZ0 + 0.5, z1 = HillsMap.FootZ0 + 12.5;
            for (double x = x0; x <= x1; x += 1.0)
                for (double z = z0; z <= z1; z += 1.0)
                    Assert.That(HillsMap.Field.HeightAt(x, z), Is.EqualTo(HillsMap.FootLevel).Within(1e-5));
        }

        [Test]
        public void YouWalkFromTheSpawnIntoTheHillHouse_AndOntoTheMesa()
        {
            ITerrain t = HillsMap.Terrain(HillsMap.BuildGround(), HillsMap.BuildBrushes(out BreachMapInfo info));
            BotNav nav = BotNav.Build(t, 0, 0, HillsMap.Size, HillsMap.Size, 1.0, topY: 30, floorY: -10);
            int spawn = nav.Nearest(info.TeamSpawns[0][0]);
            int house = nav.Nearest(new Vec3d(HillsMap.FootX0 + 6.5, HillsMap.FootLevel, HillsMap.FootZ0 + 6.5));
            int mesa = nav.Nearest(new Vec3d(165, 9, 155));
            int hilltop = nav.Nearest(new Vec3d(120, 18, 70));
            var path = new List<int>();
            foreach (int goal in new[] { house, mesa, hilltop })
            {
                Assert.That(goal, Is.GreaterThanOrEqualTo(0));
                Assert.That(nav.FindPath(spawn, goal, path), Is.True);
                for (int k = 0; k + 1 < path.Count; k++)
                    Assert.That(nav.LinkBetween(path[k], path[k + 1]), Is.Not.EqualTo(BotNav.Link.Jump), "walked, never jumped");
            }
            Assert.That(nav.Feet(mesa).Y, Is.EqualTo(9).Within(0.05));
            Assert.That(nav.Feet(house).Y, Is.EqualTo(HillsMap.FootLevel).Within(0.05));
        }
    }
}
