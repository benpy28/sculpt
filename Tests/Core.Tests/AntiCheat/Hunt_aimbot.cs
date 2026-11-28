using System;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.AntiCheat
{
    /// <summary>
    /// Red-team hunt (aimbot / triggerbot): the server can't stop a client aiming (aim is legit input), so the only
    /// defence is DETECTION via the aim stats AimSnap and FastReaction (NetServer.AntiCheat.cs). These tests drive a
    /// real NetServer with bot command streams a hacked client could produce, and assert which flag fires.
    ///
    /// Findings proven here:
    ///   1. AimSnap is only ever evaluated on the exact tick a shot lands a hit. An aimbot that snaps then fires a few
    ///      ticks later (or slews over >4 ticks) lands every shot and NEVER flags.  -> evasion (high)
    ///   2. A legit hard flick-and-hold flags AimSnap.                               -> false positive (medium)
    ///   3. A triggerbot that waits past 90 ms, or shoots an already-visible target, never flags FastReaction. -> evasion
    ///   4. A legit held-angle pre-aim (enemy steps out, you fire) flags FastReaction. -> false positive (high)
    /// </summary>
    public class Hunt_aimbot
    {
        // ---------------------------------------------------------------- helpers

        private static ITerrain OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private static NetServer Server(ITerrain t, Action<NetConfig> tweak = null)
        {
            var nc = new NetConfig();
            tweak?.Invoke(nc);
            return new NetServer(t, new MovementConfig(), nc, (c, d, r) => { });
        }

        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        private static InputCmd Cmd(NetServer s, double yaw = 0) =>
            new InputCmd { Tick = s.Tick, ViewTick = s.Tick, Move = new MoveInput { Yaw = yaw } };

        private static double YawTo(Vec3d from, Vec3d to) => Math.Atan2(to.X - from.X, to.Z - from.Z);

        /// <summary>yaw and pitch (degrees) that point from <paramref name="eye"/> at <paramref name="point"/> (matches WeaponRules.AimDir).</summary>
        private static (double yaw, double pitch) AimAt(Vec3d eye, Vec3d point)
        {
            Vec3d d = point - eye;
            double len = d.Length;
            double yaw = Math.Atan2(d.X, d.Z);
            double pitch = len < 1e-6 ? 0 : -Math.Asin(Math.Max(-1, Math.Min(1, d.Y / len))) * 180 / Math.PI;
            return (yaw, pitch);
        }

        private static Vec3d Chest(ServerPlayer p) => p.State.Position + new Vec3d(0, 0.9, 0);

        // ================================================================ AimSnap

        /// <summary>
        /// BASELINE: the naive snap aimbot the detector was built for -- aim away, snap 40 deg onto the target and fire
        /// on the SAME tick, then freeze -- is correctly flagged. (Confirms the detector works at all.)
        /// </summary>
        [Test]
        public void Baseline_SnapAndFireOnTheSameTick_IsFlaggedAimSnap()
        {
            NetServer s = Server(OpenField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            const int every = 240;
            s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    int phase = sv.Tick % every;
                    bool fire = phase == every - 30;
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    // off target until the fire tick, then snap dead on and fire; stays on target (frozen) after.
                    double y = phase < every - 30 ? yaw + 40 * Math.PI / 180 : yaw;
                    var c = Cmd(sv, y);
                    c.Pitch = (float)pitch;
                    c.Move.FirePressed = fire;
                    c.Move.FireHeld = fire;
                    return c;
                },
            }, 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            Assert.That(s.Flags.StatsOf(1).Hits, Is.GreaterThan(0), "the naive aimbot lands hits");
            Assert.That(s.Flags.Count(FlagKind.AimSnap), Is.GreaterThan(0), "and is flagged");
        }

        /// <summary>
        /// FINDING 1 (evasion, high): the SAME instant 40 deg snap, but the trigger is pulled 6 ticks (50 ms) AFTER the
        /// snap settles. AimSnap is only ever checked on the tick a hit lands (NetServer.AntiCheat.cs NoteHit sets
        /// SnapPending only when a shot hits), so the snap is never seen. The bot still lands every shot. ZERO flags.
        /// </summary>
        [Test]
        public void Aimbot_SnapsThenFiresAfewTicksLater_LandsHits_NeverFlagsAimSnap()
        {
            NetServer s = Server(OpenField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            const int every = 240;
            s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    int phase = sv.Tick % every;
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    // off target for the first part of the cycle; at phase 0 it has ALREADY snapped on; fire at phase 6.
                    bool onTarget = phase < 30;        // snapped on and holding for 30 ticks
                    bool fire = phase == 6;            // ...but fire only once the snap is 6 ticks in the past
                    double y = onTarget ? yaw : yaw + 50 * Math.PI / 180;
                    var c = Cmd(sv, y);
                    c.Pitch = (float)pitch;
                    c.Move.FirePressed = fire;
                    c.Move.FireHeld = fire;
                    return c;
                },
            }, 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            TestContext.WriteLine(s.Flags.Report());
            Assert.That(s.Flags.StatsOf(1).Hits, Is.GreaterThan(3), "the humanized aimbot lands its shots");
            Assert.That(s.Flags.Count(FlagKind.AimSnap), Is.Zero, "but AimSnap never fires: the snap was not on a hit tick");
        }

        /// <summary>
        /// FINDING 1b (evasion): a smooth aimbot that slews 50 deg onto the target over 10 ticks (83 ms) and fires once
        /// settled. The 4-tick (SnapTicks) window ending on the hit never contains a 25 deg turn, so no snap. Hits land.
        /// </summary>
        [Test]
        public void Aimbot_SlewsOverManyTicks_LandsHits_NeverFlagsAimSnap()
        {
            NetServer s = Server(OpenField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            const int every = 240, slew = 10;
            s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    int phase = sv.Tick % every;
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    double off = 50 * Math.PI / 180;
                    double y;
                    if (phase < slew) y = yaw + off * (slew - phase) / slew;  // slew from +50 deg to 0 over 10 ticks
                    else if (phase < 40) y = yaw;                            // on target
                    else y = yaw + off;                                      // look away again
                    var c = Cmd(sv, y);
                    c.Pitch = (float)pitch;
                    c.Move.FirePressed = phase == 20;
                    c.Move.FireHeld = phase == 20;
                    return c;
                },
            }, 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            Assert.That(s.Flags.StatsOf(1).Hits, Is.GreaterThan(3));
            Assert.That(s.Flags.Count(FlagKind.AimSnap), Is.Zero);
        }

        /// <summary>
        /// FINDING 2 (false positive, medium): a legit pro flick. The player looks away, flicks 30 deg onto the target
        /// over 4 ticks (33 ms, a normal hard flick), fires on arrival, and holds the mouse still to shoot (0 deg the
        /// next tick). That is exactly the AimSnap signature, so an honest flick-heavy player is flagged.
        /// </summary>
        [Test]
        public void LegitHardFlickAndHold_FalselyFlagsAimSnap()
        {
            NetServer s = Server(OpenField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            const int every = 240, flick = 4;
            s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    int phase = sv.Tick % every;
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    double off = 30 * Math.PI / 180;
                    double y;
                    int start = every - 30;                 // the flick starts here
                    if (phase < start) y = yaw + off;       // resting, looking off target
                    else if (phase < start + flick) y = yaw + off * (start + flick - phase) / flick; // flick in over 4 ticks
                    else y = yaw;                           // landed; mouse held still to fire
                    var c = Cmd(sv, y);
                    c.Pitch = (float)pitch;
                    bool fire = phase == start + flick;     // fire the tick the flick lands
                    c.Move.FirePressed = fire;
                    c.Move.FireHeld = fire;
                    return c;
                },
            }, 1, new Vec3d(20, 0, 20), 0);
            target = s.AddBot(new Brain { F = (sv, me) => Cmd(sv) }, 2, new Vec3d(20, 0, 28), 0);
            for (int t = 0; t < 10 * SimTime.TicksPerSecond; t++) s.Step();
            TestContext.WriteLine(s.Flags.Report());
            Assert.That(s.Flags.StatsOf(1).Hits, Is.GreaterThan(0));
            Assert.That(s.Flags.Count(FlagKind.AimSnap), Is.GreaterThan(0),
                "a legit flick-and-hold is indistinguishable from an aimbot snap by this metric");
        }

        // ================================================================ FastReaction

        /// <summary>A field with a short solid wall so one player can hide behind it and step out.</summary>
        private static ITerrain WallField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            int zc = (int)Math.Round(9.0 / SubGrid.SubSize);           // wall plane at world z = 9
            int x0 = (int)Math.Round(2.0 / SubGrid.SubSize);
            int x1 = (int)Math.Round(8.0 / SubGrid.SubSize);
            for (int x = x0; x <= x1; x++)
                for (int y = 0; y < 8; y++)                            // up to ~2.4 m tall
                    g.Set(x, y, zc, SubId.Anchor);
            g.ClearChanges();
            return g;
        }

        private static readonly Vec3d ShooterPos = new Vec3d(6, 0, 6);
        private static readonly Vec3d TargetHide = new Vec3d(6, 0, 12);

        /// <summary>Hides behind the wall for a second, then strafes out into the open (world +x).</summary>
        private static Brain Emerger(int stepOutTick) => new Brain
        {
            F = (sv, me) =>
            {
                var c = Cmd(sv, Math.PI / 2);                 // face +x
                if (sv.Tick >= stepOutTick) c.Move.MoveZ = 1; // forward = +x, strafe into the open
                return c;
            },
        };

        private static bool ShooterCanSee(NetServer s, ServerPlayer shooter, ServerPlayer target)
        {
            Vec3d eye = shooter.State.Position + new Vec3d(0, PlayerMovement.EyeOf(shooter.State, s.Movement), 0);
            return DuelBot.CanSee(s.Grid, eye, target.State.Position, PlayerMovement.HeightOf(target.State, s.Movement));
        }

        /// <summary>
        /// FINDING 4 (false positive, high): a legit held-angle pre-aim. The shooter holds its crosshair on the spot
        /// where an enemy (hidden &gt; 0.3 s behind a wall) will appear, and fires the instant it can see them. That is
        /// the core tac-shooter skill of holding an angle -- and it flags FastReaction (a first-shot hit on someone who
        /// came into sight &lt; 90 ms ago after being hidden 0.3 s).
        /// </summary>
        [Test]
        public void LegitPreAimHoldingAnAngle_FalselyFlagsFastReaction()
        {
            NetServer s = Server(WallField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            ServerPlayer shooter = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    var c = Cmd(sv, yaw);
                    c.Pitch = (float)pitch;
                    bool see = ShooterCanSee(sv, me, target);
                    c.Move.FirePressed = see;              // pre-aimed: fire the instant the enemy is visible
                    c.Move.FireHeld = see;
                    return c;
                },
            }, 1, ShooterPos, YawTo(ShooterPos, TargetHide));
            target = s.AddBot(Emerger(120), 2, TargetHide, 0);
            for (int t = 0; t < 6 * SimTime.TicksPerSecond; t++) s.Step();
            TestContext.WriteLine(s.Flags.Report());
            Assert.That(s.Flags.StatsOf(shooter.Id).Hits, Is.GreaterThan(0), "the pre-aimed shot lands");
            Assert.That(s.Flags.Count(FlagKind.FastReaction), Is.GreaterThan(0),
                "holding an angle is flagged as a superhuman reaction");
        }

        /// <summary>
        /// FINDING 3 (evasion): a triggerbot that waits ~110 ms (13 ticks) after the enemy is visible before firing
        /// evades FastReaction entirely (the threshold is 90 ms / 11 ticks), while still reacting far faster than a
        /// human (human floor ~150-200 ms). Same wall scene, same lethal first shot, ZERO FastReaction flags.
        /// </summary>
        [Test]
        public void Triggerbot_WaitsPast90ms_StillSuperhuman_NeverFlagsFastReaction()
        {
            NetServer s = Server(WallField(), nc => nc.AntiCheat.Interest.Enabled = false);
            ServerPlayer target = null;
            int seenFor = 0;
            ServerPlayer shooter = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    (double yaw, double pitch) = AimAt(me.State.Position + new Vec3d(0, 1.62, 0), Chest(target));
                    var c = Cmd(sv, yaw);
                    c.Pitch = (float)pitch;
                    seenFor = ShooterCanSee(sv, me, target) ? seenFor + 1 : 0;
                    bool fire = seenFor >= 13;             // wait 13 ticks (~108 ms) past the 90 ms window, then fire
                    c.Move.FirePressed = fire;
                    c.Move.FireHeld = fire;
                    return c;
                },
            }, 1, ShooterPos, YawTo(ShooterPos, TargetHide));
            target = s.AddBot(Emerger(120), 2, TargetHide, 0);
            for (int t = 0; t < 6 * SimTime.TicksPerSecond; t++) s.Step();
            TestContext.WriteLine(s.Flags.Report());
            Assert.That(s.Flags.StatsOf(shooter.Id).Hits, Is.GreaterThan(0), "it still lands the shot");
            Assert.That(s.Flags.Count(FlagKind.FastReaction), Is.Zero, "but is never flagged as a fast reaction");
        }
    }
}
