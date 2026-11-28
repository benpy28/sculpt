using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// Battle royale bot tactics (DuelBot.Br.cs): picking fights, breaking off bad ones, cover that really hides, hearing
    /// other people's fights, and rotating with the circle in time.
    /// </summary>
    public class BrBotTacticsTests
    {
        private static BotNav _nav;
        private static BotNav Nav() => _nav ??= BrMap.BuildNav(1.0);
        private static NetServer _plain;
        private static BreachMapInfo _info;

        /// <summary>A server on the battle royale map with no match (no circle): for the decision helpers.</summary>
        private static NetServer Plain()
        {
            if (_plain != null) return _plain;
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out _info, out _);
            _plain = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            TileWalls.Find(_plain.Grid)?.Prepare();
            return _plain;
        }

        private static DuelBot Bot(double aggression, int seed = 3)
        {
            var b = new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), seed);
            b.Aggression = aggression;
            return b;
        }

        private static MotorState With(int health, bool ar, int bandages)
        {
            var s = new MotorState();
            s.Damage = new WeaponsConfig().MaxHealth - health;
            var bag = new Bag { S0 = Item.Pump, S1 = ar ? Item.Ar : Item.Smg };
            if (bandages > 0) { bag[4] = Item.Bandage; bag.SetCount(4, bandages); }
            s.Hand.Bag = bag;
            return s;
        }

        private static readonly double[] Temperaments = { 0, 0.5, 1 };

        // ------------------------------------------------------------------ picking fights

        [Test]
        public void ItPicksFights_ByRangeHealthAndGuns()
        {
            NetServer s = Plain();
            ServerPlayer me = s.AddPlayer(9001, 1, new Vec3d(0, 0, 0), 0);
            foreach (double a in Temperaments)
            {
                DuelBot b = Bot(a);
                string who = $"aggression {a}";
                Assert.That(b.TakeFight(s, me, With(150, true, 0), 15, 0, 1000), who + ": healthy with an AR, 15 m: fights");
                Assert.That(b.TakeFight(s, me, With(150, true, 0), 4, 0, 1000), who + ": an enemy right on top of it: always fights");
                Assert.That(b.TakeFight(s, me, With(30, true, 3), 4, 0, 1000), who + ": even hurt, at point blank");
                Assert.That(b.TakeFight(s, me, With(150, true, 0), 60, 0, 1000), Is.False, who + ": 60 m is too far to start a fight");
                Assert.That(b.TakeFight(s, me, With(150, false, 0), 30, 0, 1000), Is.False, who + ": pump and SMG only: no 30 m fight");
                Assert.That(b.TakeFight(s, me, With(60, true, 3), 15, 0, 1000), Is.False, who + ": hurt with bandages: heals before a fight");
            }
            // Careful players take shorter fights than pushy ones.
            Assert.That(Bot(0).TakeFight(s, me, With(150, true, 0), 25, 0, 1000), Is.False, "careful: 25 m is too far");
            Assert.That(Bot(1).TakeFight(s, me, With(150, true, 0), 32, 0, 1000), "pushy: takes a 32 m fight");
            Assert.That(Bot(1).TakeFight(s, me, With(150, true, 0), 25, 0, 1000), "pushy: takes a 25 m fight");
        }

        [Test]
        public void ItTakesAFightWhenHurt_IfItsHitMarkerSaysTheEnemyIsWorse()
        {
            NetServer s = Plain();
            ServerPlayer me = s.AddPlayer(9002, 1, new Vec3d(0, 0, 0), 0);
            ServerPlayer them = s.AddPlayer(9003, 2, new Vec3d(10, 0, 0), 0);
            DuelBot b = Bot(0.5);
            MotorState hurt = With(60, true, 3); // 40%
            Assert.That(b.TakeFight(s, me, hurt, 15, them.Id, 1000), Is.False, "their health unknown: heal first");
            me.HitVictim = them.Id;
            me.HitTick = 900;
            me.HitVictimHealth = 30; // 20%: what the hit marker said
            Assert.That(b.TakeFight(s, me, hurt, 15, them.Id, 1000), "they are worse off: go");
            Assert.That(b.TakeFight(s, me, hurt, 15, them.Id, 900 + 16 * SimTime.TicksPerSecond), Is.False, "an old hit marker says nothing");
        }

        [Test]
        public void ItBreaksOffALosingFight_ButFinishesAWinningOne()
        {
            NetServer s = Plain();
            ServerPlayer me = s.AddPlayer(9004, 1, new Vec3d(0, 0, 0), 0);
            ServerPlayer them = s.AddPlayer(9005, 2, new Vec3d(10, 0, 0), 0);
            foreach (double a in Temperaments)
            {
                DuelBot b = Bot(a);
                string who = $"aggression {a}";
                Assert.That(b.ShouldRetreat(s, me, With(40, true, 3), 15, them.Id, 1000), who + ": 27% health with bandages against a healthy enemy: cover");
                Assert.That(b.ShouldRetreat(s, me, With(150, true, 3), 15, them.Id, 1000), Is.False, who + ": healthy: keeps fighting");
                Assert.That(b.ShouldRetreat(s, me, With(40, true, 3), 3, them.Id, 1000), Is.False, who + ": point blank: running is death");
                me.HitVictim = them.Id;
                me.HitTick = 990;
                me.HitVictimHealth = 20;
                Assert.That(b.ShouldRetreat(s, me, With(40, true, 3), 15, them.Id, 1000), Is.False, who + ": they are nearly down: finish it");
                me.HitVictim = 0;
            }
            // Careful players leave sooner.
            Assert.That(Bot(0).ShouldRetreat(s, me, With(70, true, 3), 15, them.Id, 1000), "careful: leaves at 47%");
            Assert.That(Bot(1).ShouldRetreat(s, me, With(70, true, 3), 15, them.Id, 1000), Is.False, "pushy: stays at 47%");
        }

        // ------------------------------------------------------------------ cover

        [Test]
        public void CoverItFinds_HidesHeadAndChestFromTheThreat_AndIsReachableOnFoot()
        {
            NetServer s = Plain();
            BotNav nav = Nav();
            var rng = new Random(11);
            int tried = 0, found = 0;
            var bad = new List<string>();
            for (int k = 0; k < 4000 && tried < 300; k++)
            {
                BotNav.Node n = nav.Nodes[rng.Next(nav.Nodes.Count)];
                if (n.Open < 8) continue;
                Vec3d me = n.Feet(nav);
                double ang = rng.NextDouble() * Math.PI * 2, dist = 12 + rng.NextDouble() * 25;
                int tn = nav.Nearest(new Vec3d(me.X + Math.Sin(ang) * dist, me.Y, me.Z + Math.Cos(ang) * dist), 3);
                if (tn < 0) continue;
                Vec3d threat = nav.Feet(tn);
                Vec3d eye = threat + new Vec3d(0, 1.6, 0);
                if (!DuelBot.CanSee(s.Grid, eye, me, 1.8)) continue; // only cases where it is in the open
                tried++;
                DuelBot b = Bot(0.5, k);
                if (!b.FindCover(s, me, threat, out Vec3d spot)) continue;
                found++;
                if (DuelBot.CanSee(s.Grid, eye, spot, 1.65) && !DuelBot.Hidden(s, eye, spot)) bad.Add($"spot {spot} for {me} is seen from {threat}");
                foreach (double up in new[] { 1.5, 0.9 })
                {
                    Vec3d p = spot + new Vec3d(0, up, 0), d = p - eye;
                    TerrainHit hit = s.Grid.Raycast(eye, d.Normalized, d.Length);
                    if (!hit.Hit || hit.Distance >= d.Length - 0.05) bad.Add($"spot {spot}: a clear line to {up} m from {threat}");
                }
                if (!nav.SameArea(nav.Nearest(me), nav.Nearest(spot))) bad.Add($"spot {spot} can't be walked to from {me}");
                if ((spot - me).HorizontalLength > 17) // rings out to 14 m, snapped to the walking grid bad.Add($"spot {spot} is {(spot - me).HorizontalLength:0} m away");
                if ((spot - threat).HorizontalLength < 7) bad.Add($"spot {spot} is on top of the threat");
            }
            TestContext.WriteLine($"{tried} bots in the open, cover found for {found}");
            Assert.That(bad, Is.Empty, string.Join("\n", bad.GetRange(0, Math.Min(10, bad.Count))));
            Assert.That(tried, Is.GreaterThan(100));
            Assert.That(found, Is.GreaterThan(tried / 3), "a village map has cover near most open spots");
        }

        // ------------------------------------------------------------------ hearing fights

        [Test]
        public void ItHearsAFight_BetweenTwoOtherPlayers_AReactionTimeLate()
        {
            NetServer s = Plain();
            DuelBot b = Bot(0.5);
            var me = new Vec3d(100, 0, 100);
            int t = 10000;
            b.NoteGunfire(1, new Vec3d(160, 0, 100), t);
            b.NoteGunfire(2, new Vec3d(170, 0, 110), t + 30);
            Assert.That(b.HeardFight(s, me, t + 31, out _), Is.False, "not yet: it reacts a reaction time late");
            Assert.That(b.HeardFight(s, me, t + 30 + b.Config.ReactionTicks, out Vec3d mid), "two people shooting 14 m apart, 66 m off");
            Assert.That((mid - new Vec3d(165, 0, 105)).Length, Is.LessThan(0.01));
            Assert.That(b.HeardFight(s, me, t + 5 * SimTime.TicksPerSecond, out _), Is.False, "the shooting stopped 4 s ago");

            DuelBot c = Bot(0.5);
            c.NoteGunfire(1, new Vec3d(160, 0, 100), t);
            c.NoteGunfire(1, new Vec3d(161, 0, 100), t + 80);
            Assert.That(c.HeardFight(s, me, t + 100, out _), Is.False, "one person shooting is not a fight");
            c.NoteGunfire(3, new Vec3d(160, 0, 200), t + 80);
            Assert.That(c.HeardFight(s, me, t + 100, out _), Is.False, "two people shooting 100 m apart are not one fight");
            c.NoteGunfire(4, new Vec3d(102, 0, 100), t + 80);
            c.NoteGunfire(5, new Vec3d(104, 0, 104), t + 80);
            Assert.That(c.HeardFight(s, me, t + 100, out _), Is.False, "a fight right next to it is its own fight, not one to walk to");
        }

        // ------------------------------------------------------------------ the circle

        private sealed class Idle : IBotBrain
        {
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me) => new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        [TestCase(0.0)]
        [TestCase(1.0)]
        public void ItRotatesIntoTheNextCircle_InTime_ButNotTheMomentItIsShown(double aggression)
        {
            DuelBot brain = null;
            var cfg = new BrConfig
            {
                Mode = BrMode.Solos,
                Seed = 4,
                FillWithoutHumans = true,
                CountdownTicks = 60 * SimTime.Per60,
                MakeBot = i =>
                {
                    if (i != 0) return new Idle();
                    brain = new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 77);
                    brain.Aggression = aggression;
                    return brain;
                },
            };
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            var s = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            TileWalls.Find(s.Grid)?.Prepare();
            s.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(s, info, br);
            for (int i = 0; i < 40 * SimTime.TicksPerSecond && s.Match.Phase != MatchPhase.Playing; i++) s.Step();
            Assert.That(s.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            ServerPlayer bot = null;
            foreach (var kv in s.Bots) if (kv.Value == brain) bot = s.FindPlayer(kv.Key);
            Assert.That(bot, Is.Not.Null);

            // Everyone else stands far away (no fights to get in the way), and the next circle is drawn 110 m from the
            // bot, toward the map's middle, shrinking from 120 s to 160 s.
            Vec3d at = bot.State.Position;
            Vec3d toMid = (br.Centre - at).WithY(0).Normalized;
            int far = Nav().Nearest(at + toMid * 420, 20);
            Assert.That(far, Is.GreaterThanOrEqualTo(0));
            foreach (ServerPlayer p in s.Players)
                if (p != bot) { p.State.Position = Nav().Feet(far) + new Vec3d(0, 0.1, 0); p.State.Velocity = Vec3d.Zero; }
            Vec3d c0 = at + toMid * 110;
            int now = s.Tick;
            s.Match.Storm = new StormState
            {
                Phase = 0, PhaseCount = 7,
                FromX = (float)br.Centre.X, FromZ = (float)br.Centre.Z, FromRadius = 600,
                ToX = (float)c0.X, ToZ = (float)c0.Z, ToRadius = 50,
                ShrinkStart = now + 120 * SimTime.TicksPerSecond, ShrinkEnd = now + 160 * SimTime.TicksPerSecond, Dps = 1,
            };
            int rotatedAt = -1;
            bool everOutside = false;
            for (int i = 0; i < 162 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                if (rotatedAt < 0 && brain.RotateCount > 0) rotatedAt = (s.Tick - now) / SimTime.TicksPerSecond;
                if (!bot.State.Dead && s.InStorm(bot.State.Position)) everOutside = true;
            }
            double end = (bot.State.Position - new Vec3d(c0.X, bot.State.Position.Y, c0.Z)).HorizontalLength;
            TestContext.WriteLine($"aggression {aggression}: rotated {rotatedAt} s after the circle was shown; ends {end:0} m from the next centre");
            Assert.That(bot.State.Dead, Is.False);
            if (rotatedAt >= 0) Assert.That(rotatedAt, Is.GreaterThan(40), "it doesn't run the moment the circle shows (2 minutes before the shrink)");
            // (A pushy bot may never need to: roaming already drifts it toward the next circle's middle.)
            Assert.That(everOutside, Is.False, "it was never caught by the circle");
            Assert.That(end, Is.LessThan(50), "it ends inside the next circle");
        }
    }
}
