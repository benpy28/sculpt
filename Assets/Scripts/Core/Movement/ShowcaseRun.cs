using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The movement showcase (dev keys F6 race / F7 watch): a tool-assisted run over <see cref="MovementCourse"/>. It
    /// only ever produces normal player inputs (move keys, yaw, jump, crouch, sprint), one per tick, decided from the
    /// player's own movement state, and they go through the real movement code like any other input. So it can only do
    /// what a player with perfect timing could do. It is meant to be close to the fastest line and hard to match: it
    /// slides every metre it can, hops flats on the exact tick, re-boosts its slide the tick the cooldown allows, climbs
    /// with a climb-cancel, carves the U-turn and the slalom on the limit. A test runs it every build: if a movement
    /// change breaks the route, the test fails.
    /// </summary>
    public sealed class ShowcaseRun
    {
        public enum Phase
        {
            Start, StairSlide, Beam, Kicker, KickerAir, ToLedge, Climb, Deck, BlockClimb, DownFlight, UTurn, Slalom,
            Hill, Finish, Brake, Done, Failed,
        }

        private readonly CourseInfo _c;
        private readonly MovementConfig _m;
        public Phase Current { get; private set; } = Phase.Start;
        public int Ticks { get; private set; }
        /// <summary>Ticks from the first input to the first tick inside the finish pad (0 until then).</summary>
        public int FinishTicks { get; private set; }
        /// <summary>Highest horizontal speed reached, for the summary line.</summary>
        public double TopSpeed { get; private set; }
        public string FailReason { get; private set; }

        private bool _jumpWasHeld;
        private bool _crouchWasHeld;
        private int _phaseTicks;
        private int _door;
        private const double North = 0, South = Math.PI;

        /// <summary>Radius of the U-turn's carve round the pillar.</summary>
        public const double TurnRadius = 7.0;

        public ShowcaseRun(CourseInfo course, MovementConfig movement)
        {
            _c = course;
            _m = movement;
        }

        public bool Finished => Current == Phase.Done || Current == Phase.Failed;

        /// <summary>The input for the next tick, given the player's current state.</summary>
        public MoveInput Next(in MotorState s)
        {
            Ticks++;
            _phaseTicks++;
            TopSpeed = Math.Max(TopSpeed, s.HorizontalSpeed);
            var i = new MoveInput { Yaw = North, MoveZ = 1, SprintHeld = true };
            bool jump = false, crouch = true;
            Vec3d p = s.Position;
            if (FinishTicks == 0 && Current != Phase.Start && _c.Finish.Contains(p, 0.01)) FinishTicks = Ticks;

            if (Ticks > SimTime.TicksPerSecond * 60) Fail("took longer than 60 s");

            switch (Current)
            {
                case Phase.Start:
                    // Sprint off the start, slide just before the flight.
                    crouch = s.Mode == MoveMode.Ground && s.HorizontalSpeed >= _m.SlideMinStartSpeed && p.Z > _c.Mark("stairs-top").Z - 1.2;
                    if (crouch) Go(Phase.StairSlide);
                    break;

                case Phase.StairSlide:
                    i.Yaw = Line(p, _c.Mark("stairs-top").X, North);
                    if (s.Mode != MoveMode.Air && p.Z > _c.Mark("stairs-bottom").Z) Go(Phase.Beam);
                    break;

                case Phase.Beam:
                    // Stay low under the beam; it is behind you before the deck's lip.
                    i.Yaw = Line(p, _c.Mark("beam-1").X, North);
                    if (p.Z > _c.Mark("beam-1").Z + 1.2) Go(Phase.Kicker);
                    break;

                case Phase.Kicker:
                {
                    // Hop the deck if a hop lands short of the lip, then slide-jump off the lip itself.
                    Vec3d lip = _c.Mark("lip");
                    i.Yaw = Line(p, lip.X, North);
                    double toLip = lip.Z - p.Z;
                    jump = Hop(s, toLip) && toLip > 1.2 || (s.Mode == MoveMode.Slide && toLip < 0.4);
                    crouch = toLip < 3 || !Reboost(s);
                    if (s.Mode == MoveMode.Air && toLip < 0.4) Go(Phase.KickerAir);
                    break;
                }

                case Phase.KickerAir:
                    // Long flight; slide-land on the downhill ramp.
                    i.Yaw = North;
                    if (s.Mode == MoveMode.Slide && _phaseTicks > 2) Go(Phase.ToLedge);
                    break;

                case Phase.ToLedge:
                {
                    // Hop at the foot of the ledge and hold jump: the air touch climbs it.
                    double toWall = _c.Mark("ledge").Z - _m.HalfWidth - p.Z;
                    i.Yaw = Line(p, _c.Mark("ledge").X, North);
                    jump = Hop(s, toWall) || (s.Mode == MoveMode.Air && toWall < 2.0 && _jumpWasHeld);
                    crouch = !Reboost(s);
                    if (s.Mode == MoveMode.Mantle) Go(Phase.Climb);
                    break;
                }

                case Phase.Climb:
                    jump = ClimbCancel(s);
                    if (s.Mode != MoveMode.Mantle && _phaseTicks > 1) Go(Phase.Deck);
                    break;

                case Phase.Deck:
                {
                    double toWall = _c.Mark("block").Z - _m.HalfWidth - p.Z;
                    i.Yaw = Line(p, _c.Mark("block").X, North);
                    jump = Hop(s, toWall) || (s.Mode == MoveMode.Air && toWall < 2.0 && _jumpWasHeld);
                    if (s.Mode == MoveMode.Mantle) Go(Phase.BlockClimb);
                    break;
                }

                case Phase.BlockClimb:
                    jump = ClimbCancel(s);
                    if (s.Mode != MoveMode.Mantle && _phaseTicks > 1) Go(Phase.DownFlight);
                    break;

                case Phase.DownFlight:
                    i.Yaw = Line(p, _c.Mark("flight-2-bottom").X, North);
                    if (s.Mode == MoveMode.Slide && p.Z > _c.Mark("flight-2-bottom").Z + 1) Go(Phase.UTurn);
                    break;

                case Phase.UTurn:
                {
                    // Carve round the pillar on a fixed radius (clockwise: north, east, south).
                    Vec3d cpt = _c.Mark("turn-centre");
                    double dx = p.X - cpt.X, dz = p.Z - cpt.Z, r = Math.Sqrt(dx * dx + dz * dz);
                    double theta = Math.Atan2(dx, dz);
                    if (dz < -3 && dx < 0) i.Yaw = Line(p, cpt.X - TurnRadius, North); // still on the way up
                    else i.Yaw = theta + Math.PI / 2 + Math.Max(-0.6, Math.Min(0.6, 0.35 * (r - TurnRadius)));
                    crouch = !Reboost(s);
                    if (dx > 0 && dz < 0 && Math.Abs(theta) > Math.PI * 0.85) { _door = 1; Go(Phase.Slalom); }
                    break;
                }

                case Phase.Slalom:
                {
                    // Through each door, then aim through the next one: a carve at every wall.
                    Vec3d d = _c.Mark("door-" + _door);
                    if (p.Z < d.Z - 0.8)
                    {
                        _door++;
                        if (_door > MovementCourse.SlalomDoors) { Go(Phase.Hill); break; }
                        d = _c.Mark("door-" + _door);
                    }
                    Vec3d after = _door < MovementCourse.SlalomDoors ? _c.Mark("door-" + (_door + 1)) : _c.Mark("hill-foot");
                    // The racing line: through each door close to its inside edge (the side the next door is on), so
                    // every carve is as small as it can be.
                    double inside = Math.Sign(after.X - d.X) * (MovementCourse.DoorWidth / 2 - 1.5);
                    double tx = d.X + inside, tz = d.Z - 1.0;
                    i.Yaw = Math.Atan2(tx - p.X, tz - p.Z);
                    // No hops here: in the air you can't carve, and the next door is always off to the side.
                    crouch = !Reboost(s);
                    break;
                }

                case Phase.Hill:
                {
                    // Hop at the ledge and hold jump: the air touch climbs it, cancelled at the top; slide the downhill.
                    Vec3d foot = _c.Mark("hill-foot");
                    i.Yaw = Line(p, foot.X, South);
                    double toWall = p.Z - foot.Z - _m.HalfWidth;
                    if (s.Mode == MoveMode.Mantle) jump = ClimbCancel(s);
                    else if (p.Z > foot.Z) jump = Hop(s, toWall) || (s.Mode == MoveMode.Air && toWall < 2.0 && _jumpWasHeld);
                    crouch = s.Mode == MoveMode.Mantle || p.Z < foot.Z || !Reboost(s);
                    if (p.Z < _c.Mark("hill-bottom").Z) Go(Phase.Finish);
                    break;
                }

                case Phase.Finish:
                {
                    i.Yaw = Line(p, _c.Mark("finish").X, South);
                    double beam = _c.Mark("beam-2").Z;
                    if (p.Z > beam + 1.5) jump = Hop(s, p.Z - beam - 1.5) && p.Z - beam - 1.5 > 1.2; // never into the beam
                    crouch = !Reboost(s);
                    if (p.Z <= _c.Finish.Max.Z + BrakeDistance(s.HorizontalSpeed) - 1.0 && p.Z < beam - 0.5 && s.Mode != MoveMode.Air) Go(Phase.Brake);
                    break;
                }

                case Phase.Brake:
                    // Counter-strafe: stand up and press back against the motion; let go once stopped.
                    crouch = false;
                    i.Yaw = South;
                    i.SprintHeld = false;
                    i.MoveZ = s.HorizontalSpeed > 0.4 && s.Velocity.Z < 0 ? -1 : 0;
                    if (s.HorizontalSpeed < 0.05 && s.Mode == MoveMode.Ground)
                    {
                        if (_c.Finish.Contains(p, 0.01)) Go(Phase.Done);
                        else Fail($"stopped outside the finish pad at {p}");
                    }
                    break;

                default:
                    i = new MoveInput { Yaw = South };
                    crouch = false;
                    break;
            }

            // Never stuck on a wall: run into one in the air and the held jump climbs it (the one ledge-climb move).
            if (s.Mode == MoveMode.Air && s.Tick - s.WallHitTick <= 2 * SimTime.Per60 && Current != Phase.Brake && !Finished) jump = true;

            i.JumpPressed = jump && !_jumpWasHeld;
            i.JumpHeld = jump;
            _jumpWasHeld = jump;
            i.CrouchPressed = crouch && !_crouchWasHeld;
            i.CrouchHeld = crouch;
            _crouchWasHeld = crouch;
            return i;
        }

        /// <summary>
        /// On flat ground while sliding: hop (slide-jump, then slide-land) so slide friction doesn't eat speed, but never
        /// a hop that would come down within 1.5 m of the next obstacle; jump at the obstacle itself. True = press jump.
        /// </summary>
        private bool Hop(in MotorState s, double toEdge)
        {
            if (s.Mode != MoveMode.Slide || s.GroundNormal.Y < 0.999) return false;
            if (toEdge <= 1.2) return toEdge > -0.5;
            double hop = s.HorizontalSpeed * 2 * _m.JumpSpeed / _m.Gravity;
            return toEdge > hop + 1.5;
        }

        /// <summary>
        /// Slide re-boost: when a ground slide has slowed below the boost speed and the flat cooldown has run out, stand
        /// up for two ticks (back to a sprint) and slide again. True = let go of crouch this tick.
        /// </summary>
        private bool Reboost(in MotorState s)
        {
            bool ready = s.Tick + 2 - s.LastSlideBoostTick >= _m.SlideBoostCooldownTicks;
            if (s.Mode == MoveMode.Slide && s.GroundNormal.Y > 0.999 && ready && s.HorizontalSpeed < _m.SlideBoostSpeed - 0.8 &&
                s.HorizontalSpeed > _m.SlideMinStartSpeed)
                return true;
            if (s.Mode == MoveMode.Ground && !s.Sprinting) return true; // the stand-up tick: become a sprint first
            return false;
        }

        /// <summary>Jump once near the top of a climb: pop over the ledge with the speed you hit it with.</summary>
        private bool ClimbCancel(in MotorState s) =>
            s.Mode == MoveMode.Mantle && s.ActionTick + 1 >= Math.Min(_m.MantleCancelFromTick, PlayerMovement.MantleRiseTicksOf(_m, s) / 2) &&
            s.ActionTargetY - s.Position.Y <= _m.MantleCancelMaxRemaining - 0.2;

        /// <summary>How far a counter-strafe brake from this speed slides you (brake to its floor, then normal decel).</summary>
        private double BrakeDistance(double v)
        {
            double floor = _m.CounterBrakeMinSpeed;
            double d = 0;
            if (v > floor) d += (v * v - floor * floor) / (2 * _m.CounterBrakeDecel);
            double u = Math.Min(v, floor);
            d += u * u / (2 * (_m.GroundAccel + _m.GroundDecel) * 0.5);
            return d + 0.6;
        }

        /// <summary>Yaw that heads along <paramref name="baseYaw"/> (north or south) while easing toward a lane line x.</summary>
        private static double Line(Vec3d p, double lineX, double baseYaw)
        {
            double off = lineX - p.X;
            if (baseYaw == South) off = -off;
            return baseYaw + Math.Max(-0.35, Math.Min(0.35, off * 0.25));
        }

        private static double Angle(double a, double b)
        {
            double d = b - a;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            return d;
        }

        private void Go(Phase p)
        {
            Current = p;
            _phaseTicks = 0;
        }

        private void Fail(string why)
        {
            if (Finished) return;
            FailReason = why;
            Current = Phase.Failed;
        }
    }
}
