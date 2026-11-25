using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The movement showcase (Ben 2026-11-21, dev key F6): a tool-assisted run over <see cref="MovementCourse"/>.
    /// It only ever produces normal player inputs (move keys, yaw, jump, crouch, sprint), one per tick, decided from
    /// the player's own movement state, and they go through the real movement code like any other input. So it can
    /// only do what a player with perfect timing could do. A test runs it every build: if a movement change breaks
    /// the route, the test fails.
    /// </summary>
    public sealed class ShowcaseRun
    {
        public enum Phase
        {
            Start, StairSlide, DeckC, GapOne, LandingRamp, CornerJump, CornerKick, CorridorF, GapTwoAir, AfterGapTwo,
            LedgeJump, Mantle, DeckG, Brake, Done, Failed,
        }

        private readonly CourseInfo _c;
        private readonly MovementConfig _m;
        public Phase Current { get; private set; } = Phase.Start;
        public int Ticks { get; private set; }
        /// <summary>Highest horizontal speed reached, for the summary line.</summary>
        public double TopSpeed { get; private set; }
        public string FailReason { get; private set; }

        private bool _jumpWasHeld;
        private int _phaseTicks;
        private const double North = 0, East = Math.PI / 2;

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
            bool jump = false;
            Vec3d p = s.Position;

            if (Ticks > SimTime.TicksPerSecond * 30) Fail("took longer than 30 s");
            if (!Finished && p.Y < 3.0 && Current != Phase.Start) Fail($"fell off in {Current}");

            switch (Current)
            {
                case Phase.Start:
                    // Sprint off the start, then slide just before the stairs.
                    if (s.Mode == MoveMode.Ground && s.HorizontalSpeed >= _m.SlideMinStartSpeed && p.Z > _c.Mark("stairs-top").Z - 1.5)
                    {
                        i.CrouchPressed = i.CrouchHeld = true;
                        Go(Phase.StairSlide);
                    }
                    break;

                case Phase.StairSlide:
                    // Stairs collide as a ramp: the slide builds speed all the way down.
                    i.CrouchHeld = true;
                    i.Yaw = Steer(p, _c.Mark("stairs-bottom").X, North);
                    if (p.Z >= _c.Mark("stairs-bottom").Z - 0.3 && s.Grounded) { jump = true; Go(Phase.DeckC); }
                    break;

                case Phase.DeckC:
                    // Slide-land, then hop (or jump right at the edge) over the first gap.
                    i.CrouchHeld = true;
                    jump = FlatFlow(s, _c.Mark("gap-1").Z - p.Z, out bool atEdge);
                    if (atEdge) Go(Phase.GapOne);
                    break;

                case Phase.GapOne:
                    i.CrouchHeld = true; // slide-land on the downhill ramp
                    if (s.Mode == MoveMode.Slide && p.Z > _c.Mark("gap-1").Z) Go(Phase.LandingRamp);
                    break;

                case Phase.LandingRamp:
                    i.CrouchHeld = true;
                    // Drift to the east side of the lane for the corner, slide the ramp, hop the deck.
                    i.Yaw = Steer(p, _c.Mark("corner-edge").X + 2.3, North);
                    jump = FlatFlow(s, _c.Mark("corner-edge").Z - p.Z, out bool atCorner);
                    if (atCorner) Go(Phase.CornerJump);
                    break;

                case Phase.CornerJump:
                    i.CrouchHeld = true;
                    i.MoveZ = 0;
                    // Kick the moment the north wall is hit, looking east.
                    if (s.Tick - s.WallHitTick <= 1 && s.WallHitNormal.Z < -0.5)
                    {
                        i.Yaw = East;
                        jump = true;
                        Go(Phase.CornerKick);
                    }
                    break;

                case Phase.CornerKick:
                    i.Yaw = East;
                    i.MoveZ = 0;
                    i.CrouchHeld = true;
                    if (s.Mode == MoveMode.Slide || s.Mode == MoveMode.Ground) Go(Phase.CorridorF);
                    break;

                case Phase.CorridorF:
                {
                    // Hop down the corridor hugging the north wall. The hop that carries over the gap drifts into
                    // the wall, and the kick off it carries you across.
                    i.CrouchHeld = true;
                    Vec3d gap = _c.Mark("gap-2");
                    i.Yaw = Steer(p, _c.Mark("corner-wall").Z - 0.5, East);
                    jump = FlatFlow(s, 1000, out _);
                    if (s.Mode == MoveMode.Air && p.X > gap.X - 1.0) Go(Phase.GapTwoAir);
                    if (s.Mode == MoveMode.Ground) Fail("slide ended in the corridor");
                    break;
                }

                case Phase.GapTwoAir:
                    // Drift into the wall late in the hop: a kick while falling buys a whole new arc.
                    i.CrouchHeld = true;
                    i.Yaw = East - 0.6;
                    if (s.Velocity.Y < -1 && p.Z >= _c.Mark("corner-wall").Z - _m.HalfWidth - _m.WallKickProbe + 0.02)
                    {
                        i.Yaw = East + 0.1;
                        jump = true;
                        Go(Phase.AfterGapTwo);
                    }
                    break;

                case Phase.AfterGapTwo:
                    i.CrouchHeld = true;
                    i.MoveZ = 0;
                    i.Yaw = East;
                    if (s.Mode == MoveMode.Slide || s.Mode == MoveMode.Ground)
                    {
                        // Hop on: the hop that reaches the ledge wall turns into an air mantle.
                        i.MoveZ = 1;
                        i.Yaw = Steer(p, _c.Mark("ledge").Z, East);
                        jump = FlatFlow(s, 1000, out _);
                        if (jump && p.X > _c.Mark("gap-2-end").X + 1) Go(Phase.LedgeJump);
                    }
                    break;

                case Phase.LedgeJump:
                    i.Yaw = East;
                    // Jump again against the ledge wall: an air mantle that keeps the speed you hit it with.
                    i.CrouchHeld = true;
                    if (s.Tick - s.WallHitTick <= 1 && s.WallHitNormal.X < -0.5) { jump = true; Go(Phase.Mantle); }
                    else if (s.Mode == MoveMode.Slide) { jump = FlatFlow(s, 1000, out _); } // short of the wall: hop again
                    break;

                case Phase.Mantle:
                    i.Yaw = East;
                    i.CrouchHeld = true;
                    if (s.Mode == MoveMode.Mantle && s.ActionTick + 1 >= _m.MantleCancelFromTick &&
                        s.ActionTargetY - s.Position.Y <= _m.MantleCancelMaxRemaining - 0.2) jump = true;
                    if (s.Mode != MoveMode.Mantle && _phaseTicks > 2) Go(Phase.DeckG);
                    break;

                case Phase.DeckG:
                {
                    // Sprint into the block (a sprint vault keeps your speed), then brake on the pad.
                    i.Yaw = Steer(p, _c.Mark("vault").Z, East);
                    double stopDist = BrakeDistance(s.HorizontalSpeed);
                    if (p.X > _c.Mark("vault").X + 1 && p.X >= _c.Mark("finish").X - stopDist && s.Mode != MoveMode.Air && s.Mode != MoveMode.Vault)
                        Go(Phase.Brake);
                    break;
                }

                case Phase.Brake:
                    // Counter-strafe: press back against the motion; let go once stopped.
                    i.Yaw = East;
                    i.SprintHeld = false;
                    i.MoveZ = s.HorizontalSpeed > 0.4 && s.Velocity.X > 0 ? -1 : 0;
                    if (s.HorizontalSpeed < 0.05 && s.Mode == MoveMode.Ground)
                    {
                        if (_c.Finish.Contains(p, 0.01)) Go(Phase.Done);
                        else Fail($"stopped outside the finish pad at {p}");
                    }
                    break;

                default:
                    i = new MoveInput { Yaw = East };
                    break;
            }

            i.JumpPressed = jump && !_jumpWasHeld;
            i.JumpHeld = jump;
            _jumpWasHeld = jump;
            return i;
        }

        /// <summary>
        /// On flat ground while sliding: hop (slide-jump, then slide-land) to stop the slide friction eating speed, but
        /// never a hop that would come down short of an edge; jump at the edge itself. True = press jump now.
        /// </summary>
        private bool FlatFlow(in MotorState s, double toEdge, out bool atEdge)
        {
            atEdge = false;
            if (s.Mode != MoveMode.Slide || s.GroundNormal.Y < 0.999) return false;
            if (toEdge <= 1.2) { atEdge = true; return true; }
            double hop = s.HorizontalSpeed * 2 * _m.JumpSpeed / _m.Gravity;
            return toEdge > hop + 1.5;
        }

        /// <summary>How far a counter-strafe brake from this speed slides you (brake to its floor, then normal decel).</summary>
        private double BrakeDistance(double v)
        {
            double floor = _m.CounterBrakeMinSpeed;
            double d = 0;
            if (v > floor) d += (v * v - floor * floor) / (2 * _m.CounterBrakeDecel);
            double u = Math.Min(v, floor);
            d += u * u / (2 * (_m.GroundAccel + _m.GroundDecel) * 0.5);
            return d + 0.4;
        }

        /// <summary>Yaw that heads along <paramref name="baseYaw"/> while easing toward a lane line.</summary>
        private static double Steer(Vec3d p, double line, double baseYaw)
        {
            // North-bound: line is an X; east-bound: line is a Z (north is +Z, i.e. a smaller yaw).
            double off = baseYaw == North ? line - p.X : -(line - p.Z);
            return baseYaw + Math.Max(-0.35, Math.Min(0.35, off * 0.25));
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
