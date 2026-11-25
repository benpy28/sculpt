using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Net
{
    /// <summary>
    /// The 1v1 anti-hiding ping (Ben 2026-11-25): after 10 s with no damage between two enemies, no clear line between
    /// them and not within 10 m, the server shows each to the other through walls for 3 s. Server-authoritative; the
    /// reveal message goes out only when a ping starts.
    /// </summary>
    public class PingTests
    {
        private static BreachMapInfo _info;

        private static Vec3d Spawn(int team)
        {
            if (_info == null) DuelMap.BuildBrushes(out _info);
            return _info.TeamSpawns[team - 1][0];
        }

        private static SculptNetHarness Duel()
        {
            var h = new SculptNetHarness(DuelMap.BuildGround, wrap: g => DuelMap.Terrain(g, DuelMap.BuildBrushes(out _)));
            h.Server.Ping = new PingConfig();
            return h;
        }

        private static SculptNetHarness OpenField()
        {
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            });
            h.Server.Ping = new PingConfig();
            return h;
        }

        private static void Match(NetServer s)
        {
            s.MatchConfig.Enabled = true;
            s.MatchConfig.CountdownTicks = 30 * SimTime.Per60;
            s.MatchConfig.DurationTicks = 120 * 60 * SimTime.Per60;
            s.MatchConfig.ResultsTicks = 60 * SimTime.Per60;
        }

        /// <summary>Runs until the match is being played; returns its first tick.</summary>
        private static int RunToPlaying(SculptNetHarness h)
        {
            for (int k = 0; k < 10 * SimTime.TicksPerSecond && h.Server.Match.Phase != MatchPhase.Playing; k++)
                h.RunMs(SculptNetHarness.TickMs);
            Assert.That(h.Server.Match.Phase, Is.EqualTo(MatchPhase.Playing));
            return h.Server.Match.PhaseStartTick;
        }

        private static void RunUntilTick(SculptNetHarness h, int tick)
        {
            while (h.Server.Tick < tick) h.RunMs(SculptNetHarness.TickMs);
        }

        [Test]
        public void Hiding_IsPingedAfterTenQuietSeconds_BothWays_ForThreeSeconds_AndAgainTenSecondsAfterThat()
        {
            var h = Duel();
            Match(h.Server);
            h.Server.Ping.StopRange = 0; // single reveals (repeats are tested below)
            var a = h.AddClient(Spawn(1), team: 1);
            var b = h.AddClient(Spawn(2), team: 2, seed: 2);
            Assert.That((Spawn(1) - Spawn(2)).Length, Is.GreaterThan(10), "the spawns are far apart");
            int start = RunToPlaying(h);
            int quiet = SimTime.SecondsToTicks(10), reveal = SimTime.SecondsToTicks(3);
            int ida = a.ServerSide.Id, idb = b.ServerSide.Id;

            RunUntilTick(h, start + quiet - 5);
            Assert.That(h.Server.PingsFired, Is.Zero, "nothing before 10 s");
            Assert.That(a.Client.RevealsStarted, Is.Empty, "and nothing about the enemy is sent before it");
            Assert.That(b.Client.RevealsStarted, Is.Empty);
            Assert.That(a.Client.IsRevealed(idb, out _), Is.False);

            RunUntilTick(h, start + quiet + 3);
            Assert.That(h.Server.PingsFired, Is.EqualTo(1), "the ping fires at 10 s");
            Assert.That(h.Server.IsRevealed(ida, idb) && h.Server.IsRevealed(idb, ida), "both are revealed, to each other");
            h.RunSeconds(0.1); // the reliable message's trip
            Assert.That(a.Client.IsRevealed(idb, out RevealEntry ea), "A sees B");
            Assert.That(b.Client.IsRevealed(ida, out RevealEntry eb), "B sees A");
            Assert.That((ea.Position - b.ServerSide.State.Position).Length, Is.LessThan(0.5), "where B really is");
            Assert.That((eb.Position - a.ServerSide.State.Position).Length, Is.LessThan(0.5));
            Assert.That(ea.EndTick - (start + quiet), Is.InRange(reveal - 3, reveal + 3), "for 3 s");
            Assert.That(a.Client.RevealsStarted.Count, Is.EqualTo(1), "one message per ping, not a stream");

            RunUntilTick(h, ea.EndTick + 20);
            Assert.That(h.Server.IsRevealed(ida, idb), Is.False, "over after 3 s");
            Assert.That(a.Client.IsRevealed(idb, out _), Is.False);

            RunUntilTick(h, ea.EndTick + quiet - 5);
            Assert.That(h.Server.PingsFired, Is.EqualTo(1), "the next quiet spell starts when the reveal ends");
            RunUntilTick(h, ea.EndTick + quiet + 3);
            Assert.That(h.Server.PingsFired, Is.EqualTo(2), "and pings again");
        }

        [Test]
        public void OnceFired_ItKeepsPingingEveryThreeSeconds_UntilTheyAreWithin30m()
        {
            var h = Duel();
            Match(h.Server);
            var a = h.AddClient(Spawn(1), team: 1);
            var b = h.AddClient(Spawn(2), team: 2, seed: 2);
            double apart = (Spawn(1) - Spawn(2)).Length;
            Assert.That(apart, Is.GreaterThan(30), "the spawns are more than 30 m apart");
            int start = RunToPlaying(h);
            int quiet = SimTime.SecondsToTicks(10), reveal = SimTime.SecondsToTicks(3);
            int ida = a.ServerSide.Id, idb = b.ServerSide.Id;

            RunUntilTick(h, start + quiet + 3);
            Assert.That(h.Server.PingsFired, Is.EqualTo(1));
            // Still far apart: a fresh reveal each time one ends, with no gap.
            for (int k = 1; k <= 4; k++)
            {
                RunUntilTick(h, start + quiet + k * reveal + 3);
                Assert.That(h.Server.PingsFired, Is.EqualTo(1 + k), "repeats every 3 s");
                Assert.That(h.Server.IsRevealed(ida, idb), "no gap between repeats");
            }
            h.RunSeconds(0.1);
            Assert.That(a.Client.IsRevealed(idb, out _), "the client sees them all along");
            Assert.That(a.Client.RevealsStarted.Count, Is.EqualTo(5), "one message per repeat (each is a ping you hear)");

            // Within the stop range (as if they had walked up): over at once, and the quiet clock starts again.
            h.Server.Ping.StopRange = apart + 1;
            h.RunSeconds(0.1);
            Assert.That(h.Server.IsRevealed(ida, idb), Is.False, "within 30 m: it stops");
            Assert.That(a.Client.IsRevealed(idb, out _), Is.False, "and the clients are told");
            Assert.That(b.Client.IsRevealed(ida, out _), Is.False);
            int fired = h.Server.PingsFired;
            h.Server.Ping.StopRange = 30;
            h.RunSeconds(9.5);
            Assert.That(h.Server.PingsFired, Is.EqualTo(fired), "the next one needs another 10 quiet seconds");
            h.RunSeconds(0.7);
            Assert.That(h.Server.PingsFired, Is.EqualTo(fired + 1));
        }

        [Test]
        public void InTheOpen_ALineOfSightKeepsTheClockAtZero()
        {
            var h = OpenField();
            Match(h.Server);
            h.Server.Ping.ContactRange = 0; // only sight counts here
            h.AddClient(SculptNetHarness.FeetIn(1, 1), team: 1);
            h.AddClient(SculptNetHarness.FeetIn(6, 6), team: 2, seed: 2);
            RunToPlaying(h);
            h.RunSeconds(15);
            Assert.That(h.Server.PingsFired, Is.Zero);
        }

        [Test]
        public void Close_CountsAsContact_EvenThroughWalls()
        {
            var h = Duel();
            Match(h.Server);
            h.Server.Ping.ContactRange = 1000; // everywhere on the map is "close"
            h.AddClient(Spawn(1), team: 1);
            h.AddClient(Spawn(2), team: 2, seed: 2);
            RunToPlaying(h);
            h.RunSeconds(15);
            Assert.That(h.Server.PingsFired, Is.Zero);
        }

        [Test]
        public void NoMatch_OrSwitchedOff_NoPing()
        {
            var h = Duel(); // free play: no match running
            h.AddClient(Spawn(1), team: 1);
            h.AddClient(Spawn(2), team: 2, seed: 2);
            h.RunSeconds(15);
            Assert.That(h.Server.PingsFired, Is.Zero, "only while a match is being played");

            var h2 = Duel();
            Match(h2.Server);
            h2.Server.Ping.Enabled = false;
            h2.AddClient(Spawn(1), team: 1);
            h2.AddClient(Spawn(2), team: 2, seed: 2);
            RunToPlaying(h2);
            h2.RunSeconds(15);
            Assert.That(h2.Server.PingsFired, Is.Zero, "Breach Settings switch");
        }

        [Test]
        public void Teammates_AreNeverPinged()
        {
            var h = Duel();
            Match(h.Server);
            h.Server.MatchConfig.MinPlayers = 2;
            h.AddClient(Spawn(1), team: 1);
            h.AddClient(Spawn(2), team: 1, seed: 2);
            h.Server.MatchConfig.Enabled = true;
            h.RunSeconds(1);
            if (h.Server.Match.Phase == MatchPhase.Playing || h.Server.Match.Phase == MatchPhase.Countdown) h.RunSeconds(15);
            Assert.That(h.Server.PingsFired, Is.Zero);
        }

        [Test]
        public void Protocol_RoundTrips_AndRejectsJunk()
        {
            var w = new ByteWriter();
            var list = new List<RevealEntry> { new RevealEntry { PlayerId = 7, EndTick = 1234, Position = new Vec3d(1.5, 4.8, -3) } };
            RevealProtocol.Write(w, 1000, list);
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<RevealEntry>();
            Assert.That(RevealProtocol.Read(ref r, into), Is.EqualTo(1000));
            Assert.That(into.Count, Is.EqualTo(1));
            Assert.That(into[0].PlayerId, Is.EqualTo(7));
            Assert.That(into[0].EndTick, Is.EqualTo(1234));
            Assert.That((into[0].Position - list[0].Position).Length, Is.LessThan(1e-5));

            w.Clear();
            RevealProtocol.Write(w, 1000, new List<RevealEntry> { new RevealEntry { Position = new Vec3d(double.NaN, 0, 0) } });
            data = w.ToArray();
            Assert.Throws<NetFormatException>(() => { var rr = new ByteReader(data, 0, data.Length); RevealProtocol.Read(ref rr, into); });
            Assert.Throws<NetFormatException>(() => { var rr = new ByteReader(data, 0, data.Length - 3); RevealProtocol.Read(ref rr, into); });
        }

        [Test]
        public void TheBot_HuntsTheRevealedSpot_ALateReactionAfterThePing_WithoutAWallhack()
        {
            var h = Duel();
            Match(h.Server);
            var human = h.AddClient(Spawn(2), team: 2, seed: 2); // stands still at its spawn, behind the divide
            DuelMap.BuildBrushes(out BreachMapInfo info);
            var brain = new DuelBot(DuelMap.BuildNav(), DuelBotConfig.For(BotLevel.Normal), seed: 3);
            ServerPlayer bot = h.Server.AddBot(brain, 1, Spawn(1), info.SpawnYaw[0]);
            // Let it roam, but keep it from ever seeing the human before the ping: a ping is only fired after 10 s
            // without sight anyway, so just find the first ping.
            int start = RunToPlaying(h);
            int pingTick = -1;
            for (int k = 0; k < 60 * SimTime.TicksPerSecond && pingTick < 0; k++)
            {
                h.RunMs(SculptNetHarness.TickMs);
                if (h.Server.IsRevealed(bot.Id, human.ServerSide.Id)) pingTick = h.Server.Tick;
            }
            Assert.That(pingTick, Is.GreaterThan(start), "a ping fires");
            Assert.That(brain.LastPingTick, Is.LessThan(pingTick), "it hasn't reacted on the ping tick itself");

            RunUntilTick(h, pingTick + brain.Config.ReactionTicks + 2);
            Assert.That(brain.LastPingTick, Is.GreaterThanOrEqualTo(pingTick), "it acts on the ping a reaction time late");
            bool sawThen = DuelBot.CanSee(h.Server.Grid, bot.State.Position + new Vec3d(0, 1.62, 0), human.ServerSide.State.Position, 1.8);
            if (!sawThen) Assert.That(brain.Engaged, Is.False, "a ping is not a sighting: nothing to aim at through the wall");

            var path = new List<int>();
            BotNav nav = brain.Nav;
            double before = PathLength(nav, bot.State.Position, human.ServerSide.State.Position, path);
            h.RunSeconds(6);
            double after = PathLength(nav, bot.State.Position, human.ServerSide.State.Position, path);
            TestContext.WriteLine($"walk to the human: {before:0} m at the ping, {after:0} m 6 s later");
            Assert.That(after, Is.LessThan(before - 15), "it heads for where the ping showed them");
        }

        private static double PathLength(BotNav nav, Vec3d from, Vec3d to, List<int> path)
        {
            int a = nav.Nearest(from), b = nav.Nearest(to, 6);
            if (a < 0 || b < 0 || !nav.FindPath(a, b, path)) return (to - from).Length;
            double len = 0;
            Vec3d p = from;
            foreach (int n in path) { Vec3d q = nav.Feet(n); len += (q - p).HorizontalLength; p = q; }
            return len;
        }
    }
}
