using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Audio;

namespace Sculpt.Core.Tests.Audio
{
    /// <summary>Movement sounds others hear (Ben 2026-11-26): steps by stride, crouch-walking quiet, jumps, landings, slides.</summary>
    public class MoveSoundsTests
    {
        private static readonly MoveSoundConfig C = new MoveSoundConfig();

        private static int Steps(FootstepClock clock, double speed, double metres, bool crouched, out double vol, out double range)
        {
            int n = 0;
            vol = range = 0;
            double dt = 1 / 144.0;
            for (double walked = 0; walked < metres - 1e-9; walked += speed * dt)
                if (clock.Advance(C, true, crouched, speed * dt, speed, out double v, out double r)) { n++; vol = v; range = r; }
            return n;
        }

        [Test]
        public void Sprinting_StepsEveryStride_LoudAndFar()
        {
            var clock = new FootstepClock();
            int n = Steps(clock, 10, 23, false, out double vol, out double range);
            Assert.That(n, Is.EqualTo(10), "23 m at a 2.3 m stride");
            Assert.That(vol, Is.EqualTo(C.SprintVolume).Within(1e-9));
            Assert.That(range, Is.EqualTo(C.SprintRange).Within(1e-9));
        }

        [Test]
        public void CrouchWalking_IsQuietAndOnlyHeardUpClose_NeverSilent()
        {
            var clock = new FootstepClock();
            int n = Steps(clock, 2.5, 9, true, out double vol, out double range);
            Assert.That(n, Is.EqualTo(10), "9 m at a 0.9 m stride");
            Assert.That(vol, Is.EqualTo(C.CrouchVolume));
            Assert.That(range, Is.EqualTo(C.CrouchRange));
            Assert.That(range, Is.LessThan(C.JogRange / 3));
            Assert.That(vol, Is.GreaterThan(0));
        }

        [Test]
        public void StandingStill_OrInTheAir_NoSteps_AndTheFirstStepAfterLandingComesEarly()
        {
            var clock = new FootstepClock();
            Assert.That(clock.Advance(C, true, false, 0.0, 0.0, out _, out _), Is.False);
            Assert.That(clock.Advance(C, false, false, 1.0, 8, out _, out _), Is.False);
            Assert.That(clock.Stride, Is.EqualTo(C.FirstStepAfter));
            // Landed at a jog: the first step after 1.5 - 0.6 = 0.9 m, not a whole stride.
            Assert.That(clock.Advance(C, true, false, 0.85, 3, out _, out _), Is.False);
            Assert.That(clock.Advance(C, true, false, 0.1, 3, out _, out _), Is.True);
        }

        [Test]
        public void ALongFrame_NeverFiresABurst()
        {
            var clock = new FootstepClock();
            Assert.That(clock.Advance(C, true, false, 1.9, 10, out _, out _), Is.False); // over the teleport cut? no: 1.9 < 2
            Assert.That(clock.Advance(C, true, false, 1.9, 10, out _, out _), Is.True);
            Assert.That(clock.Stride, Is.LessThan(2.3));
            Assert.That(clock.Advance(C, true, false, 3.0, 10, out _, out _), Is.False, "a 3 m jump in one update is a snap, not steps");
        }

        private static List<MoveSound> Feed(RemoteMoveSounds r, int id, Vec3d feet, Vec3d vel, MoveMode mode, bool crouched = false, bool dead = false, bool extrapolated = false)
        {
            var into = new List<MoveSound>();
            r.BeginFrame();
            r.Update(id, feet, vel, mode, crouched, dead, extrapolated, into);
            r.EndFrame();
            return into;
        }

        [Test]
        public void Remote_RunningMakesSteps_FirstSightMakesNone()
        {
            var r = new RemoteMoveSounds();
            var p = new Vec3d(0, 0, 0);
            var v = new Vec3d(10, 0, 0);
            Assert.That(Feed(r, 5, p, v, MoveMode.Ground), Is.Empty, "first sight");
            int steps = 0;
            for (int i = 1; i <= 144; i++)
                foreach (MoveSound s in Feed(r, 5, new Vec3d(i * 10 / 144.0, 0, 0), v, MoveMode.Ground))
                    if (s.Type == MoveSoundType.Step) { steps++; Assert.That(s.Range, Is.EqualTo(C.SprintRange)); }
            Assert.That(steps, Is.EqualTo(4), "10 m at 2.3 m a stride, the first after 2.3 - 0.6");
        }

        [Test]
        public void Remote_GuessedPoses_MakeNoSound()
        {
            var r = new RemoteMoveSounds();
            var v = new Vec3d(10, 0, 0);
            Feed(r, 1, Vec3d.Zero, v, MoveMode.Ground);
            for (int i = 1; i <= 144; i++)
                Assert.That(Feed(r, 1, new Vec3d(i * 10 / 144.0, 0, 0), v, MoveMode.Ground, extrapolated: true), Is.Empty);
        }

        [Test]
        public void Remote_JumpAndLanding_LandingWeightFollowsTheFall()
        {
            var r = new RemoteMoveSounds();
            Feed(r, 2, Vec3d.Zero, Vec3d.Zero, MoveMode.Ground);
            List<MoveSound> s = Feed(r, 2, new Vec3d(0, 0.05, 0), new Vec3d(0, 5, 0), MoveMode.Air);
            Assert.That(s.Exists(m => m.Type == MoveSoundType.Jump));
            Feed(r, 2, new Vec3d(0, 0.5, 0), new Vec3d(0, -3, 0), MoveMode.Air);
            s = Feed(r, 2, Vec3d.Zero, Vec3d.Zero, MoveMode.Ground);
            MoveSound hop = s.Find(m => m.Type == MoveSoundType.Land);
            Assert.That(hop.Weight, Is.EqualTo(0), "a hop");
            Assert.That(hop.Range, Is.EqualTo(C.LandRangeMin));

            Feed(r, 2, new Vec3d(0, 1.5, 0), new Vec3d(0, -1, 0), MoveMode.Air);
            Feed(r, 2, new Vec3d(0, 1.0, 0), new Vec3d(0, -14, 0), MoveMode.Air);
            s = Feed(r, 2, new Vec3d(0, 0.5, 0), new Vec3d(0, 0, 0), MoveMode.Slide);
            MoveSound big = s.Find(m => m.Type == MoveSoundType.Land);
            Assert.That(big.Weight, Is.EqualTo(1));
            Assert.That(big.Range, Is.EqualTo(C.LandRangeMax));
            Assert.That(s.Exists(m => m.Type == MoveSoundType.SlideStart), "landed into a slide");
            s = Feed(r, 2, new Vec3d(0.1, 0.5, 0), new Vec3d(3, 0, 0), MoveMode.Ground);
            Assert.That(s.Exists(m => m.Type == MoveSoundType.SlideEnd));
        }

        [Test]
        public void Remote_RespawnOrDeath_IsSilent_AndLeaversAreForgotten()
        {
            var r = new RemoteMoveSounds();
            Feed(r, 3, Vec3d.Zero, Vec3d.Zero, MoveMode.Air);
            Assert.That(Feed(r, 3, new Vec3d(40, 0, 0), Vec3d.Zero, MoveMode.Ground), Is.Empty, "a snap across the map");
            Assert.That(Feed(r, 3, new Vec3d(40, 0, 0), Vec3d.Zero, MoveMode.Ground, dead: true), Is.Empty);
            Assert.That(Feed(r, 3, new Vec3d(2, 0, 2), new Vec3d(0, -10, 0), MoveMode.Ground), Is.Empty, "respawned: no landing");
            Assert.That(r.Tracked, Is.EqualTo(1));
            r.BeginFrame(); r.EndFrame();
            Assert.That(r.Tracked, Is.EqualTo(0));
        }

        [Test]
        public void Remote_VaultOrMantle_Grabs()
        {
            var r = new RemoteMoveSounds();
            Feed(r, 4, Vec3d.Zero, new Vec3d(6, 0, 0), MoveMode.Ground);
            Assert.That(Feed(r, 4, new Vec3d(0.05, 0.1, 0), new Vec3d(6, 2, 0), MoveMode.Mantle).Exists(m => m.Type == MoveSoundType.Grab));
        }
    }
}
