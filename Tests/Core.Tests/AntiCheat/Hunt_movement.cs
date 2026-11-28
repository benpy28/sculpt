using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Hunt
{
    /// <summary>
    /// Red-team hunt (movement / position). A modified client controls only button bits, a look direction and the
    /// view tick; the server simulates all movement, so a client can't send a speed or a position. These tests drive a
    /// real NetServer the way a hacked client would and check what the server actually does.
    ///
    /// Findings and cleared-safe cases are documented in the hunt report. The one FAILING test here
    /// (VoidFall_...) proves a server-side logic hole; the others confirm the authority holds.
    /// </summary>
    public class Hunt_movement
    {
        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private static InputCmd Cmd(NetServer s) => new InputCmd { Tick = s.Tick, ViewTick = s.Tick };

        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        // ============================================================ FIXED: void-fall now books a death

        /// <summary>
        /// REGRESSION GUARD (was a finding): a player whose feet drop below NetConfig.KillY (-30 m) used to be teleported
        /// to spawn at FULL health, instantly, with NO death recorded, because NetSim.Simulate's KillY branch called
        /// Respawn() directly without ever setting Dead, so NetServer's "if (!wasDead &amp;&amp; State.Dead) OnKilled(...)"
        /// never fired. The design says "deaths by your own fall count as deaths". Fixed: the KillY branch now routes
        /// through WeaponRules.Kill (sets Dead + RespawnTick) and emits a Died event, so the server books the death via
        /// OnKilled(DamageSource.Fall) and applies the normal 3 s respawn delay, exactly like a fatal fall onto ground.
        /// </summary>
        [Test]
        public void VoidFall_BooksADeath_NoFreeFullHealthRespawn()
        {
            var s = new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            ServerPlayer p = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 1, new Vec3d(20, 0, 20), 0);
            s.Step(); // let it settle on the ground

            // The player is badly hurt in a fight (100 of 150 damage), then ends up in the void below KillY (on a map
            // with a reachable drop). We force the position; a cheat can't send it, the point is the server's RESPONSE.
            p.State.Damage = 100;
            int deathsBefore = p.Deaths;
            p.State.Position = new Vec3d(20, -40, 20);
            p.Body.Position = new Vec3d(20, -40, 20);
            s.Step();

            // The fall out of the world is now a death: marked dead, waiting out the respawn delay, not a free escape.
            Assert.That(p.State.Dead, Is.True, "a fall out of the world now marks the player dead");
            Assert.That(p.Deaths, Is.EqualTo(deathsBefore + 1),
                "a fall out of the world counts as a death (design: 'deaths by your own fall count as deaths')");
            Assert.That(p.State.RespawnTick, Is.GreaterThan(s.Tick), "the normal respawn delay is applied, not an instant teleport");
        }

        // ============================================================ SAFE: forged movement can't beat the caps

        /// <summary>
        /// A hacked client sending the worst movement spam it can (jump + crouch + sprint every tick, flipping the move
        /// axes, firing) still has every tick simulated by the server. Over 30 s it never approaches the 60 m/s watchdog
        /// cap or legit top speed, and the integrity watchdog (speed / teleport) raises nothing: the server never grants
        /// impossible movement from forged inputs.
        /// </summary>
        [Test]
        public void ForgedAdversarialInputs_NeverExceedLegitSpeed_NoIntegrityFlags()
        {
            var s = new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            double maxSpeed = 0;
            var brain = new Brain
            {
                F = (sv, me) =>
                {
                    double sp = me.State.Velocity.Length;
                    if (sp > maxSpeed) maxSpeed = sp;
                    var c = Cmd(sv);
                    c.Move.MoveZ = 1;
                    c.Move.MoveX = (sv.Tick / 7) % 2 == 0 ? 1 : -1; // whip the wish direction to fish for free speed
                    c.Move.Yaw = (sv.Tick * 0.31) % (2 * Math.PI) - Math.PI;
                    c.Move.SprintHeld = true;
                    c.Move.JumpPressed = sv.Tick % 3 == 0; // bhop spam
                    c.Move.JumpHeld = true;
                    c.Move.CrouchPressed = sv.Tick % 5 == 0; // slide-reboot spam
                    c.Move.CrouchHeld = sv.Tick % 11 < 6;
                    c.Move.FireHeld = true;
                    c.Move.WantsToShoot = true;
                    return c;
                },
            };
            s.AddBot(brain, 1, new Vec3d(20, 0, 20), 0);
            for (int i = 0; i < 30 * SimTime.TicksPerSecond; i++) s.Step();

            TestContext.WriteLine($"peak server speed over 30 s of forged spam: {maxSpeed:0.0} m/s");
            Assert.That(maxSpeed, Is.LessThan(25), "forged inputs can't make the server move the player faster than movement allows");
            Assert.That(s.Flags.Count(FlagKind.SpeedOverCap), Is.Zero);
            Assert.That(s.Flags.Count(FlagKind.Teleport), Is.Zero);
            Assert.That(s.Flags.CountCategory(FlagCategory.Integrity), Is.Zero);
        }

        // ============================================================ SAFE: malformed / extreme wire input

        /// <summary>
        /// A hacked client puts NaN/Inf and out-of-range angles on the wire. The read path drops non-finite angles
        /// (Malformed) and wraps/clamps out-of-range ones (BadView); nothing crashes the server, the player's position
        /// stays finite, and no corrupt value reaches the simulation (no client corrections from the junk).
        /// </summary>
        [Test]
        public void MalformedAndExtremeWireInputs_AreSanitized_NoCrashNoDesync()
        {
            var h = NetHarness.Flat();
            var p = h.AddClient();
            h.RunSeconds(0.5);
            int corrBefore = p.Client.Corrections;

            void Send(MoveInput move, float pitch)
            {
                var c = new InputCmd { Tick = h.Server.Tick + 3, ViewTick = h.Server.Tick, Move = move, Pitch = pitch };
                var w = new ByteWriter(256);
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = h.Server.Tick, Cmds = new List<InputCmd> { c } });
                Assert.DoesNotThrow(() => h.Server.Receive(p.ConnectionId, w.Buffer, 0, w.Length), "a junk packet must never throw out of Receive");
            }

            Send(new MoveInput { Yaw = double.NaN }, 0);                 // non-finite yaw -> dropped (Malformed)
            Send(new MoveInput { Yaw = double.PositiveInfinity }, 0);    // -> dropped
            Send(new MoveInput(), float.NaN);                           // non-finite pitch -> dropped
            Send(new MoveInput { Yaw = 50.0 }, 0);                       // way out of range -> wrapped, BadView
            Send(new MoveInput(), 1e9f);                                // -> clamped, BadView
            Send(new MoveInput { MoveX = 1e9, MoveZ = -1e9 }, 0);        // huge axes -> quantized into [-1,1]

            h.RunSeconds(1.0);
            TestContext.WriteLine(h.Server.Flags.Report());

            Assert.That(h.Server.Flags.Count(FlagKind.Malformed), Is.GreaterThanOrEqualTo(3), "the three non-finite packets were dropped");
            Assert.That(h.Server.Flags.Count(FlagKind.BadView), Is.GreaterThan(0), "the out-of-range angles were sanitized and flagged");
            Vec3d pos = p.ServerSide.State.Position;
            Assert.That(double.IsFinite(pos.X) && double.IsFinite(pos.Y) && double.IsFinite(pos.Z), Is.True,
                "no NaN/Inf ever reached the server's position");
            Assert.That(double.IsFinite(p.ServerSide.State.Velocity.Length), Is.True);
            Assert.That(p.Client.Corrections - corrBefore, Is.LessThan(3),
                "nothing the junk said desynced the client's prediction");
        }
    }
}
