using System;
using NUnit.Framework;
using Sculpt.Core.AntiCheat;
using Sculpt.Core.Net;

namespace Sculpt.Core.Tests.Hunt
{
    /// <summary>
    /// The phantom-track honeypot (prototype, 2026-11-26). Interest management proves an honest client is never sent a
    /// hidden enemy's position (see NobodyPopsIn), so it cannot aim at one. The server watches, for every pair where the
    /// enemy is being WITHHELD from the viewer, whether the viewer's aim ray stays locked on that enemy's head while the
    /// bearing to them sweeps (the enemy has lateral motion, so holding the lock means actively following through a
    /// wall). That is a wallhack tell that needs no shot. These tests prove it catches a through-wall tracker and that
    /// legit play (no sweep, or a visible enemy) never trips it. Logged only, replay-verifiable (Aim category).
    /// </summary>
    public class Hunt_honeypot
    {
        private static ITerrain OpenField()
        {
            var g = new SubGrid(16, 4, 16, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        private sealed class Brain : IBotBrain
        {
            public Func<NetServer, ServerPlayer, InputCmd> F;
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer s, ServerPlayer me) => InputCmd.Quantize(F(s, me));
        }

        /// <summary>A server where enemies at range are always withheld: no hearing (NoiseSlack 0), sight only to 5 m, close only to 1 m.</summary>
        private static NetServer HiddenPairsServer()
        {
            var s = new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            InterestConfig ic = s.Config.AntiCheat.Interest;
            ic.NoiseSlack = 0;      // nobody is heard, however fast they move
            ic.MaxSightRange = 5;   // line-of-sight tests stop at 5 m
            ic.AlwaysRange = 1;     // only point-blank is always sent
            return s;
        }

        /// <summary>Yaw (rad) and pitch (deg) that make WeaponRules.AimDir point exactly from <paramref name="eye"/> at <paramref name="at"/>.</summary>
        private static void AimAt(Vec3d eye, Vec3d at, out double yaw, out double pitch)
        {
            Vec3d to = at - eye;
            double len = to.Length;
            yaw = Math.Atan2(to.X, to.Z);
            pitch = -Math.Asin(Math.Max(-1, Math.Min(1, to.Y / len))) * (180.0 / Math.PI);
        }

        private static Vec3d Head(NetServer s, ServerPlayer p) =>
            p.State.Position + new Vec3d(0, PlayerMovement.HeightOf(p.State, s.Movement) - new WeaponsConfig().HeadHeight * 0.5, 0);
        private static Vec3d Eye(NetServer s, ServerPlayer p) =>
            p.State.Position + new Vec3d(0, PlayerMovement.EyeOf(p.State, s.Movement), 0);

        // ============================================================ CATCHES the through-wall tracker

        /// <summary>
        /// V (team 2) is a wallhacking client: it keeps its crosshair exactly on hidden enemy E's head every tick. E
        /// (team 1) sprints sideways across V's view, so the bearing to E sweeps well past the threshold. Interest
        /// withholds E from V the whole time (asserted). The server flags V for phantom-tracking, and the flag is an
        /// Aim-category flag, so a replay re-run reproduces it.
        /// </summary>
        [Test]
        public void AWallhackerTrackingAHiddenStrafingEnemy_IsFlagged()
        {
            NetServer s = HiddenPairsServer();
            ServerPlayer e = null;
            // E sprints along +Z (perpendicular to the V->E line, which runs along +X): pure lateral motion.
            e = s.AddBot(new Brain { F = (sv, me) => { var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick }; c.Move.Yaw = 0; c.Move.MoveZ = 1; c.Move.SprintHeld = true; return c; } },
                1, new Vec3d(30, 0, 20), 0);
            // V stands still at 10 m and aims dead on E's head every tick (a perfect wallhack).
            ServerPlayer v = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    AimAt(Eye(sv, me), Head(sv, e), out double yaw, out double pitch);
                    c.Move.Yaw = yaw; c.Pitch = (float)pitch;
                    return c;
                }
            }, 2, new Vec3d(20, 0, 20), Math.PI / 2);
            v.Viewer = true; // a stand-in for a person: interest (and so the honeypot) is worked out for viewers

            bool everWithheld = false;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                if (!s.IsSentTo(v.Id, e.Id)) everWithheld = true;
            }

            Assert.That(everWithheld, "E was withheld from V (interest), so V could not fairly know where to aim");
            Assert.That(s.Flags.Count(FlagKind.PhantomTrack), Is.GreaterThan(0),
                "V tracked a hidden, strafing enemy's head through the cull: phantom-track flagged");
            Assert.That(FlagLog.FromSimulation(FlagKind.PhantomTrack), Is.True,
                "phantom-track is an Aim-category flag, so a replay re-run reproduces it");
        }

        // ============================================================ NO false positive: no bearing sweep

        /// <summary>
        /// Two players walled off from each other (withheld), walking straight at each other down a corridor, each
        /// looking dead ahead. V's crosshair naturally sits on hidden E through the wall the whole time, but the bearing
        /// to E barely moves (E is straight ahead, closing), so there is nothing to "follow". No flag: this is exactly
        /// the coincidental alignment the sweep gate exists to ignore.
        /// </summary>
        [Test]
        public void TwoPlayersWalkingStraightAtEachOther_AreNotFlagged()
        {
            NetServer s = HiddenPairsServer();
            // E approaches V along -X (straight toward V); V aims straight along +X at E. Bearing stays ~90 deg: no sweep.
            ServerPlayer e = s.AddBot(new Brain { F = (sv, me) => { var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick }; c.Move.Yaw = -Math.PI / 2; c.Move.MoveZ = 1; c.Move.SprintHeld = true; return c; } },
                1, new Vec3d(40, 0, 20), -Math.PI / 2);
            ServerPlayer v = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    c.Move.Yaw = Math.PI / 2; // dead ahead along +X, toward E
                    return c;
                }
            }, 2, new Vec3d(20, 0, 20), Math.PI / 2);
            v.Viewer = true; // a stand-in for a person: interest (and so the honeypot) is worked out for viewers

            bool everWithheld = false, everOnTarget = false;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                if (!s.IsSentTo(v.Id, e.Id)) everWithheld = true;
                // The crosshair really is on E (so it is the sweep gate, not a miss, that spares them).
                Vec3d aim = WeaponRules.AimDir(Math.PI / 2, 0, 0, 0);
                Vec3d to = e.State.Position - v.State.Position;
                if (to.Length > 1e-3)
                {
                    double cos = (aim.X * to.X + aim.Z * to.Z) / to.Length;
                    if (Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180 / Math.PI < 3) everOnTarget = true;
                }
            }

            Assert.That(everWithheld, "the pair is withheld from each other");
            Assert.That(everOnTarget, "V's crosshair is on E for a stretch (so only the lack of bearing sweep spares it)");
            Assert.That(s.Flags.Count(FlagKind.PhantomTrack), Is.Zero,
                "no sweep, no follow: a straight-on approach is never phantom-track");
        }

        // ============================================================ NO false positive: the enemy is visible

        /// <summary>
        /// The same perfect tracking as the catch test, but with sight range left at its default so E is in V's view
        /// (sent). Phantom-track only ever considers WITHHELD pairs, so legitimately tracking an enemy you can see is
        /// never flagged, however tightly you follow them.
        /// </summary>
        [Test]
        public void TrackingAVisibleEnemy_IsNotFlagged()
        {
            var s = new NetServer(OpenField(), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            s.Config.AntiCheat.Interest.NoiseSlack = 0; // isolate the sight path
            ServerPlayer e = null;
            e = s.AddBot(new Brain { F = (sv, me) => { var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick }; c.Move.Yaw = 0; c.Move.MoveZ = 1; c.Move.SprintHeld = true; return c; } },
                1, new Vec3d(30, 0, 20), 0);
            ServerPlayer v = s.AddBot(new Brain
            {
                F = (sv, me) =>
                {
                    var c = new InputCmd { Tick = sv.Tick, ViewTick = sv.Tick };
                    AimAt(Eye(sv, me), Head(sv, e), out double yaw, out double pitch);
                    c.Move.Yaw = yaw; c.Pitch = (float)pitch;
                    return c;
                }
            }, 2, new Vec3d(20, 0, 20), Math.PI / 2);
            v.Viewer = true; // a stand-in for a person: interest (and so the honeypot) is worked out for viewers

            bool everSent = false;
            for (int i = 0; i < 2 * SimTime.TicksPerSecond; i++)
            {
                s.Step();
                if (s.IsSentTo(v.Id, e.Id)) everSent = true;
            }

            Assert.That(everSent, "E is in the open within sight range, so interest sends E to V (a fair sighting)");
            Assert.That(s.Flags.Count(FlagKind.PhantomTrack), Is.Zero,
                "tracking an enemy you can legitimately see is never phantom-track");
        }
    }
}
