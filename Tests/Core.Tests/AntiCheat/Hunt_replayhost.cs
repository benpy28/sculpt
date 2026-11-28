using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Tests.Hunt
{
    /// <summary>
    /// Red-team hunt (replay / spectator / listen-server host). Drives a real NetServer + a spectator NetClient the way
    /// a cheat client would, and asserts what full-state data the server actually hands out, plus the limits of the
    /// replay verifier used for review. See the handback report for the host-trust code-path writeup.
    /// </summary>
    public class Hunt_Replayhost
    {
        private static BotNav _nav;
        private static BotNav Nav() => _nav ??= DuelMap.BuildNav();

        private static NetServer NewDuelServer(out BreachMapInfo info, NetServer.SendFn send)
        {
            SubGrid g = DuelMap.BuildGround();
            var s = new NetServer(DuelMap.Terrain(g, DuelMap.BuildBrushes(out info)), new MovementConfig(), new NetConfig(), send);
            s.Sculpt = new SculptSystem(g, new SculptConfig());
            return s;
        }

        private static void SpawnItems(NetServer s, BreachMapInfo info)
        {
            foreach (GroundItem gi in info.ItemSpawns) s.SpawnItem(gi.Kind, gi.Count, gi.Position);
        }

        private static NetClient NewDuelClient()
        {
            SubGrid g = DuelMap.BuildGround();
            var mc = new MovementConfig();
            return new NetClient(DuelMap.Terrain(g, DuelMap.BuildBrushes(out _)), mc, new NetConfig(), (d, r) => { }, new SculptClient(g, new SculptConfig(), mc));
        }

        // =====================================================================================================
        // REGRESSION GUARD 1 (was a high finding): the spectator stream is live full state of EVERY player with zero
        // interest management; the only thing keeping it from being a live wallhack is the delay. A host could set
        // -specdelay 0 and nothing floored it, so a colluding/second spectator connection got live ESP of everyone.
        // Fixed: a non-dedicated (listen/home) server floors the EFFECTIVE spectator delay at MinSpectatorDelayTicks
        // (30 s), enforced server-side where the delay is consumed (SpectatorWelcome + StepSpectators), so a modified
        // build that skips the command-line clamp can't get a live feed. A dedicated server (ranked) sets its own policy.
        // =====================================================================================================
        [Test]
        public void SpectatorDelay_IsFlooredServerSide_OnANonDedicatedHost_EvenWhenSetToZero()
        {
            var links = new Dictionary<int, Action<byte[], bool>>();
            NetServer server = NewDuelServer(out BreachMapInfo info, (c, d, r) => { if (links.TryGetValue(c, out var l)) l(d, r); });
            SpawnItems(server, info);
            server.MatchConfig.Enabled = true;

            ServerPlayer a = server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 5), 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
            ServerPlayer b = server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 4), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);

            // The cheat asks for a zero delay on a normal (non-dedicated) host.
            server.SpectatorDelayTicks = 0;
            Assert.That(server.Dedicated, Is.False, "a listen/home server is not dedicated");
            Assert.That(server.EffectiveSpectatorDelayTicks, Is.EqualTo(server.MinSpectatorDelayTicks),
                "the effective delay is floored to the server-side minimum, not the host's 0");
            Assert.That(server.MinSpectatorDelayTicks, Is.GreaterThanOrEqualTo(30 * SimTime.TicksPerSecond), "the floor is at least 30 s");

            NetClient spec = NewDuelClient();
            links[2] = (d, r) => spec.Receive(d, 0, d.Length);
            server.AddSpectator(2);

            // Over a short match (well under the 30 s floor) the spectator never syncs: there is no delayed state to send.
            bool specEverSawBoth = false;
            for (int t = 0; t < 40; t++)
            {
                server.Step();
                spec.UpdateVisual(1.0 / SimTime.TicksPerSecond);
                if (spec.SpectatorSynced && spec.SpectatedIds.Count == 2) specEverSawBoth = true;
            }

            Assert.That(specEverSawBoth, Is.False,
                "with the floor applied, a near-live spectator gets no full-state feed of the players");
            Assert.That(spec.Playing, Is.False, "the spectator connection is not a player and costs no player slot");

            // A dedicated server honours an explicit 0 (ranked/official servers set their own trusted policy).
            var ded = NewDuelServer(out _, (c, d, r) => { });
            ded.Dedicated = true;
            ded.SpectatorDelayTicks = 0;
            Assert.That(ded.EffectiveSpectatorDelayTicks, Is.Zero, "a dedicated server is trusted to set its own delay");
        }

        // A sanity companion: the spectator stream's per-player record carries health/ammo/bag, which the normal
        // player snapshot (PlayerSnap, see NetServer.SendSnapshots) simply does not have fields for. So even a
        // DELAYED spectator feed is a strictly richer leak than anything an opponent is ever sent.
        [Test]
        public void SpectatorSnapshot_CarriesEnemyHealthAmmoAndBag_WhichPlayerSnapshotsNeverDo()
        {
            var p = new SpecPlayer { Id = 1, Team = 1, Position = new Vec3d(1, 2, 3), Health = 73, Ammo = 9, Bar = 4210 };
            for (int k = 0; k < Items.Slots; k++) { p.Bag[k] = (Item)(k % 3); p.Bag.SetCount(k, k); }
            var enc = new SpecSnapCodec();
            byte[] key = enc.Encode(0, true, new List<SpecPlayer> { p });
            var dec = new SpecSnapCodec();
            var r = new ByteReader(key, 0, key.Length);
            var into = new List<SpecPlayer>();
            dec.Decode(ref r, into, out _);
            Assert.That(into[0].Health, Is.EqualTo(73), "spectator wire carries exact enemy health");
            Assert.That(into[0].Ammo, Is.EqualTo(9), "and magazine ammo");
            Assert.That(into[0].Bar, Is.EqualTo(4210), "and the heal/sculpt bar");
            // PlayerSnap (what opponents get) has no such fields: see Assets/Scripts/Core/Net (SnapshotCodec /
            // NetServer.SendSnapshots builds PlayerSnap with Position/Velocity/Yaw/Pitch/Mode/Crouched/Dead/Held/Hand only).
        }

        // =====================================================================================================
        // REGRESSION GUARD 3 (was a medium finding): the ReplayVerifier only re-derives (and can only prove/refute)
        // simulation flags (Integrity/Interest/Aim). Input and Network flags -- including the lag-comp / backtracking
        // detectors ViewBehindAck and ViewLagJump -- are NOT reproduced; they live only in the recorded footer, which
        // on a listen server the (possibly cheating) host writes. So `Passed` means "consistent", never "clean". That
        // re-run limitation is unchanged (and documented here); the fix is that eligibility no longer keys off Passed:
        // ReplayCheck now exposes FlagsLogged (every flag of either kind) and a Clean property (Passed AND zero flags),
        // so review/eligibility code can't mistake a flag-filled but consistent match for a clean one.
        // =====================================================================================================
        [Test]
        public void Verifier_ExposesCleanNotJustPassed_AndStillCannotReproduceInputOrNetworkFlags()
        {
            // The lag-comp / backtracking detectors are Network category -> the re-run never regenerates them.
            Assert.That(FlagLog.FromSimulation(FlagKind.ViewBehindAck), Is.False, "backtracking-view flag is unverifiable by re-run");
            Assert.That(FlagLog.FromSimulation(FlagKind.ViewLagJump), Is.False, "backtracking flag is unverifiable by re-run");
            Assert.That(FlagLog.FromSimulation(FlagKind.ClockAhead), Is.False);
            Assert.That(FlagLog.FromSimulation(FlagKind.Malformed), Is.False);
            Assert.That(FlagLog.FromSimulation(FlagKind.BadView), Is.False, "input flags are unverifiable by re-run");
            Assert.That(FlagLog.FromSimulation(FlagKind.DevRespawnRefused), Is.False);
            // Only these are reproduced from inputs:
            Assert.That(FlagLog.FromSimulation(FlagKind.SpeedOverCap), Is.True);
            Assert.That(FlagLog.FromSimulation(FlagKind.HitUnseen), Is.True);
            Assert.That(FlagLog.FromSimulation(FlagKind.AimSnap), Is.True);

            // An honest recorded match verifies as Passed. `Passed` has no "zero flags" term (ReplayCheck.Passed =
            // Problem == null && FirstMismatch < 0 && HashesChecked > 0 && FlagsMatch): a match full of logged Aim /
            // backtracking flags still returns Passed == true, so a reviewer must read the flags, not trust Passed.
            byte[] file = RecordHonestMatch(12);
            ReplayFile f = ReplayFile.Read(file);
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                NetServer s = NewDuelServer(out BreachMapInfo info, (c, d, r) => { });
                SpawnItems(s, info);
                s.MatchConfig.Enabled = true;
                s.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
                return s;
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Problem, Is.Null, "the honest match re-runs");
            Assert.That(check.Passed, "verifier passes; note this proves consistency, not absence of a cheat");

            // The fix: Clean is the eligibility gate, Passed is only consistency. Clean <=> consistent AND no flag at all.
            Assert.That(check.Clean, Is.EqualTo(check.Passed && check.FlagsLogged == 0),
                "Clean = Passed AND zero flags logged, so a flag-filled consistent match is NOT clean");
            int allFlagsInFile = 0, simFlagsInFile = 0;
            foreach (Flag fl in f.Flags) { allFlagsInFile++; if (FlagLog.FromSimulation(fl.Kind)) simFlagsInFile++; }
            Assert.That(check.FlagsLogged, Is.EqualTo(allFlagsInFile), "FlagsLogged counts every flag of either category");
            // The verifier still only RE-DERIVES simulation flags: the Network/Input half of the footer is host-written.
            Assert.That(check.FlagsChecked, Is.EqualTo(simFlagsInFile));

            // A synthetic consistent-but-flagged record is Passed yet NOT Clean: the exact reviewer trap the fix closes.
            var flagged = new ReplayCheck { HashesChecked = 10, FlagsLogged = 3 };
            Assert.That(flagged.Passed, Is.True, "consistent: no problem, no mismatch, flags reproduced");
            Assert.That(flagged.Clean, Is.False, "but NOT clean: three flags were logged");
        }

        private static byte[] RecordHonestMatch(double seconds)
        {
            NetServer server = NewDuelServer(out BreachMapInfo info, (c, d, r) => { });
            SpawnItems(server, info);
            server.MatchConfig.Enabled = true;
            server.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
            var ms = new MemoryStream();
            MatchStream stream = server.StartStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "Duel", Mode = "test", Build = "tests", StartedUtcTicks = 1 }, stream);
            server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 5), 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
            server.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 4), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            int n = (int)(seconds * SimTime.TicksPerSecond);
            for (int k = 0; k < n; k++) server.Step();
            writer.Finish();
            return ms.ToArray();
        }
    }
}
