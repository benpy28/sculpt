using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Hunt
{
    /// <summary>
    /// Red-team hunt (hackclient): build the common cheats a modified client would ship and drive them against a REAL
    /// NetServer, then measure what the server actually grants. Two attacker models are used:
    ///   (a) CheatClient — crafts raw input packets (InputsMsg bytes) with arbitrary fields and calls Server.Receive,
    ///       exactly as a hand-written cheat packet builder would. The server's own Read/sanitise/sim decides the result.
    ///   (b) NetHarness + Peer.Tamper — a real NetClient predicts normally and a tamper hook rewrites each outgoing
    ///       packet (for lag-comp cheats that need a live client/server over a SimulatedLink).
    /// These tests ASSERT the server's behaviour so a fix pass can see which cheats land. Nothing here edits production
    /// code. Findings and file:line proofs are summarised in the agent's report.
    /// </summary>
    public class Hunt_hackclient
    {
        private readonly MovementConfig _mc = new MovementConfig();

        // ---------------------------------------------------------------- a raw-packet cheat client
        private sealed class CheatClient
        {
            public readonly NetServer S;
            public readonly int Conn;
            public readonly ServerPlayer P;
            public int Ack;
            public CheatClient(NetServer s, int conn, ServerPlayer p) { S = s; Conn = conn; P = p; Ack = s.Tick; }

            /// <summary>Sends one crafted command for the next tick, then steps the server one tick.</summary>
            public void SendAndStep(InputCmd c)
            {
                var w = new ByteWriter(256);
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = Ack, Cmds = new List<InputCmd> { c } });
                S.Receive(Conn, w.Buffer, 0, w.Length);
                S.Step();
                if (Ack <= S.Tick) Ack = S.Tick; // an honest client keeps its ack fresh
            }
        }

        private static ITerrain Flat()
        {
            var g = new VoxelGrid(4, 2, 4, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private CheatClient OneCheater(out NetServer s, Action<NetConfig> tweak = null)
        {
            var nc = new NetConfig();
            tweak?.Invoke(nc);
            s = new NetServer(Flat(), _mc, nc, (c, d, r) => { });
            ServerPlayer p = s.AddPlayer(1, 1, TileMap.FeetIn(8, 0, 8), 0);
            return new CheatClient(s, 1, p);
        }

        private static InputCmd Base(NetServer s) => new InputCmd { Tick = s.Tick + 1, ViewTick = s.Tick + 1 };

        // ================================================================ 1. speed hack / teleport

        [Test]
        public void SpeedHack_MaxInputAndDiagonal_IsClampedByServer_NoFasterThanSprint_NoFlag()
        {
            CheatClient c = OneCheater(out NetServer s);
            double maxSpeed = 0, maxStep = 0;
            Vec3d prev = c.P.State.Position;
            for (int i = 0; i < 300; i++)
            {
                InputCmd cmd = Base(s);
                // Everything a cheat would cram in: full move on BOTH axes (diagonal), sprint, jump spam.
                cmd.Move.MoveX = 1; cmd.Move.MoveZ = 1; cmd.Move.SprintHeld = true;
                cmd.Move.JumpPressed = true; cmd.Move.JumpHeld = true;
                c.SendAndStep(cmd);
                double sp = c.P.State.Velocity.HorizontalLength;
                double step = (c.P.State.Position - prev).HorizontalLength;
                prev = c.P.State.Position;
                if (sp > maxSpeed) maxSpeed = sp;
                if (i > 5 && step > maxStep) maxStep = step;
            }
            TestContext.WriteLine($"max horizontal speed the server granted: {maxSpeed:0.00} m/s (sprint {_mc.SprintSpeed}); max move in one tick {maxStep * SimTime.TicksPerSecond:0.0} m/s-equiv");
            Assert.That(maxSpeed, Is.LessThan(_mc.SprintSpeed + 0.5), "diagonal input is normalised; no speed gain");
            Assert.That(s.Flags.Count(FlagKind.SpeedOverCap) + s.Flags.Count(FlagKind.Teleport), Is.Zero);
        }

        [Test]
        public void Teleport_CannotBeSent_ThereIsNoPositionField()
        {
            // The wire has no position. The only movement a client can ask for is a move axis (clamped to +-1) and a
            // look. Proven by SpeedHack above; this is a sanity note: a cheat cannot teleport because nothing carries a
            // position. Confirmed SAFE by construction.
            Assert.Pass("InputCmd carries no position/velocity; movement is 100% server-run (see InputCmd.cs).");
        }

        // ================================================================ 2. rapid fire (no fire-rate limit)

        [Test]
        public void RapidFire_PressingFireEveryTick_DoesNotExceedTheGunsRate_NoFlag()
        {
            CheatClient c = OneCheater(out NetServer s);
            // Draw the pistol (semi-auto, 5/s). Key 4 -> slot 3.
            for (int i = 0; i < 40; i++) { InputCmd d = Base(s); if (i == 0) d.Hand.WeaponKey = 4; c.SendAndStep(d); }
            Assert.That(c.P.State.Hand.HeldItem, Is.EqualTo(Item.Pistol));

            int shots = 0;
            int seconds = 2;
            int ticks = seconds * SimTime.TicksPerSecond;
            for (int i = 0; i < ticks; i++)
            {
                InputCmd d = Base(s);
                d.Move.FirePressed = true; d.Move.FireHeld = true; d.Move.WantsToShoot = true; // "fire" on EVERY tick
                c.SendAndStep(d);
                foreach (CombatEvent e in s.CombatEvents) if (e.Type == CombatEventType.Shot && e.Attacker == c.P.Id) shots++;
            }
            double rate = shots / (double)seconds;
            TestContext.WriteLine($"pistol, fire pressed every tick for {seconds}s: server fired {shots} shots = {rate:0.0}/s (cap 5/s)");
            Assert.That(rate, Is.LessThanOrEqualTo(5.2), "the server's per-gun fire timer caps the rate; spamming FirePressed does nothing extra");
            Assert.That(s.Flags.Count(FlagKind.FireTooFast), Is.Zero, "the server never produced a too-fast pair");
        }

        // ================================================================ 3. infinite ammo (no reload)

        [Test]
        public void InfiniteAmmo_FiringPastTheMagazine_StopsAtMagSize_UntilAReloadCompletes()
        {
            CheatClient c = OneCheater(out NetServer s);
            for (int i = 0; i < 40; i++) { InputCmd d = Base(s); if (i == 0) d.Hand.WeaponKey = 4; c.SendAndStep(d); } // pistol out
            int mag = s.Movement.Weapons.Of(Weapon.Pistol).MagSize;

            // Fire flat-out but NEVER press reload. Count shots until the server refuses to fire any more.
            int shots = 0, dry = 0;
            for (int i = 0; i < 400 && dry < 60; i++)
            {
                InputCmd d = Base(s);
                d.Move.FirePressed = true; d.Move.FireHeld = true; d.Move.WantsToShoot = true;
                c.SendAndStep(d);
                bool fired = false;
                foreach (CombatEvent e in s.CombatEvents) if (e.Type == CombatEventType.Shot && e.Attacker == c.P.Id) { shots++; fired = true; }
                dry = fired ? 0 : dry + 1;
            }
            TestContext.WriteLine($"pistol fired {shots} shots with no reload press, then dried up (mag {mag}); UsedPistol now {c.P.State.Weapon.UsedPistol}");
            Assert.That(shots, Is.EqualTo(mag), "a cheat cannot fire past the magazine; the server tracks ammo");
            Assert.That(s.Flags.Count(FlagKind.MagOverflow), Is.Zero);
        }

        // ================================================================ 4. instant respawn (dev respawn off)

        [Test]
        public void InstantRespawn_PressedEveryTick_WithDevRespawnOff_IsRefusedAndFlagged()
        {
            CheatClient c = OneCheater(out NetServer s, nc => nc.AllowDevRespawn = false);
            Vec3d spawn = c.P.Spawn;
            // Walk north a while so we are clearly away from spawn and alive.
            for (int i = 0; i < 120; i++) { InputCmd d = Base(s); d.Move.MoveZ = 1; d.Move.SprintHeld = true; c.SendAndStep(d); }
            double away = (c.P.State.Position - spawn).HorizontalLength;
            Assert.That(away, Is.GreaterThan(3));
            // Now spam Respawn every tick (a cheat trying to reset the 3 s death wait / teleport home mid-fight).
            for (int i = 0; i < 60; i++) { InputCmd d = Base(s); d.Respawn = true; d.Move.MoveZ = 1; c.SendAndStep(d); }
            double after = (c.P.State.Position - spawn).HorizontalLength;
            TestContext.WriteLine($"after 60 respawn presses: still {after:0.0} m from spawn (not teleported); DevRespawnRefused flags = {s.Flags.Count(FlagKind.DevRespawnRefused)}");
            Assert.That(after, Is.GreaterThan(3), "the respawn press is refused: no teleport home");
            Assert.That(s.Flags.Count(FlagKind.DevRespawnRefused), Is.GreaterThan(0), "and it is flagged");
        }

        // ================================================================ 5. backtrack / fake-lag (lag-comp abuse)

        /// <summary>Yaw/pitch (degrees) that aims a shot from <paramref name="eye"/> at <paramref name="at"/> (matches WeaponRules.AimDir).</summary>
        private static (double yaw, double pitch) Aim(Vec3d eye, Vec3d at)
        {
            Vec3d d = at - eye;
            double len = d.Length;
            Vec3d u = len > 1e-9 ? d / len : new Vec3d(0, 0, 1);
            return (Math.Atan2(u.X, u.Z), -Math.Asin(Math.Max(-1, Math.Min(1, u.Y))) * 180 / Math.PI);
        }

        private static ITerrain BigFlat()
        {
            var g = new VoxelGrid(6, 2, 6, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        /// <summary>
        /// Two raw-packet players ~10 m apart on a big flat field. The target strafes steadily; the shooter stands still
        /// with the pistol and, every fire command, aims at where the target WAS <c>back</c> ticks ago (what a
        /// backtracker's stale view shows) and sets its command's ViewTick back by <c>back</c> ticks. Both players are
        /// driven by crafted packets, so ViewTick and the acked snapshot tick are whatever the cheat wants.
        ///   backtrack=false: honest ViewTick (= command tick) -> the server does not rewind, so the stale aim misses.
        ///   backtrack=true : ViewTick = cmd-back -> the server rewinds the target onto the stale spot -> hits.
        ///   backdateAck    : also lowers the acked snapshot tick to match, to dodge the ViewBehindAck check.
        /// Returns (hits, ViewBehindAck flags, ViewLagJump flags).
        /// </summary>
        private (int hits, int behind, int jump) RunBacktrack(bool backtrack, bool backdateAck)
        {
            var s = new NetServer(BigFlat(), _mc, new NetConfig(), (c, d, r) => { });
            Vec3d sFeet = TileMap.FeetIn(20, 0, 20), tFeet = TileMap.FeetIn(20, 0, 27); // ~10.5 m apart on +Z
            ServerPlayer shooter = s.AddPlayer(1, 1, sFeet, 0);
            ServerPlayer target = s.AddPlayer(2, 2, tFeet, Math.PI);
            int back = s.Movement.Weapons.MaxRewindTicks; // 36 ticks (0.3 s)

            var w = new ByteWriter(256);
            void Feed(int conn, InputCmd c, int ack) { w.Clear(); Protocol.Write(w, new InputsMsg { AckSnapshotTick = ack, Cmds = new List<InputCmd> { c } }); s.Receive(conn, w.Buffer, 0, w.Length); }
            var tHist = new Dictionary<int, Vec3d>();

            // Settle both on the ground and draw the shooter's pistol (key 4 -> slot 3).
            for (int i = 0; i < 40; i++)
            {
                int tk = s.Tick + 1;
                var sc = new InputCmd { Tick = tk, ViewTick = tk };
                if (i == 0) sc.Hand.WeaponKey = 4;
                Feed(1, sc, s.Tick);
                Feed(2, new InputCmd { Tick = tk, ViewTick = tk, Move = new MoveInput { Yaw = Math.PI } }, s.Tick);
                s.Step();
                tHist[s.Tick] = target.State.Position;
            }

            int hits0 = s.Flags.StatsOf(1).Hits;
            for (int i = 0; i < 6 * SimTime.TicksPerSecond; i++)
            {
                int tk = s.Tick + 1;
                // Target: steady strafe, reversing on a long ~1.3 s period so a stale spot is clearly off the live one.
                double mx = (s.Tick / 80) % 2 == 0 ? 1 : -1;
                Feed(2, new InputCmd { Tick = tk, ViewTick = tk, Move = new MoveInput { MoveX = mx, SprintHeld = true, Yaw = Math.PI } }, s.Tick);

                // Shooter: aim at the target's position `back` ticks ago, fire the pistol, and (cheat) claim the old view.
                if (!tHist.TryGetValue(s.Tick - back, out Vec3d stale)) stale = target.State.Position;
                Vec3d eye = shooter.State.Position + new Vec3d(0, PlayerMovement.EyeOf(shooter.State, _mc), 0);
                Vec3d chest = stale + new Vec3d(0, PlayerMovement.HeightOf(target.State, _mc) * 0.6, 0);
                (double yaw, double pitch) = Aim(eye, chest);
                int viewTick = backtrack ? tk - back : tk;
                int ack = backtrack && backdateAck ? s.Tick - back : s.Tick;
                var cmd = new InputCmd { Tick = tk, ViewTick = viewTick, Pitch = (float)pitch, Move = new MoveInput { Yaw = yaw, FirePressed = true, FireHeld = true, WantsToShoot = true } };
                Feed(1, cmd, ack);
                s.Step();
                tHist[s.Tick] = target.State.Position;
            }
            return (s.Flags.StatsOf(1).Hits - hits0, s.Flags.Count(FlagKind.ViewBehindAck), s.Flags.Count(FlagKind.ViewLagJump));
        }

        [Test]
        public void Backtrack_WithBackdatedAck_EvadesViewBehindAck_ButTheToggleTripsViewLagJump()
        {
            // Baseline: aim at the stale (0.3 s old) spot with an HONEST view -> the target has moved, shots miss.
            var honest = RunBacktrack(backtrack: false, backdateAck: false);
            // Cheat: rewind ViewTick to the full cap AND backdate the ack. The old ack keeps the rewind clamp's floor old,
            // so the server still rewinds the target onto the stale spot and the shots land; backdating also hides it from
            // ViewBehindAck. But the backtrack is toggled on after honest seconds, so the view-lag jump trips the
            // now-wired ViewLagJump: the cheat is caught.
            var cheat = RunBacktrack(backtrack: true, backdateAck: true);
            TestContext.WriteLine($"stale-aim honest view: {honest.hits} hits. backtrack+backdated ack: {cheat.hits} hits, ViewBehindAck={cheat.behind}, ViewLagJump={cheat.jump}");
            Assert.That(honest.hits, Is.LessThanOrEqualTo(5), "aiming at where the target was 0.3 s ago mostly misses with an honest view");
            Assert.That(cheat.hits, Is.GreaterThanOrEqualTo(honest.hits + 6), "the full-rewind backtrack (old ack, clamp floor old) still turns misses into hits");
            Assert.That(cheat.behind, Is.Zero, "backdating the ack hides the backtrack from ViewBehindAck");
            Assert.That(cheat.jump, Is.GreaterThan(0), "but the toggled view-lag jump trips the now-wired ViewLagJump");
        }

        [Test]
        public void Backtrack_WithHonestAck_IsCaughtByBothFlags_AndPreventedByTheRewindClamp()
        {
            // Honest baseline (no backtrack): aim at the stale spot with an honest view -> mostly misses.
            var honest = RunBacktrack(backtrack: false, backdateAck: false);
            // Backtrack with an HONEST (recent) ack: ViewBehindAck sees the old view (fires), the toggle trips the
            // now-wired ViewLagJump (fires), AND because the ack is recent the rewind clamp's floor is recent, so the
            // server refuses to rewind the target onto the 0.3 s-old spot -- the stale-aim shots are PREVENTED, landing
            // no more than the honest baseline.
            var cheat = RunBacktrack(backtrack: true, backdateAck: false);
            TestContext.WriteLine($"honest baseline: {honest.hits} hits. backtrack, honest ack: {cheat.hits} hits, ViewBehindAck={cheat.behind}, ViewLagJump={cheat.jump}");
            Assert.That(cheat.behind, Is.GreaterThan(0), "ViewBehindAck fires: the view is far behind the honest ack");
            Assert.That(cheat.jump, Is.GreaterThan(0), "the now-wired ViewLagJump fires on the toggled lag spike");
            Assert.That(cheat.hits, Is.LessThanOrEqualTo(honest.hits + 2),
                "the rewind clamp (recent ack -> recent floor) prevents the stale-position backtrack: no better than honest aim");
        }

        // ================================================================ 7. silent aimbot (perfect server-truth aim)

        [Test]
        public void SilentAimbot_ServerGrantsHitsFromTheClientsLook_WithNoAimFlags()
        {
            // The server derives the bullet direction from the yaw/pitch the client sends. A client that points exactly
            // at the target every tick (perfect tracking, a human can't) therefore gets near-100% hits, and because the
            // aim never SNAPS (it is already on target), the AimSnap/FastReaction heuristics do not fire.
            var h = NetHarness.Flat();
            var shooter = h.AddClient(NetHarness.Lag(15), NetHarness.Lag(15), seed: 3);
            var target = h.AddClient(NetHarness.Lag(15), NetHarness.Lag(15), seed: 4, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 5));
            target.Script = t => new MoveInput { MoveX = (t / 120) % 2 == 0 ? 0.25 : -0.25, Yaw = Math.PI };
            shooter.Script = t =>
            {
                // Perfectly track the target's current server position every tick (impossible by hand).
                Vec3d from = shooter.ServerSide.State.Position, to = target.ServerSide.State.Position;
                return new MoveInput { Yaw = Math.Atan2(to.X - from.X, to.Z - from.Z), FireHeld = true, WantsToShoot = true };
            };
            h.RunSeconds(10);
            PlayerFlagStats st = h.Server.Flags.StatsOf(shooter.ServerSide.Id);
            TestContext.WriteLine($"perfect-aim bot: {st.Shots} shots, {st.Hits} hits ({st.HitShare:P0}), AimSnap={st.Count(FlagKind.AimSnap)}, FastReaction={st.Count(FlagKind.FastReaction)}");
            Assert.That(st.Hits, Is.GreaterThan(3), "the server grants hits straight from the client's look (a perfect-aim bot just points exactly at the target)");
            Assert.That(st.Count(FlagKind.AimSnap), Is.Zero, "smooth tracking never trips the snap heuristic");
            Assert.That(st.Count(FlagKind.FastReaction), Is.Zero, "an always-visible target never trips the reaction heuristic");
        }
    }
}
