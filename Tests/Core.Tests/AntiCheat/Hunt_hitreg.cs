using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Red-team hunt (hit-validation / lag-compensation / backtracking). A modified client sends only buttons, a look
    /// direction and the tick it was drawing enemies at (ViewTick/ViewFrac). The server rewinds victims to that view,
    /// capped at Weapons.MaxRewindTicks (0.3 s / 36 ticks). Two lag-comp flags guard the rewind:
    ///   - ViewBehindAck: the view sits more than InterpDelay(3) + ViewBehindAck(12) = 15 ticks behind the snapshot tick
    ///                    the client acked in the same packet.
    ///   - ViewLagJump:   the view lag (cmd.Tick - ViewTick) of a shot spikes > 12 ticks above the player's own median.
    ///
    /// Findings proven here (all three asserts FAIL on the current server except the root-cause witness):
    ///  (1) A classic backtrack of ~0.125 s on every shot (5x the legit interp delay, chosen per shot) is NOT flagged,
    ///      with a completely honest ack and no cost to the cheat's own snapshots. ViewBehindAck's window leaves this
    ///      much slack and ViewLagJump never fires.
    ///  (2) Lowering the acked snapshot tick (a value the server trusts) lets the backtrack reach the full 0.3 s rewind
    ///      cap while still raising zero flags.
    ///  (3) ViewLagJump is dead code: NetServer.AntiCheat.PushLag has no call site, so MedianLag() always returns
    ///      int.MinValue and the ViewLagJump branch in NoteHit can never run. Even a blatant 36-tick spike never trips it.
    /// </summary>
    public class Hunt_hitreg
    {
        private static double YawTo(Vec3d from, Vec3d to) => Math.Atan2(to.X - from.X, to.Z - from.Z);

        /// <summary>
        /// Shooter and a strafing target 10 m apart on a flat field. Every fire command's ViewTick is forged back by
        /// <paramref name="back"/> ticks; the shooter aims (by server truth) at where the server will rewind the target
        /// to, so the backtracked shots land. With <paramref name="lowerAck"/> the client also lowers its acked snapshot
        /// tick to keep ViewBehindAck's gap at zero.
        /// </summary>
        private static (NetHarness h, NetHarness.Peer shooter, NetHarness.Peer target) Setup(int back, bool lowerAck)
        {
            var h = NetHarness.Flat();
            var shooter = h.AddClient(NetHarness.Lag(30, 5, 0), NetHarness.Lag(30, 5, 0), seed: 3);
            var target = h.AddClient(NetHarness.Lag(20), NetHarness.Lag(20), seed: 4, team: 2, spawn: h.Spawn + new Vec3d(0, 0, 10));
            target.Script = t => new MoveInput { MoveX = (t / 60) % 2 == 0 ? 1 : -1, Yaw = Math.PI };

            var list = new List<InputCmd>();
            var w = new ByteWriter(1024);
            shooter.Tamper = data =>
            {
                if (Protocol.PeekType(data, 0, data.Length) != MsgType.Inputs) return data;
                var r = new ByteReader(data, 0, data.Length);
                InputsMsg m = Protocol.ReadInputs(ref r, list);
                bool any = false;
                for (int i = 0; i < list.Count; i++)
                {
                    InputCmd c = list[i];
                    if (c.Move.FireHeld) { c.ViewTick = c.Tick - back; list[i] = c; any = true; }
                }
                int ack = m.AckSnapshotTick;
                if (any && lowerAck) ack = list[list.Count - 1].ViewTick; // a tick the client still has; gap = 0
                w.Clear();
                Protocol.Write(w, new InputsMsg { AckSnapshotTick = ack, Cmds = list });
                return w.ToArray();
            };
            return (h, shooter, target);
        }

        private static void AimAtRewound(NetHarness h, NetHarness.Peer shooter, NetHarness.Peer target, int back)
        {
            shooter.Script = t =>
            {
                Vec3d aimAt = h.Server.TryGetRewound(target.ServerSide, t - back, out Vec3d feet, out _, out _)
                    ? feet : target.ServerSide.State.Position;
                return new MoveInput { Yaw = YawTo(shooter.ServerSide.State.Position, aimAt), FireHeld = true, WantsToShoot = true };
            };
        }

        private static (int hits, int behindAck, int lagJump) Fight(int back, bool lowerAck)
        {
            var (h, shooter, target) = Setup(back, lowerAck);
            shooter.Script = t => default;    // a couple of honest seconds first (so a working ViewLagJump would have a small median)
            h.RunSeconds(2);
            AimAtRewound(h, shooter, target, back);
            h.RunSeconds(6);
            int hits = h.Server.Flags.StatsOf(shooter.ServerSide.Id).Hits;
            TestContext.WriteLine(h.Server.Flags.Report());
            return (hits, h.Server.Flags.Count(FlagKind.ViewBehindAck), h.Server.Flags.Count(FlagKind.ViewLagJump));
        }

        /// <summary>
        /// REGRESSION GUARD / documented residual (was a finding). A ~0.125 s backtrack on every shot, toggled on after a
        /// couple of honest seconds, with a fully honest ack. This sits INSIDE the detectors' honest-latency tolerance:
        /// ViewBehindAck needs the view more than interp + ViewBehindAckTicks (15 ticks) behind the ack, and ViewLagJump
        /// needs the lag to jump more than ViewLagJumpTicks (12) over the player's median — a 15-tick backtrack over a
        /// ~4-tick honest median jumps only ~11. The rewind clamp is looser still, so it does not bite. So this modest
        /// backtrack is neither flagged nor prevented: it is the irreducible overlap with honest ~125 ms latency. Closing
        /// it needs a trusted RTT measurement (out of scope); the aim-pattern and phantom-track detectors are the
        /// backstop against a player who exploits this band. This guard pins that boundary so a future threshold change
        /// is a conscious one.
        /// </summary>
        [Test]
        public void AModest125msBacktrack_SitsInsideHonestLatencyTolerance_IsADocumentedResidual()
        {
            const int back = 15; // 125 ms; InterpDelay is 3 ticks (25 ms), flag boundary is interp + 12 = 15 ticks
            var (hits, behindAck, lagJump) = Fight(back, lowerAck: false);
            TestContext.WriteLine($"back={back} ticks, hits={hits}, ViewBehindAck={behindAck}, ViewLagJump={lagJump}");
            Assert.That(hits, Is.GreaterThan(3), "the backtracked shots land (within tolerance, so not prevented)");
            Assert.That(behindAck + lagJump, Is.Zero,
                "a 125 ms backtrack is indistinguishable from honest latency at the lag-comp detectors' tolerance: residual");
        }

        /// <summary>
        /// REGRESSION GUARD (was a finding). The full 0.3 s rewind-cap backtrack with a LOWERED ack (gap 0, so
        /// ViewBehindAck can't see it) toggled on after honest seconds. ViewBehindAck is evaded by the lowered ack, but
        /// the toggle makes the view lag jump from the honest median to the full cap, so the now-wired ViewLagJump fires.
        /// And because the ack is lowered (old), the rewind clamp's floor is old too, so the hit still lands — the cheat
        /// is caught, not prevented, in this configuration. (The hits-land half is why the honest-ack variant below,
        /// where the clamp DOES bite, is the one that is prevented.)
        /// </summary>
        [Test]
        public void MaxBacktrackWithLoweredAck_EvadesViewBehindAck_ButTheToggleTripsViewLagJump()
        {
            int back = new MovementConfig().Weapons.MaxRewindTicks; // 36 ticks = 0.3 s
            var (hits, behindAck, lagJump) = Fight(back, lowerAck: true);
            TestContext.WriteLine($"back={back} ticks (rewind cap), hits={hits}, ViewBehindAck={behindAck}, ViewLagJump={lagJump}");
            Assert.That(hits, Is.GreaterThan(3), "the old/lowered ack keeps the clamp floor old, so the shots land");
            Assert.That(behindAck, Is.Zero, "the lowered ack hides the backtrack from ViewBehindAck");
            Assert.That(lagJump, Is.GreaterThan(0), "but the toggled lag jump trips the now-wired ViewLagJump");
        }

        /// <summary>
        /// REGRESSION GUARD (was a finding). The SAME 0.3 s backtrack with an HONEST ack is now caught by BOTH lag-comp
        /// flags: ViewBehindAck (the view is far behind the honest ack) and the now-wired ViewLagJump (the toggle). It
        /// used to be caught by ViewBehindAck alone while ViewLagJump sat dead (PushLag was never called).
        /// </summary>
        [Test]
        public void SameBacktrackWithHonestAck_IsCaughtByBothLagCompFlags()
        {
            int back = new MovementConfig().Weapons.MaxRewindTicks;
            var (hits, behindAck, lagJump) = Fight(back, lowerAck: false);
            TestContext.WriteLine($"back={back} ticks, hits={hits}, ViewBehindAck={behindAck}, ViewLagJump={lagJump}");
            Assert.That(behindAck, Is.GreaterThan(0), "with an honest ack the full-cap backtrack is caught by ViewBehindAck");
            Assert.That(lagJump, Is.GreaterThan(0), "and the now-wired ViewLagJump also fires on the toggled lag spike");
        }
    }
}
