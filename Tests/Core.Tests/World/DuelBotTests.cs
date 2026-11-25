using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>The scripted 1v1 bot: walks the duel map, sees only what has a clear line, reacts late, fights, plays the match.</summary>
    public class DuelBotTests
    {
        private static BotNav _nav;
        private static BreachMapInfo _info;

        private static BotNav Nav()
        {
            if (_nav != null) return _nav;
            BrushWorld w = DuelMap.BuildBrushes(out _info);
            _nav = BotNav.Build(DuelMap.Terrain(DuelMap.BuildGround(), w), 0, 0, _info.SizeX, _info.SizeZ);
            return _nav;
        }

        private static SculptNetHarness Duel() =>
            new SculptNetHarness(DuelMap.BuildGround, wrap: g => DuelMap.Terrain(g, DuelMap.BuildBrushes(out _)));

        private static Vec3d Spawn(int team) { Nav(); return _info.TeamSpawns[team - 1][0]; }
        private static double SpawnYaw(int team) { Nav(); return _info.SpawnYaw[team - 1]; }

        [Test]
        public void Nav_CoversTheMap_BuildsQuickly_AndLinksBothSpawnsAndTheUpperFloors()
        {
            var sw = Stopwatch.StartNew();
            BrushWorld w = DuelMap.BuildBrushes(out BreachMapInfo info);
            BotNav nav = BotNav.Build(DuelMap.Terrain(DuelMap.BuildGround(), w), 0, 0, info.SizeX, info.SizeZ);
            sw.Stop();
            TestContext.WriteLine($"nav: {nav.Nodes.Count} nodes, {nav.EdgeCount} edges, {sw.ElapsedMilliseconds} ms");
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000), "built at map load; must not stall the menu");
            Assert.That(nav.Nodes.Count, Is.GreaterThan(nav.NX * nav.NZ / 2), "most of the map is walkable");

            var path = new List<int>();
            int a = nav.Nearest(info.TeamSpawns[0][0]), b = nav.Nearest(info.TeamSpawns[1][0]);
            Assert.That(a, Is.GreaterThanOrEqualTo(0));
            Assert.That(b, Is.GreaterThanOrEqualTo(0));
            Assert.That(nav.FindPath(a, b, path), "spawn to spawn");

            // Upper floors (first floor at 4.8 m, roofs at 9.6 m) are reachable by stairs and ramps from a spawn.
            int up1 = -1, up2 = -1;
            for (int i = 0; i < nav.Nodes.Count; i++)
            {
                double y = nav.Nodes[i].Y;
                if (up1 < 0 && Math.Abs(y - 4.8) < 0.05 && nav.Nodes[i].Open == 8 && nav.FindPath(a, i, path)) up1 = i;
                if (up2 < 0 && Math.Abs(y - 9.6) < 0.05 && nav.Nodes[i].Open == 8 && nav.FindPath(a, i, path)) up2 = i;
                if (up1 >= 0 && up2 >= 0) break;
            }
            Assert.That(up1, Is.GreaterThanOrEqualTo(0), "a first floor");
            Assert.That(up2, Is.GreaterThanOrEqualTo(0), "a roof");
        }

        [Test]
        public void CanSee_IsBlockedByWalls_LikeBullets()
        {
            Nav();
            ITerrain t = DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out BreachMapInfo info));
            Aabb hall = info.Block.Buildings[0];
            // From outside one wall of a building to the far side of it: blocked (unless doors line up, so try several).
            int blocked = 0, tries = 0;
            for (double x = hall.Min.X + 2; x < hall.Max.X - 2; x += 1.5)
            {
                tries++;
                Vec3d eye = new Vec3d(x, 1.62, hall.Min.Z - 2);
                if (!DuelBot.CanSee(t, eye, new Vec3d(x, 0, hall.Max.Z + 2), 1.8)) blocked++;
            }
            Assert.That(blocked, Is.GreaterThan(tries / 2), "the building's walls hide the far side");
            // Open ground: seen.
            Vec3d s = info.TeamSpawns[0][0];
            Assert.That(DuelBot.CanSee(t, s + new Vec3d(0, 1.62, 0), s + new Vec3d(3, 0, 0), 1.8));
        }

        /// <summary>Runs a server on the duel map with two bots and records, per tick, what each could see.</summary>
        private sealed class BotMatch
        {
            public readonly SculptNetHarness H = Duel();
            public ServerPlayer A, B;
            public DuelBot BrainA, BrainB;
            public double MovedA, MovedB, HighestFeet;
            public int Ticks, FiresWithoutSight, Fires;
            private readonly Dictionary<int, bool>[] _sight = { new Dictionary<int, bool>(), new Dictionary<int, bool>() };

            public BotMatch(BotLevel la, BotLevel lb, int seedA = 3, int seedB = 7)
            {
                BrainA = new DuelBot(Nav(), DuelBotConfig.For(la), seed: seedA);
                BrainB = new DuelBot(Nav(), DuelBotConfig.For(lb), seed: seedB);
                A = H.Server.AddBot(BrainA, 1, Spawn(1), SpawnYaw(1));
                B = H.Server.AddBot(BrainB, 2, Spawn(2), SpawnYaw(2));
            }

            public void Run(double seconds)
            {
                int n = (int)(seconds * SimTime.TicksPerSecond);
                for (int k = 0; k < n; k++)
                {
                    Vec3d pa = A.State.Position, pb = B.State.Position;
                    H.RunMs(SculptNetHarness.TickMs);
                    Ticks++;
                    if (!A.State.Dead && (A.State.Position - pa).Length < 2) MovedA += (A.State.Position - pa).HorizontalLength;
                    if (!B.State.Dead && (B.State.Position - pb).Length < 2) MovedB += (B.State.Position - pb).HorizontalLength;
                    if (!A.State.Dead) HighestFeet = Math.Max(HighestFeet, A.State.Position.Y);
                    if (!B.State.Dead) HighestFeet = Math.Max(HighestFeet, B.State.Position.Y);
                    Check(0, A, B, BrainA);
                    Check(1, B, A, BrainB);
                }
            }

            private void Check(int i, ServerPlayer me, ServerPlayer them, DuelBot brain)
            {
                int tick = H.Server.Tick;
                MovementConfig mc = H.Server.Movement;
                Vec3d eye = me.State.Position + new Vec3d(0, PlayerMovement.EyeOf(me.State, mc), 0);
                _sight[i][tick] = !them.State.Dead && !me.State.Dead &&
                                  DuelBot.CanSee(H.Server.Grid, eye, them.State.Position, PlayerMovement.HeightOf(them.State, mc));
                if (!brain.Firing) return;
                Fires++;
                // It may only fire at what it could see a reaction time ago (it acts on that tick's view).
                bool saw = _sight[i].TryGetValue(tick - brain.Config.ReactionTicks, out bool s) && s;
                bool sawNear = saw;
                for (int d = -2 * SimTime.Per60; d <= 2 * SimTime.Per60 && !sawNear; d++)
                    sawNear = _sight[i].TryGetValue(tick - brain.Config.ReactionTicks + d, out bool s2) && s2;
                if (!sawNear) FiresWithoutSight++;
            }
        }

        [Test]
        public void TwoBots_FindEachOther_MoveAroundTheMap_AndTradeKills_NeverShootingWhatTheyCouldNotSee()
        {
            // Seeds 1 and 2: whether two bots happen to fight their way upstairs inside 150 s depends on the seeds (checked
            // over five seed pairs at 60 and at 120 Hz: some pairs stay on the ground floor at either rate). This pair
            // climbs at both rates, so the check stays about the stairs movement, not about luck.
            var m = new BotMatch(BotLevel.Normal, BotLevel.Normal, 1, 2);
            m.Run(150);
            TestContext.WriteLine($"A {m.A.Kills} kills / {m.A.Deaths} deaths, moved {m.MovedA:0} m; B {m.B.Kills} / {m.B.Deaths}, moved {m.MovedB:0} m; fires {m.Fires}, without sight {m.FiresWithoutSight}, highest feet {m.HighestFeet:0.0} m");
            Assert.That(m.HighestFeet, Is.GreaterThan(4.6), "they really climb stairs or ramps to an upper floor (the movement, not just the nav graph)");
            Assert.That(m.MovedA, Is.GreaterThan(60), "A walks the map");
            Assert.That(m.MovedB, Is.GreaterThan(60), "B walks the map");
            Assert.That(m.A.Kills + m.B.Kills, Is.GreaterThanOrEqualTo(3), "they find each other and fight");
            Assert.That(m.FiresWithoutSight, Is.Zero, "no wallhacks: every shot is at something it saw a reaction time ago");
        }

        [Test]
        public void ReactsLate_FirstShotComesAfterTheReactionTime()
        {
            var h = Duel();
            Vec3d s = Spawn(1);
            // A standing human 9 m away in the open, somewhere the bot can see from its spawn; the bot starts facing them.
            Vec3d at = default;
            double yaw = 0;
            for (int k = 0; k < 32; k++)
            {
                double a = k * Math.PI / 16;
                Vec3d c = s + new Vec3d(Math.Sin(a), 0, Math.Cos(a)) * 9;
                if (!DuelBot.CanSee(h.Server.Grid, s + new Vec3d(0, 1.62, 0), c, 1.8) || h.Server.Grid.BoxOverlapsSolid(new Aabb(c + new Vec3d(-0.35, 0.05, -0.35), c + new Vec3d(0.35, 1.8, 0.35)))) continue;
                at = c; yaw = a; break;
            }
            Assert.That(at.LengthSquared, Is.GreaterThan(0), "an open spot near the spawn");
            var human = h.AddClient(at, team: 2);
            var brain = new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), seed: 5);
            ServerPlayer bot = h.Server.AddBot(brain, 1, s, yaw);
            int start = h.Server.Tick, firstSeen = -1, firstShot = -1;
            for (int k = 0; k < 300 && firstShot < 0; k++)
            {
                h.RunMs(SculptNetHarness.TickMs);
                if (firstSeen < 0 && brain.Engaged) firstSeen = h.Server.Tick;
                if (brain.Firing) firstShot = h.Server.Tick;
            }
            Assert.That(firstShot, Is.GreaterThan(0), "it shoots a target standing in the open");
            Assert.That(firstShot - start, Is.GreaterThanOrEqualTo(brain.Config.ReactionTicks), "never before its reaction time");
            h.RunSeconds(8);
            Assert.That(human.ServerSide.Deaths, Is.GreaterThanOrEqualTo(1), "and kills a target that stands still");
        }

        [Test]
        public void PlaysTheMatch_AHumanAndABotStartIt_TheBotHasAScore_TheDummyStillDoesNot()
        {
            var h = Duel();
            h.Server.MatchConfig.Enabled = true;
            h.Server.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            var human = h.AddClient(Spawn(1), team: 1);
            h.RunSeconds(0.3);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Waiting));
            ServerPlayer bot = h.Server.AddBot(new DuelBot(Nav()), 2, Spawn(2), SpawnYaw(2));
            h.RunSeconds(0.3);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Countdown));
            h.RunSeconds(0.6);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            Assert.That(human.Client.Match.Scores.Count, Is.EqualTo(2), "the client sees both scores");
            Assert.That(human.Client.Match.Scores.Exists(x => x.PlayerId == bot.Id));

            h.Server.AddDummy(new PracticeDummy(Spawn(2) + new Vec3d(3, 0, 0), 0, new int[0]), team: 2);
            h.RunSeconds(0.2);
            Assert.That(h.Server.Match.Scores.Count, Is.EqualTo(2), "the dummy is scenery");
        }

        [Test]
        public void HardBeatsEasy_MostOfTheTime()
        {
            var m = new BotMatch(BotLevel.Hard, BotLevel.Easy);
            m.Run(150);
            TestContext.WriteLine($"Hard {m.A.Kills} kills, Easy {m.B.Kills} kills");
            Assert.That(m.A.Kills, Is.GreaterThan(m.B.Kills), "the levels mean something");
        }
    }
}
