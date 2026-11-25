using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    /// <summary>Momentum movement (Ben 2026-11-21) on the exact building world.</summary>
    public class MomentumTests
    {
        private static readonly double East = Math.PI / 2, North = 0;
        private BrushWorld _w;
        private MovementConfig _c;
        private MotorState _s;
        private List<MoveEvent> _ev;

        [SetUp]
        public void SetUp()
        {
            _w = new BrushWorld();
            _w.AddBox(new Vec3d(-50, -1, -50), new Vec3d(150, 0, 150), BrushKind.Ground);
            _c = new MovementConfig();
            _ev = new List<MoveEvent>();
        }

        private void Tick(MoveInput i) => PlayerMovement.Step(_w, _c, ref _s, i, _ev);
        private void Run(int n, MoveInput i) { for (int k = 0; k < n; k++) Tick(i); }
        private bool Had(MoveEventType t) => _ev.Any(e => e.Type == t);

        private void At(double x, double y, double z, double vx = 0, double vz = 0)
        {
            _s = MotorState.At(new Vec3d(x, y, z));
            _s.Tick = 1000;
            _s.Velocity = new Vec3d(vx, 0, vz);
            _ev.Clear();
        }

        private void Settle() { Run(20, new MoveInput { Yaw = East }); _ev.Clear(); }

        // ---------------- mantle ----------------

        [Test]
        public void MantleKeepsTheSpeedYouHitTheLedgeWith()
        {
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(14, 3, 5), BrushKind.Frame); // a 3 m ledge (two tiles)
            At(8.6, 1.2, 0, vx: 9);
            _s.Mode = MoveMode.Air;
            var jump = new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true };
            for (int k = 0; k < 30 && _s.Mode == MoveMode.Air; k++) Tick(k < 2 ? new MoveInput { Yaw = East, MoveZ = 1 } : jump);
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle), "air mantle caught the 3 m ledge");
            for (int k = 0; k < 40 && _s.Mode == MoveMode.Mantle; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1 });
            Assert.That(Had(MoveEventType.MantleFinished), Is.True);
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(9).Within(0.6), "left the mantle at about the speed it hit the wall");
        }

        [Test]
        public void MantleCancelPopsOverEarlyWithSpeedAndNoBigSnap()
        {
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(14, 3, 5), BrushKind.Frame);
            At(8.6, 1.2, 0, vx: 9);
            _s.Mode = MoveMode.Air;
            for (int k = 0; k < 30 && _s.Mode == MoveMode.Air; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = k >= 2 });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
            int ticks = 0;
            double lastY = _s.Position.Y;
            while (_s.Mode == MoveMode.Mantle && ticks < 40)
            {
                bool near = _s.ActionTargetY - _s.Position.Y <= _c.MantleCancelMaxRemaining - 0.2;
                Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = near });
                Assert.That(_s.Position.Y - lastY, Is.LessThanOrEqualTo(_c.MantleCancelMaxRemaining + 1e-9));
                lastY = _s.Position.Y;
                ticks++;
            }
            Assert.That(Had(MoveEventType.MantleCancel), Is.True);
            Assert.That(ticks, Is.LessThan(_c.MantleRiseTicks + _c.MantleForwardTicks - 6), "saves real time");
            Run(20, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Assert.That(_s.Position.Y, Is.EqualTo(3).Within(1e-3), "on the ledge");
            Assert.That(_s.Position.X, Is.GreaterThan(11));
        }

        [Test]
        public void MantleCancelIsRefusedFarFromTheTop()
        {
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(14, 3, 5), BrushKind.Frame);
            At(9.6, 0, 0);
            Settle();
            Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
            Run(_c.MantleCancelFromTick, new MoveInput { Yaw = East, MoveZ = 1 });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true }); // still ~1 m below the top
            Assert.That(Had(MoveEventType.MantleCancel), Is.False);
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
        }

        [Test]
        public void VaultsAOneTileWallButMantlesTwoTiles()
        {
            _c.AutoVault = true; // parked: off in the game since vault and mantle merged
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(10.3, 1.5, 5), BrushKind.Wall);
            At(4, 0, 0);
            Settle();
            Run(80, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Assert.That(Had(MoveEventType.Vaulted), Is.True, "sprint vault over a 1.5 m tile");
            Assert.That(_s.Position.X, Is.GreaterThan(10.3));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.SprintSpeed).Within(1e-6));

            SetUp();
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(10.3, 3.3, 5), BrushKind.Wall); // 3.3 m: a hard wall
            At(9.6, 0, 0);
            Settle();
            Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true });
            Run(60, new MoveInput { Yaw = East, MoveZ = 1 });
            Assert.That(Had(MoveEventType.MantleStarted), Is.False);
        }

        // ---------------- wall kick ----------------

        private void FlyAtNorthWall(double speed, double angle)
        {
            // Wall face at z = 10, approached heading north-east at the given angle from north.
            _w.AddBox(new Vec3d(-20, 0, 10), new Vec3d(40, 12, 11), BrushKind.Wall);
            At(0, 4, 10 - 0.35 - speed * Math.Cos(angle) * 0.1, vx: speed * Math.Sin(angle), vz: speed * Math.Cos(angle));
            _s.Mode = MoveMode.Air;
            _s.Velocity = new Vec3d(_s.Velocity.X, 2, _s.Velocity.Z);
        }

        [Test]
        public void PerfectWallKickTurnsAndKeepsTheSpeedYouHitWith()
        {
            _c.WallKicksEnabled = true; // parked: wall kicks are off in the game (Ben 2026-11-25)
            FlyAtNorthWall(10, 0.3);
            int k = 0;
            while (_s.Tick - _s.WallHitTick > 0 && k++ < 20) Tick(new MoveInput { Yaw = North });
            Assert.That(_s.Tick - _s.WallHitTick, Is.EqualTo(0), "hit the wall");
            Tick(new MoveInput { Yaw = East, JumpPressed = true }); // look east and kick straight away
            MoveEvent kick = _ev.Single(e => e.Type == MoveEventType.WallKick);
            Assert.That(kick.Value, Is.EqualTo(1), "perfect");
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(10).Within(1e-6));
            Assert.That(_s.Velocity.X, Is.GreaterThan(9), "now heading east");
            Assert.That(_s.Velocity.Z, Is.LessThan(0), "pushed off the wall");
            Assert.That(_s.Velocity.Y, Is.GreaterThanOrEqualTo(_c.WallKickUpSpeed - 1));
        }

        [Test]
        public void LateWallKickKeepsLess()
        {
            _c.WallKicksEnabled = true; // parked: wall kicks are off in the game (Ben 2026-11-25)
            FlyAtNorthWall(10, 0.3);
            int k = 0;
            while (_s.Tick - _s.WallHitTick > 0 && k++ < 20) Tick(new MoveInput { Yaw = North });
            Run(_c.WallKickPerfectTicks + 2, new MoveInput { Yaw = North, MoveZ = 1 });
            Tick(new MoveInput { Yaw = East, JumpPressed = true });
            MoveEvent kick = _ev.Single(e => e.Type == MoveEventType.WallKick);
            Assert.That(kick.Value, Is.EqualTo(0));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(10 * _c.WallKickLateKeep).Within(0.05));
        }

        [Test]
        public void WallKicksAreLimitedAndNeverTheSameWallTwiceInARow()
        {
            _c.WallKicksEnabled = true; // parked: wall kicks are off in the game (Ben 2026-11-25)
            FlyAtNorthWall(8, 0.0);
            int k = 0;
            while (_s.Tick - _s.WallHitTick > 0 && k++ < 20) Tick(new MoveInput { Yaw = North });
            Tick(new MoveInput { Yaw = North, JumpPressed = true }); // looking into the wall: mirrored straight back
            Assert.That(_ev.Count(e => e.Type == MoveEventType.WallKick), Is.EqualTo(1));
            Assert.That(_s.Velocity.Z, Is.LessThan(-7));
            // Turn around and fly back into the same wall: no second kick off it before touching the ground.
            _s.Velocity = new Vec3d(0, 3, 8);
            for (int t = 0; t < 30; t++) Tick(new MoveInput { Yaw = North, JumpPressed = t % 7 == 6 });
            Assert.That(_ev.Count(e => e.Type == MoveEventType.WallKick), Is.EqualTo(1));
        }

        [Test]
        public void NoWallKickOffASmallStep()
        {
            _c.WallKicksEnabled = true; // parked: wall kicks are off in the game (Ben 2026-11-25)
            _w.AddBox(new Vec3d(-20, 0, 10), new Vec3d(40, 0.2, 11), BrushKind.Wall); // a 20 cm kerb
            At(0, 0.05, 9.6, vz: 6);
            _s.Mode = MoveMode.Air;
            Tick(new MoveInput { Yaw = North, JumpPressed = true });
            Assert.That(Had(MoveEventType.WallKick), Is.False);
        }

        // ---------------- counter-strafe brake ----------------

        private double StopDistance(MoveInput press)
        {
            At(0, 0, 0);
            Settle();
            _s.Velocity = new Vec3d(12, 0, 0);
            double x0 = _s.Position.X;
            for (int k = 0; k < 120 && _s.HorizontalSpeed > 0.01; k++)
            {
                if (press.MoveZ < 0 && _s.Velocity.X < 0.05) press = new MoveInput { Yaw = East };
                Tick(press);
            }
            return _s.Position.X - x0;
        }

        [Test]
        public void PressingBackAtSpeedStopsMuchFasterThanLettingGo()
        {
            double coast = StopDistance(new MoveInput { Yaw = East });
            double brake = StopDistance(new MoveInput { Yaw = East, MoveZ = -1 });
            Assert.That(brake, Is.LessThan(coast * 0.6), $"brake {brake:0.00} m vs let go {coast:0.00} m");
            Assert.That(Had(MoveEventType.Braked), Is.True);
        }

        [Test]
        public void SidewaysCountsAsABrakeButNeverFlipsDirectionInOneTick()
        {
            At(0, 0, 0);
            Settle();
            _s.Velocity = new Vec3d(12, 0, 0);
            Tick(new MoveInput { Yaw = East, MoveX = -1 }); // strafe left = toward north, across the motion
            Assert.That(Had(MoveEventType.Braked), Is.True);
            Assert.That(_s.Velocity.X, Is.GreaterThan(8), "only bled speed this tick");
            Assert.That(_s.HorizontalSpeed, Is.LessThan(12));
        }

        [Test]
        public void NoBrakeAtWalkingSpeeds()
        {
            At(0, 0, 0);
            Settle();
            Run(30, new MoveInput { Yaw = East, MoveZ = 1 });
            Tick(new MoveInput { Yaw = East, MoveZ = -1 });
            Assert.That(Had(MoveEventType.Braked), Is.False);
        }

        [Test]
        public void SlideBrakeWorksWhileSliding()
        {
            At(0, 0, 0);
            Settle();
            Run(30, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Slide));
            double before = _s.HorizontalSpeed;
            Tick(new MoveInput { Yaw = East, MoveZ = -1, CrouchHeld = true });
            Assert.That(_s.HorizontalSpeed, Is.LessThan(before - _c.SlideFriction * _c.Dt - 0.5));
        }

        // ---------------- air ----------------

        [Test]
        public void FastAirSteeringIsOnlyANudgeAndNeverAddsSpeed()
        {
            At(0, 30, 0, vx: 10);
            _s.Mode = MoveMode.Air;
            Run(36, new MoveInput { Yaw = East, MoveX = -1 }); // hold strafe toward north for 0.6 s
            double turned = Math.Atan2(_s.Velocity.Z, _s.Velocity.X);
            Assert.That(turned, Is.GreaterThan(0.1), $"some nudge (v {_s.Velocity}, mode {_s.Mode})");
            Assert.That(turned, Is.LessThanOrEqualTo(_c.AirNudgeTurnRate * 0.6 + 1e-6), "but only a nudge");
            Assert.That(_s.HorizontalSpeed, Is.LessThanOrEqualTo(10 + 1e-9));
        }

        [Test]
        public void AirSpeedAboveTheSoftCapBleeds()
        {
            double v0 = _c.AirSoftCapSpeed + 5;
            At(0, 60, 0, vx: v0);
            _s.Mode = MoveMode.Air;
            Run(SimTime.TicksPerSecond, new MoveInput { Yaw = East });
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(v0 - _c.AirSoftCapDecel).Within(0.05));
        }

        // ---------------- one ledge climb, no wall kicks (Ben 2026-11-25: "fortnite style movement, not parkour") ----------------

        [Test]
        public void NoWallKicksByDefault()
        {
            _w.AddBox(new Vec3d(-20, 0, 10), new Vec3d(40, 12, 11), BrushKind.Wall);
            At(0, 4, 10 - _c.HalfWidth, vx: 3, vz: 8);
            _s.Mode = MoveMode.Air;
            _s.Velocity = new Vec3d(3, 2, 8);
            Tick(new MoveInput { Yaw = North, MoveZ = 1 }); // hits the wall
            Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpPressed = true, JumpHeld = true });
            Assert.That(Had(MoveEventType.WallKick), Is.False);
            Assert.That(_s.Velocity.Y, Is.LessThan(2), "no kick upward either");
        }

        [Test]
        public void JumpingAtAOneTileLedgeClimbsItQuicklyAndKeepsSpeed()
        {
            // The old sprint vault is now the same move as the mantle: jump at the ledge. A one-tile climb is short.
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(10.3, 1.5, 5), BrushKind.Wall);
            At(4, 0, 0);
            Settle();
            int k = 0;
            for (; k < 120 && _s.Position.X < 10 - _c.HalfWidth - 1e-6; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Assert.That(Had(MoveEventType.Vaulted), Is.False, "no automatic vault");
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, JumpPressed = true, JumpHeld = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
            int ticks = 0;
            while (_s.Mode == MoveMode.Mantle && ticks < 60) { Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true }); ticks++; }
            Assert.That(ticks, Is.LessThanOrEqualTo(SimTime.TicksPerSecond / 5), "about 0.2 s for one tile (a full 3 m climb is 0.4 s)");
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(_c.SprintSpeed - 0.5), "left with the sprint speed it hit the ledge at");
        }

        [Test]
        public void HoldingJumpInTheAirClimbsALedgeYouTouch()
        {
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(14, 3, 5), BrushKind.Frame); // 3 m: too high to jump onto
            At(7.5, 0, 0);
            Settle();
            Run(10, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, JumpPressed = true, JumpHeld = true });
            for (int k = 0; k < 40 && _s.Mode == MoveMode.Air; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1, JumpHeld = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle), "one press, held: the touch climbs");
            // Not holding: a touch in the air does nothing until you press again.
            At(7.5, 0, 0);
            Settle();
            Run(10, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, JumpPressed = true, JumpHeld = true });
            for (int k = 0; k < 40 && _s.Mode == MoveMode.Air; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1 });
            Assert.That(Had(MoveEventType.MantleStarted), Is.False);
        }

        // ---------------- slide carving (Ben 2026-11-25: "no sliding around rooms") ----------------

        /// <summary>A slide at <paramref name="speed"/> heading east, then n ticks steering with this yaw and keys.</summary>
        private (double turned, double speed) SlideSteer(double speed, double yaw, double moveX, double moveZ, int ticks)
        {
            At(0, 0, 0, vx: speed);
            _s.Mode = MoveMode.Slide;
            _s.Crouched = true;
            _s.Grounded = true;
            Vec3d before = _s.Velocity;
            Run(ticks, new MoveInput { Yaw = yaw, MoveX = moveX, MoveZ = moveZ, CrouchHeld = true });
            Vec3d after = new Vec3d(_s.Velocity.X, 0, _s.Velocity.Z);
            double turned = Math.Acos(Math.Max(-1, Math.Min(1, after.Normalized.Dot(new Vec3d(before.X, 0, before.Z).Normalized))));
            return (turned, _s.HorizontalSpeed);
        }

        [Test]
        public void GentleSlideSteeringIsFree()
        {
            // A beginner holding W and turning the mouse a little: up to the free turn rate, only friction is paid.
            int n = SimTime.TicksPerSecond / 4;
            At(0, 0, 0, vx: 12); _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            for (int k = 0; k < n; k++) // the mouse turns 0.3 rad over the quarter second (1.2 rad/s)
                Tick(new MoveInput { Yaw = East - 0.3 * (k + 1) / n, MoveZ = 1, CrouchHeld = true });
            double turned = Math.Abs(Math.Atan2(_s.Velocity.Z, _s.Velocity.X)), speed = _s.HorizontalSpeed;
            Assert.That(turned, Is.EqualTo(0.3).Within(0.02), "followed the aim all the way");
            Assert.That(speed, Is.EqualTo(12 - _c.SlideFriction * n * _c.Dt).Within(1e-6), "nothing but friction");
            Assert.That(_s.Carving, Is.False);
            Assert.That(Had(MoveEventType.SlideCarve), Is.False);
        }

        [Test]
        public void HardSlideCarveTurnsFastAndCostsSpeed()
        {
            // Aiming 90 degrees left of the slide: a carve turns up to SlideCarveTurnRate, paying SlideCarveLoss per
            // radian above the free rate. A quarter turn takes ~0.35 s and keeps most of the speed.
            int n = (int)Math.Ceiling(Math.PI / 2 / _c.SlideCarveTurnRate * SimTime.TicksPerSecond) + 1;
            var (turned, speed) = SlideSteer(13, North, 0, 1, n);
            Assert.That(turned, Is.EqualTo(Math.PI / 2).Within(0.01), "came round a full corner");
            Assert.That(Had(MoveEventType.SlideCarve), Is.True);
            double friction = _c.SlideFriction * n * _c.Dt;
            double extra = Math.PI / 2 - _c.SlideTurnRate * n * _c.Dt;
            Assert.That(speed, Is.LessThan(13 - friction - 0.05), "a carve costs speed");
            Assert.That(speed, Is.GreaterThan((13 - friction) * (1 - _c.SlideCarveLoss * extra) - 0.2), "but not much");
            Assert.That(speed, Is.GreaterThan(_c.SprintSpeed), "still faster than a sprint after a corner");
            Assert.That(Had(MoveEventType.Braked), Is.False, "holding W steers, it never brakes");
            // The old slide (1.6 rad/s only) needed ~1 s for the same corner: a 90 degree turn is at least 2.5x faster.
            Assert.That(n, Is.LessThan(SimTime.TicksPerSecond * 0.4));
        }

        [Test]
        public void SmoothCarvingKeepsMoreSpeedThanASnapTurn()
        {
            // The skill: start the turn early and spread it (free steering) instead of snapping at the last moment.
            int n = SimTime.TicksPerSecond;
            At(0, 0, 0, vx: 13); _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            for (int k = 0; k < n; k++) Tick(new MoveInput { Yaw = East - Math.PI / 2 * Math.Min(1, (k + 1) / (double)n), MoveZ = 1, CrouchHeld = true });
            double smooth = _s.HorizontalSpeed;
            At(0, 0, 0, vx: 13); _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            for (int k = 0; k < n; k++) Tick(new MoveInput { Yaw = k < n * 2 / 3 ? East : North, MoveZ = 1, CrouchHeld = true });
            double snap = _s.HorizontalSpeed;
            Assert.That(smooth, Is.GreaterThan(snap + 0.3));
        }

        [Test]
        public void SidewaysOrBackKeysStillBrakeASlide()
        {
            At(0, 0, 0, vx: 13); _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            Run(SimTime.TicksPerSecond / 10, new MoveInput { Yaw = East, MoveZ = -1, CrouchHeld = true });
            Assert.That(Had(MoveEventType.Braked), Is.True);
            At(0, 0, 0, vx: 13); _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            Run(SimTime.TicksPerSecond / 10, new MoveInput { Yaw = East, MoveX = 1, CrouchHeld = true });
            Assert.That(Had(MoveEventType.Braked), Is.True, "D alone (strafe) brakes");
        }

        [Test]
        public void SlidingIntoALedgeAndJumpingMantlesIt()
        {
            _w.AddBox(new Vec3d(10, 0, -5), new Vec3d(14, 3, 5), BrushKind.Frame); // a 3 m ledge
            At(8, 0, 0, vx: 11);
            _s.Mode = MoveMode.Slide; _s.Crouched = true; _s.Grounded = true;
            for (int k = 0; k < 30 && _s.Position.X < 10 - _c.HalfWidth - 1e-6; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1, CrouchHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, CrouchHeld = true, JumpPressed = true, JumpHeld = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
        }

        // ---------------- slide-hops ----------------

        [Test]
        public void ChainedSlideHopsKeepMostSpeedButNotAll()
        {
            At(0, 0, 0);
            Settle();
            Run(30, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            double start = _s.HorizontalSpeed;
            int hops = 0;
            bool held = false;
            for (int k = 0; k < 400 && hops < 4; k++)
            {
                bool jump = _s.Mode == MoveMode.Slide && !held;
                if (jump) hops++;
                Tick(new MoveInput { Yaw = East, MoveZ = 1, CrouchHeld = true, JumpPressed = jump, JumpHeld = jump });
                held = jump;
            }
            for (int k = 0; k < 60 && _s.Mode != MoveMode.Slide; k++) Tick(new MoveInput { Yaw = East, MoveZ = 1, CrouchHeld = true });
            Assert.That(_ev.Count(e => e.Type == MoveEventType.SlideLandSuccess), Is.GreaterThanOrEqualTo(4));
            Assert.That(_s.HorizontalSpeed, Is.LessThan(start * 0.9), "four hops cost speed");
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(start * 0.7), "but keep most of it");
        }

        // ---------------- stairs ----------------

        [Test]
        public void StairsAreSmoothToRunUpAndDown()
        {
            _w.AddRamp(new Vec3d(10, 0, -3), new Vec3d(19, 4.5, 3), Dir.East, BrushKind.Stairs); // one storey up over 9 m
            _w.AddBox(new Vec3d(19, 0, -3), new Vec3d(30, 4.5, 3), BrushKind.Floor);
            At(5, 0, 0);
            Settle();
            int air = 0;
            double minSpeed = double.MaxValue;
            for (int k = 0; k < 140; k++)
            {
                Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
                if (_s.Position.X > 11 && _s.Position.X < 18)
                {
                    if (_s.Mode == MoveMode.Air) air++;
                    minSpeed = Math.Min(minSpeed, _s.HorizontalSpeed);
                }
            }
            Assert.That(_s.Position.Y, Is.EqualTo(4.5).Within(1e-3), "made it to the top");
            Assert.That(air, Is.EqualTo(0), "no bumps on the way up");
            Assert.That(minSpeed, Is.GreaterThan(_c.SprintSpeed - 0.01), "no speed lost on stairs");
        }

        [Test]
        public void SlidingDownStairsBuildsSpeed()
        {
            _w.AddBox(new Vec3d(-10, 0, -3), new Vec3d(10, 9, 3), BrushKind.Floor);
            _w.AddRamp(new Vec3d(10, 0, -3), new Vec3d(28, 9, 3), Dir.West, BrushKind.Stairs);
            At(4, 9, 0);
            Settle();
            Run(30, new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true });
            Tick(new MoveInput { Yaw = East, MoveZ = 1, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            double top = 0;
            for (int k = 0; k < 200 && _s.Position.X < 27; k++)
            {
                Tick(new MoveInput { Yaw = East, MoveZ = 1, CrouchHeld = true });
                top = Math.Max(top, _s.HorizontalSpeed);
            }
            Assert.That(top, Is.GreaterThan(13.0));
        }
    }
}
