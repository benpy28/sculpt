using System;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Jumping on stairs (Ben 2026-11-25: "stairs feel weird, especially when jumping"). The cause was the air move:
    /// a jump that came down on the slope, or a jump taken on it going up, ran into the slope sideways first and was
    /// stopped like a wall, so every jump on stairs ended in a dead stop. Now the air move rides up a walkable slope.
    /// </summary>
    public class StairJumpTests
    {
        private BrushWorld _w;
        private ITerrain _t;
        private MovementConfig _c;
        private Brush _flight;
        private double _x, _yaw;

        [SetUp]
        public void SetUp()
        {
            _w = DuelMap.BuildBrushes(out BreachMapInfo info);
            _t = DuelMap.Terrain(DuelMap.BuildGround(), _w);
            _c = new MovementConfig();
            _flight = info.Block.Climbs[0];
            Assume.That(_flight.Rise, Is.EqualTo(Dir.North));
            _x = (_flight.Bounds.Min.X + _flight.Bounds.Max.X) / 2;
            _yaw = 0;
        }

        private MotorState Settle(Vec3d at)
        {
            MotorState s = MotorState.At(at);
            s.Tick = 1000;
            for (int k = 0; k < 30; k++) PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = _yaw });
            return s;
        }

        private void Sprint(ref MotorState s, bool jump) =>
            PlayerMovement.Step(_t, _c, ref s, new MoveInput { Yaw = _yaw, MoveZ = 1, SprintHeld = true, JumpPressed = jump, JumpHeld = jump });

        [Test]
        public void SprintJumpOntoTheStairs_KeepsItsSpeedWhenItComesDownOnTheSlope()
        {
            MotorState s = Settle(new Vec3d(_x, 0, _flight.Bounds.Min.Z - 2));
            for (int k = 0; k < 10; k++) Sprint(ref s, false);
            Sprint(ref s, true);
            bool landed = false;
            double minSpeed = double.MaxValue;
            for (int k = 0; k < 60 && !landed; k++)
            {
                Sprint(ref s, false);
                minSpeed = Math.Min(minSpeed, s.Velocity.HorizontalLength);
                landed = s.Mode == MoveMode.Ground && s.Position.Y > 0.5;
            }
            Assert.That(landed, Is.True, "came down on the flight");
            Assert.That(s.GroundNormal.Y, Is.LessThan(0.95), "on the slope");
            Assert.That(minSpeed, Is.GreaterThan(_c.SprintSpeed - 0.05), "never stopped by the slope");
        }

        [Test]
        public void JumpingWhileRunningUpTheStairs_NeverLosesSpeed()
        {
            // Start on the flight's upper part, in the open middle of the stairwell (no slab overhead).
            double z = _flight.Bounds.Min.Z + (_flight.Bounds.Max.Z - _flight.Bounds.Min.Z) * 0.45;
            MotorState s = Settle(new Vec3d(_x, _flight.Bounds.Min.Y + 0.45 * 4.8 + 0.3, z));
            for (int k = 0; k < 12; k++) Sprint(ref s, false);
            Assume.That(s.Mode, Is.EqualTo(MoveMode.Ground));
            Assume.That(s.Velocity.HorizontalLength, Is.EqualTo(_c.SprintSpeed).Within(0.01));
            Sprint(ref s, true);
            Assert.That(s.Mode, Is.EqualTo(MoveMode.Air));
            for (int k = 0; k < 20; k++)
            {
                Sprint(ref s, false);
                Assert.That(s.Velocity.HorizontalLength, Is.GreaterThan(_c.SprintSpeed - 0.05), $"tick {k} after the jump at {s.Position}");
            }
        }

        [Test]
        public void LandingOnTheWayUp_HappensAsSoonAsYouStopRisingFasterThanTheSlope()
        {
            // A jump up the flight lands on the slope once its climb slows to the slope's own climb, not only after
            // the apex (it would otherwise glide up the steps in the air).
            double z = _flight.Bounds.Min.Z + (_flight.Bounds.Max.Z - _flight.Bounds.Min.Z) * 0.3;
            MotorState s = Settle(new Vec3d(_x, 0.3 * 4.8 + 0.3, z));
            // Running, not sprinting, so the whole jump stays over the flight.
            void Run(ref MotorState m, bool jump) =>
                PlayerMovement.Step(_t, _c, ref m, new MoveInput { Yaw = _yaw, MoveZ = 1, JumpPressed = jump, JumpHeld = jump });
            for (int k = 0; k < 12; k++) Run(ref s, false);
            Run(ref s, true);
            int air = 0;
            while (s.Mode == MoveMode.Air && air < 80) { Run(ref s, false); air++; }
            Assert.That(s.Position.Z, Is.LessThan(_flight.Bounds.Max.Z), "landed on the flight");
            Assert.That(s.Mode, Is.Not.EqualTo(MoveMode.Air));
            // Same airtime as a jump on flat ground (relative to the slope, a jump is a normal jump): ~2 * 8.8 / 30 s.
            int flat = (int)Math.Round(2 * _c.JumpSpeed / _c.Gravity * 60);
            Assert.That(air, Is.InRange(flat - 3, flat + 3));
        }

        [Test]
        public void AWallStillStopsAnAirMove()
        {
            // Not every block becomes a slope: a jump into a plain wall tile still stops (wall kicks need that).
            var w = new BrushWorld();
            w.AddBox(new Vec3d(-20, -1, -20), new Vec3d(20, 0, 20), BrushKind.Ground);
            w.AddBox(new Vec3d(-5, 0, 3), new Vec3d(5, 3, 3.3), BrushKind.Wall);
            MotorState s = MotorState.At(new Vec3d(0, 0, 0));
            s.Tick = 1000;
            for (int k = 0; k < 10; k++) PlayerMovement.Step(w, _c, ref s, new MoveInput { Yaw = 0, MoveZ = 1, SprintHeld = true });
            PlayerMovement.Step(w, _c, ref s, new MoveInput { Yaw = 0, MoveZ = 1, SprintHeld = true, JumpPressed = true, JumpHeld = true });
            for (int k = 0; k < 20; k++) PlayerMovement.Step(w, _c, ref s, new MoveInput { Yaw = 0, MoveZ = 1, SprintHeld = true });
            Assert.That(s.Position.Z, Is.LessThan(3 - _c.HalfWidth + 1e-6));
            Assert.That(Math.Abs(s.Velocity.Z), Is.LessThan(1.0));
        }
    }
}
