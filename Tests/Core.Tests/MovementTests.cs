using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class MovementTests
    {
        private const double B = VoxelGrid.BlockSize;
        private static readonly double East = Math.PI / 2; // yaw facing +X

        private VoxelGrid _g;
        private MovementConfig _c;
        private MotorState _s;
        private List<MoveEvent> _events;

        [SetUp]
        public void SetUp()
        {
            _g = new VoxelGrid(2, 2, 2, -16);
            _g.FillBaseGround();
            _c = new MovementConfig();
            _events = new List<MoveEvent>();
            Place(3, 0, 5);
        }

        private void Place(double cx, double feetY, double cz)
        {
            _s = MotorState.At(new Vec3d((cx + 0.5) * B, feetY, (cz + 0.5) * B));
            Settle();
        }

        private void Settle()
        {
            for (int i = 0; i < 30; i++) Tick(new MoveInput { Yaw = East });
            _events.Clear();
        }

        private void Tick(MoveInput i) => PlayerMovement.Step(_g, _c, ref _s, i, _events);

        private void Run(int ticks, MoveInput i)
        {
            for (int t = 0; t < ticks; t++) Tick(i);
        }

        private static MoveInput Fwd(bool sprint = false, bool crouch = false) =>
            new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = sprint, CrouchHeld = crouch };

        private bool Penetrating() =>
            VoxelGeometry.BoxOverlapsSolid(_g, PlayerMovement.Box(_s.Position, _c.HalfWidth, PlayerMovement.HeightOf(_s, _c)));

        // ---------- Basic speeds ----------

        [Test]
        public void SettlesOnGround()
        {
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_s.Position.Y, Is.EqualTo(0).Within(1e-3));
        }

        [Test]
        public void RunReachesSixQuickly()
        {
            Run(7, Fwd());
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.RunSpeed).Within(1e-6), "full speed within about 0.1 s");
            Run(30, Fwd());
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.RunSpeed).Within(1e-6));
            Assert.That(_s.Velocity.X, Is.EqualTo(_c.RunSpeed).Within(1e-6), "yaw pi/2 faces east");
        }

        [Test]
        public void SprintAndCrouchSpeeds()
        {
            Run(20, Fwd(sprint: true));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.SprintSpeed).Within(1e-6));
            Assert.That(_s.Sprinting, Is.True);
            Place(3, 0, 10);
            Run(20, Fwd(crouch: true));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(3.0).Within(1e-6));
            Assert.That(_s.Crouched, Is.True);
        }

        [Test]
        public void DiagonalIsNotFaster()
        {
            Run(20, new MoveInput { MoveZ = 1, MoveX = 1, Yaw = 0 });
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.RunSpeed).Within(1e-6));
        }

        [Test]
        public void SprintBackwardsIsJustRunning()
        {
            Run(20, new MoveInput { MoveZ = -1, Yaw = East, SprintHeld = true });
            Assert.That(_s.Sprinting, Is.False);
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.RunSpeed).Within(1e-6));
        }

        [Test]
        public void StopsSnappily()
        {
            Run(20, Fwd());
            Run(8, new MoveInput { Yaw = East });
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(0).Within(1e-9));
        }

        // ---------- Jump ----------

        [Test]
        public void JumpApexIsAboutOnePointThreeMetres()
        {
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            double peak = 0;
            for (int i = 0; i < 60; i++)
            {
                Tick(new MoveInput { Yaw = East });
                peak = Math.Max(peak, _s.Position.Y);
            }
            Assert.That(peak, Is.EqualTo(1.3).Within(0.03));
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_events.Count(e => e.Type == MoveEventType.Landed), Is.EqualTo(1));
        }

        [Test]
        public void JumpClearsOneLevel()
        {
            _g.Fill(new Int3(6, 0, 0), new Int3(12, 0, 31), Block.Solid);
            Run(10, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Run(40, Fwd());
            Assert.That(_s.Position.Y, Is.EqualTo(B).Within(1e-3));
            Assert.That(_s.Position.X, Is.GreaterThan(6 * B));
        }

        [Test]
        public void JumpCannotClearTwoLevelsAndThreeLevelsCannotBeMantled()
        {
            _g.Fill(new Int3(6, 0, 0), new Int3(6, 2, 31), Block.Solid); // 3 levels
            Run(30, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Run(60, Fwd());
            Assert.That(_s.Position.Y, Is.EqualTo(0).Within(1e-3));
            Assert.That(_s.Position.X, Is.LessThan(6 * B));
            Assert.That(_events.Any(e => e.Type == MoveEventType.MantleStarted), Is.False);
        }

        [Test]
        public void CoyoteJumpAfterWalkingOffLedge()
        {
            _g.Fill(new Int3(0, 0, 0), new Int3(5, 2, 31), Block.Solid); // a 3-high plateau, ledge at x = 7.2
            Place(5, 3 * B, 5);
            int airTicks = 0;
            while (_s.Mode != MoveMode.Air && airTicks < 100) { Tick(Fwd()); airTicks++; }
            Run(3, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Assert.That(_events.Any(e => e.Type == MoveEventType.Jumped), Is.True);
            Assert.That(_s.Velocity.Y, Is.GreaterThan(8));
        }

        [Test]
        public void NoCoyoteJumpLongAfterLeavingLedge()
        {
            _g.Fill(new Int3(0, 0, 0), new Int3(5, 2, 31), Block.Solid);
            Place(5, 3 * B, 5);
            while (_s.Mode != MoveMode.Air) Tick(Fwd());
            Run(10, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Assert.That(_events.Any(e => e.Type == MoveEventType.Jumped), Is.False);
        }

        [Test]
        public void NoDoubleJump()
        {
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            Run(5, new MoveInput { Yaw = East });
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            Assert.That(_events.Count(e => e.Type == MoveEventType.Jumped), Is.EqualTo(1));
        }

        [Test]
        public void BufferedJumpFiresOnLanding()
        {
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            while (_s.Velocity.Y > -8) Tick(new MoveInput { Yaw = East }); // falling, a few ticks before touchdown
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            Run(12, new MoveInput { Yaw = East });
            Assert.That(_events.Count(e => e.Type == MoveEventType.Jumped), Is.EqualTo(2));
        }

        [Test]
        public void AirControlIsLimited()
        {
            Tick(new MoveInput { JumpPressed = true, Yaw = East });
            Run(18, Fwd()); // 0.3 s
            Assert.That(_s.HorizontalSpeed, Is.LessThanOrEqualTo(0.15 * 60 * 0.31 + 1e-6));
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(1.0));
        }

        [Test]
        public void AirKeepsMomentumWithoutInput()
        {
            Run(20, Fwd(sprint: true));
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, JumpPressed = true });
            Run(20, new MoveInput { Yaw = East });
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.SprintSpeed).Within(1e-6));
        }

        // ---------- Slide ----------

        private void StartSlideFromSprint()
        {
            Run(20, Fwd(sprint: true));
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
        }

        [Test]
        public void SlideBoostsToTenThenDecaysOverAboutOneSecond()
        {
            StartSlideFromSprint();
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.SlideBoostSpeed - _c.SlideFriction / SimTime.TicksPerSecond).Within(1e-6));
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideStarted && e.Value == 1), Is.True);
            int ticks = 1;
            while (_s.Mode == MoveMode.Slide && ticks < 200) { Tick(Fwd(crouch: true)); ticks++; }
            double expect = (_c.SlideBoostSpeed - _c.SlideEndSpeed) / _c.SlideFriction * SimTime.TicksPerSecond;
            Assert.That(ticks, Is.InRange(expect - 5, expect + 5), "boost to the end speed at the slide friction (about 1.3 s)");
            Assert.That(_s.Crouched, Is.True);
        }

        [Test]
        public void FlatSlideBoostHasCooldown()
        {
            StartSlideFromSprint();
            Run(10, Fwd(crouch: true));
            Run(30, Fwd(sprint: true)); // stand and sprint again, 0.67 s after the first boost
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(_s.HorizontalSpeed, Is.LessThan(_c.SprintSpeed), "no boost during cooldown");
            Run(10, Fwd(crouch: true));
            Run(60, Fwd(sprint: true)); // now past 1.5 s
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(9.8));
        }

        [Test]
        public void CrouchWithoutSprintIsCrouchNotSlide()
        {
            Run(20, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_s.Crouched, Is.True);
        }

        [Test]
        public void ReleasingCrouchEndsSlide()
        {
            StartSlideFromSprint();
            Run(5, Fwd(crouch: true));
            Tick(Fwd());
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_s.Crouched, Is.False);
        }

        [Test]
        public void SlideJumpCarriesSlideSpeed()
        {
            StartSlideFromSprint();
            Tick(new MoveInput { MoveZ = 1, Yaw = East, CrouchHeld = true, JumpPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Air));
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(9.7));
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideJump), Is.True);
            Run(10, new MoveInput { Yaw = East });
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(9.7), "momentum kept in the air");
        }

        /// <summary>A 45-degree slope going down toward +X: ramps from x=2 (top, y=6) to x=8 (y=0), plateau at x 0..1.</summary>
        private void BuildDownhill()
        {
            _g.Fill(new Int3(0, 0, 0), new Int3(1, 6, 31), Block.Solid);
            for (int i = 0; i <= 6; i++)
            {
                int x = 2 + i, top = 6 - i;
                if (top > 0) _g.Fill(new Int3(x, 0, 0), new Int3(x, top - 1, 31), Block.Solid);
                for (int z = 0; z < 32; z++) _g.Set(x, top, z, Block.Ramp(Dir.East));
            }
        }

        [Test]
        public void DownhillSlideAcceleratesToCap()
        {
            BuildDownhill();
            Place(0, 7 * B, 5);
            double maxSpeed = 0;
            bool started = false;
            for (int i = 0; i < 120; i++)
            {
                bool press = !started && _s.Sprinting && _s.HorizontalSpeed > 8;
                Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = !started, CrouchHeld = started || press, CrouchPressed = press });
                if (press) started = true;
                if (_s.Mode == MoveMode.Slide) maxSpeed = Math.Max(maxSpeed, _s.HorizontalSpeed);
                Assert.That(Penetrating(), Is.False, "tick " + i);
            }
            Assert.That(started, Is.True);
            Assert.That(maxSpeed, Is.GreaterThan(12.0));
            Assert.That(maxSpeed, Is.LessThanOrEqualTo(_c.SlideMaxSpeed + 1e-9));
        }

        [Test]
        public void UphillSlideDeceleratesFasterThanFlat()
        {
            // Flat reference.
            StartSlideFromSprint();
            Run(15, Fwd(crouch: true));
            double flat = _s.HorizontalSpeed;

            // Uphill: the slope rises toward -X, so slide west into it.
            SetUp();
            BuildDownhill();
            Place(12, 0, 5);
            var west = new MoveInput { MoveZ = 1, Yaw = -Math.PI / 2, SprintHeld = true };
            while (_s.Position.X > 9.5 * B) Tick(west); // sprint up to the foot of the slope
            Tick(new MoveInput { MoveZ = 1, Yaw = -Math.PI / 2, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            Run(15, new MoveInput { MoveZ = 1, Yaw = -Math.PI / 2, CrouchHeld = true });
            Assert.That(_s.HorizontalSpeed, Is.LessThan(flat - 1.0));
        }

        [Test]
        public void DownhillSlideHasNoCooldown()
        {
            BuildDownhill();
            Place(0, 7 * B, 5);
            // Use the flat boost first, on the plateau.
            Run(20, Fwd(sprint: true));
            _s.LastSlideBoostTick = _s.Tick; // boost just used
            while (_s.Position.X < 3.5 * B) Tick(Fwd(sprint: true)); // now on the slope
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideStarted && e.Value == 1), Is.True);
        }

        // ---------- Slide-land ----------

        private void LaunchInAir(double height, double speed)
        {
            _s = MotorState.At(new Vec3d(3.5 * B, height, 5.5 * B));
            _s.Velocity = new Vec3d(speed, 0, 0);
            _s.Tick = 1000;
            _events.Clear();
        }

        [Test]
        public void SlideLandHoldingCrouchKeepsSpeed()
        {
            LaunchInAir(3.0, 12);
            for (int i = 0; i < 60 && _s.Mode == MoveMode.Air; i++) Tick(new MoveInput { Yaw = East, CrouchHeld = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(11.5));
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideLandSuccess), Is.True);
        }

        [Test]
        public void MissedSlideLandLosesSpeed()
        {
            LaunchInAir(3.0, 12);
            for (int i = 0; i < 60 && _s.Mode == MoveMode.Air; i++) Tick(new MoveInput { Yaw = East });
            Run(20, Fwd());
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.RunSpeed).Within(1e-6));
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideLandSuccess), Is.False);
        }

        [Test]
        public void LateSlideLandWithinWindowRestoresLandingSpeed()
        {
            LaunchInAir(3.0, 12);
            for (int i = 0; i < 60 && _s.Mode == MoveMode.Air; i++) Tick(new MoveInput { Yaw = East });
            Run(7, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Slide));
            Assert.That(_s.HorizontalSpeed, Is.GreaterThan(11.5));
        }

        [Test]
        public void SlideLandTooLateFails()
        {
            LaunchInAir(3.0, 12);
            for (int i = 0; i < 60 && _s.Mode == MoveMode.Air; i++) Tick(new MoveInput { Yaw = East });
            Run(10, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, CrouchHeld = true, CrouchPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(_events.Any(e => e.Type == MoveEventType.SlideLandSuccess), Is.False);
        }

        // ---------- Vault and mantle ----------

        [Test]
        public void SprintVaultsOneLevelWithoutLosingSpeed()
        {
            _g.Fill(new Int3(7, 0, 0), new Int3(7, 0, 31), Block.Solid);
            for (int i = 0; i < 90; i++) Tick(Fwd(sprint: true));
            Assert.That(_events.Any(e => e.Type == MoveEventType.Vaulted), Is.True);
            Assert.That(_s.Position.X, Is.GreaterThan(8 * B), "made it over");
            Assert.That(_s.HorizontalSpeed, Is.EqualTo(_c.SprintSpeed).Within(1e-6));
        }

        [Test]
        public void SlideVaultsOneLevel()
        {
            _g.Fill(new Int3(7, 0, 0), new Int3(7, 0, 31), Block.Solid);
            Place(1, 0, 5);
            StartSlideFromSprint();
            for (int i = 0; i < 40; i++) Tick(Fwd(crouch: true));
            Assert.That(_events.Any(e => e.Type == MoveEventType.Vaulted), Is.True);
            Assert.That(_s.Position.X, Is.GreaterThan(7 * B + 0.2), "on or past the obstacle");
        }

        [Test]
        public void RunningIntoOneLevelJustStops()
        {
            _g.Fill(new Int3(6, 0, 0), new Int3(6, 0, 31), Block.Solid);
            Run(90, Fwd());
            Assert.That(_events.Any(e => e.Type == MoveEventType.Vaulted), Is.False);
            Assert.That(_s.Position.X, Is.EqualTo(6 * B - 0.35 - 0.15).Within(1e-3), "stops against the wall's 0.15 m base fillet, not lifted onto it");
            Assert.That(_s.Position.Y, Is.EqualTo(0).Within(1e-3));
        }

        [Test]
        public void MantleTwoLevelLedgeTakesPointFourSeconds()
        {
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 1, 31), Block.Solid);
            Run(30, Fwd()); // pressed against the wall
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
            int ticks = 0;
            while (_s.Mode == MoveMode.Mantle && ticks < 100)
            {
                Assert.That(_s.CanShoot, Is.False);
                Tick(Fwd());
                ticks++;
            }
            Assert.That(ticks, Is.EqualTo(24));
            Run(10, Fwd());
            Assert.That(_s.Position.Y, Is.EqualTo(2 * B).Within(1e-3));
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Ground));
            Assert.That(Penetrating(), Is.False);
        }

        [Test]
        public void AirMantleCatchesALedge()
        {
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 1, 31), Block.Solid);
            Run(30, Fwd());
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true }); // mantles straight away from the ground
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));

            // From the air: fall past a 1-level ledge and press jump.
            SetUp();
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 0, 31), Block.Solid);
            _s = MotorState.At(new Vec3d(5 * B - 0.36, 0.6, 5.5 * B));
            Tick(new MoveInput { MoveZ = 1, Yaw = East, JumpPressed = true });
            Assert.That(_s.Mode, Is.EqualTo(MoveMode.Mantle));
        }

        // ---------- Ramps ----------

        [Test]
        public void WalkUpRampOntoBlockWithoutJumping()
        {
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 0, 31), Block.Ramp(Dir.West));
            _g.Fill(new Int3(6, 0, 0), new Int3(12, 0, 31), Block.Solid);
            int airTicks = 0;
            for (int i = 0; i < 90; i++)
            {
                Tick(Fwd());
                if (_s.Mode == MoveMode.Air) airTicks++;
                Assert.That(Penetrating(), Is.False);
            }
            Assert.That(_s.Position.Y, Is.EqualTo(B).Within(1e-3));
            Assert.That(_s.Position.X, Is.GreaterThan(6 * B));
            Assert.That(airTicks, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void WalkDownRampStaysGrounded()
        {
            _g.Fill(new Int3(0, 0, 0), new Int3(4, 0, 31), Block.Solid);
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 0, 31), Block.Ramp(Dir.East));
            Place(3, B, 5);
            int airTicks = 0;
            for (int i = 0; i < 90; i++)
            {
                Tick(Fwd(sprint: true));
                if (_s.Mode == MoveMode.Air) airTicks++;
            }
            Assert.That(_s.Position.Y, Is.EqualTo(0).Within(1e-3));
            Assert.That(airTicks, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void RampLaunchKeepsVerticalVelocity()
        {
            _g.Fill(new Int3(5, 0, 0), new Int3(5, 0, 31), Block.Ramp(Dir.West)); // rises toward east, drops after
            double peak = 0, launchVy = 0;
            for (int i = 0; i < 120; i++)
            {
                MoveMode before = _s.Mode;
                Tick(Fwd(sprint: true));
                if (before != MoveMode.Air && _s.Mode == MoveMode.Air && launchVy == 0) launchVy = _s.Velocity.Y;
                peak = Math.Max(peak, _s.Position.Y);
            }
            Assert.That(launchVy, Is.GreaterThan(7.0), "leaves the edge with the slope's upward speed");
            Assert.That(peak, Is.GreaterThan(B + 0.8));
        }

        // ---------- Sprint to aim, fall damage, crouch headroom ----------

        [Test]
        public void SprintToAimDelay()
        {
            Run(20, Fwd(sprint: true));
            Assert.That(_s.CanShoot, Is.False, "can't shoot while sprinting");
            Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, WantsToShoot = true });
            Assert.That(_s.Sprinting, Is.False);
            int t = 0;
            while (!_s.CanShoot && t < 30) { Tick(new MoveInput { MoveZ = 1, Yaw = East, SprintHeld = true, WantsToShoot = true }); t++; }
            Assert.That(t, Is.EqualTo(9), "0.15 s");
        }

        [TestCase(5, 0)]
        [TestCase(8, 20)]
        public void FallDamageOnlyAboveSixLevels(int levels, int damage)
        {
            _s = MotorState.At(new Vec3d(3.5 * B, levels * B, 5.5 * B));
            _events.Clear();
            for (int i = 0; i < 200 && !_events.Any(e => e.Type == MoveEventType.Landed); i++) Tick(new MoveInput { Yaw = East });
            MoveEvent land = _events.First(e => e.Type == MoveEventType.Landed);
            Assert.That(land.Amount, Is.EqualTo(levels).Within(0.01));
            Assert.That(land.Value, Is.EqualTo(damage));
        }

        [Test]
        public void StaysCrouchedWithoutHeadroom()
        {
            _g.Fill(new Int3(4, 1, 0), new Int3(8, 1, 31), Block.Anchor); // ceiling at 1.2 m
            Place(2, 0, 5);
            Run(10, Fwd(crouch: true));
            while (_s.Position.X < 5.5 * B) Tick(Fwd(crouch: true));
            Tick(Fwd());
            Assert.That(_s.Crouched, Is.True);
            Assert.That(Penetrating(), Is.False);
        }

        // ---------- Robustness ----------

        private static VoxelGrid BuildArena()
        {
            var g = new VoxelGrid(2, 2, 2, -16);
            g.FillBaseGround();
            var rng = new Random(1234);
            for (int i = 0; i < 120; i++)
            {
                int x = rng.Next(2, 30), z = rng.Next(2, 30), h = rng.Next(0, 3);
                for (int y = 0; y < h; y++) g.Set(x, y, z, Block.Solid);
                if (rng.Next(3) == 0) g.Set(x, h, z, Block.Ramp(DirUtil.Horizontal[rng.Next(4)]));
            }
            return g;
        }

        private static MotorState Fuzz(VoxelGrid g, MovementConfig c, int seed, int ticks, Action<MotorState> check)
        {
            var rng = new Random(seed);
            var s = MotorState.At(new Vec3d(16 * B, 6, 16 * B));
            double yaw = 0;
            var input = new MoveInput();
            for (int t = 0; t < ticks; t++)
            {
                if (t % 10 == 0)
                {
                    yaw += (rng.NextDouble() - 0.5) * 2;
                    input = new MoveInput
                    {
                        MoveX = rng.Next(3) - 1,
                        MoveZ = rng.Next(3) - 1,
                        Yaw = yaw,
                        SprintHeld = rng.Next(2) == 0,
                        CrouchHeld = rng.Next(3) == 0,
                    };
                }
                var i = input;
                i.JumpPressed = rng.Next(15) == 0;
                i.CrouchPressed = i.CrouchHeld && rng.Next(4) == 0;
                PlayerMovement.Step(g, c, ref s, i);
                check(s);
            }
            return s;
        }

        [Test]
        public void FuzzNeverPenetratesGeometry()
        {
            var g = BuildArena();
            var c = new MovementConfig();
            Fuzz(g, c, 42, 3000, s =>
            {
                bool inside = VoxelGeometry.BoxOverlapsSolid(g, PlayerMovement.Box(s.Position, c.HalfWidth, PlayerMovement.HeightOf(s, c)));
                Assert.That(inside, Is.False, $"tick {s.Tick} mode {s.Mode} pos {s.Position}");
            });
        }

        [Test]
        public void MovementIsDeterministic()
        {
            var g = BuildArena();
            var c = new MovementConfig();
            MotorState a = Fuzz(g, c, 7, 1500, _ => { });
            MotorState b = Fuzz(g, c, 7, 1500, _ => { });
            Assert.That(a.Position, Is.EqualTo(b.Position));
            Assert.That(a.Velocity, Is.EqualTo(b.Velocity));
            Assert.That(a.Mode, Is.EqualTo(b.Mode));
        }
    }
}
