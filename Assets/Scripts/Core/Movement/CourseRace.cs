using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// A timed run of the movement course against the TAS (Ben 2026-11-25, dev key F6): a 3-2-1 countdown at the start
    /// (you can look around but not move), then the clock runs until you first enter the finish pad. The TAS ghost is
    /// recorded once, up front, by running <see cref="ShowcaseRun"/> through the real movement code from the same start,
    /// and replayed tick for tick from GO, so it is exactly what the TAS would do in your place. Pure data: the game draws
    /// the ghost and the clock.
    /// </summary>
    public sealed class CourseRace
    {
        public static readonly int CountdownTicks = 3 * SimTime.TicksPerSecond;

        private readonly CourseInfo _course;
        /// <summary>The ghost's feet per run tick (index 0 = GO).</summary>
        public readonly Vec3d[] Ghost;
        /// <summary>The ghost's facing per run tick (radians, 0 = north).</summary>
        public readonly double[] GhostYaw;
        /// <summary>Run ticks the TAS takes to reach the finish pad (0 if it never did).</summary>
        public readonly int GhostFinishTicks;

        /// <summary>Ticks since the race began, countdown included.</summary>
        public int Tick { get; private set; }
        public bool Started => Tick >= CountdownTicks;
        /// <summary>Ticks since GO (0 during the countdown).</summary>
        public int RunTicks => Math.Max(0, Tick - CountdownTicks);
        /// <summary>Run ticks at which you first entered the finish pad (0 until then).</summary>
        public int PlayerFinishTicks { get; private set; }
        public bool PlayerFinished => PlayerFinishTicks > 0;
        /// <summary>Seconds left on the countdown, 3, 2, 1, then 0 (GO).</summary>
        public int CountdownSeconds => Started ? 0 : (CountdownTicks - Tick + SimTime.TicksPerSecond - 1) / SimTime.TicksPerSecond;

        public CourseRace(CourseInfo course, Vec3d[] ghost, double[] ghostYaw, int ghostFinishTicks)
        {
            _course = course;
            Ghost = ghost;
            GhostYaw = ghostYaw;
            GhostFinishTicks = ghostFinishTicks;
        }

        /// <summary>Runs the TAS from the course start (as a warp puts you there) and records its path.</summary>
        public static CourseRace Begin(ITerrain terrain, CourseInfo course, MovementConfig movement)
        {
            var run = new ShowcaseRun(course, movement);
            MotorState s = MotorState.At(course.Start);
            var path = new List<Vec3d> { s.Position };
            var yaw = new List<double> { course.StartYaw };
            int limit = SimTime.TicksPerSecond * 90;
            while (!run.Finished && path.Count < limit)
            {
                MoveInput i = run.Next(s);
                PlayerMovement.Step(terrain, movement, ref s, i);
                path.Add(s.Position);
                yaw.Add(i.Yaw);
            }
            return new CourseRace(course, path.ToArray(), yaw.ToArray(), run.FinishTicks);
        }

        /// <summary>One simulation tick: call after the player's tick with the player's new state.</summary>
        public void Step(in MotorState player)
        {
            Tick++;
            if (Started && !PlayerFinished && RunTicks > 0 && _course.Finish.Contains(player.Position, 0.01)) PlayerFinishTicks = RunTicks;
        }

        /// <summary>The ghost's feet at a (fractional) run tick; it waits at the start during the countdown and stays
        /// where it stopped once its run is over.</summary>
        public Vec3d GhostAt(double runTick)
        {
            if (Ghost.Length == 0) return _course.Start;
            if (runTick <= 0) return Ghost[0];
            int k = (int)Math.Floor(runTick);
            if (k >= Ghost.Length - 1) return Ghost[Ghost.Length - 1];
            double f = runTick - k;
            return Ghost[k] + (Ghost[k + 1] - Ghost[k]) * f;
        }

        public double GhostYawAt(double runTick)
        {
            if (GhostYaw.Length == 0) return _course.StartYaw;
            int k = Math.Max(0, Math.Min(GhostYaw.Length - 1, (int)Math.Floor(runTick)));
            return GhostYaw[k];
        }
    }
}
