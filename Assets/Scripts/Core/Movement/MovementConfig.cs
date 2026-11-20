using System;

namespace Sculpt.Core
{
    /// <summary>Every movement number. Game fills it from a ScriptableObject. Metres, seconds, ticks at 60 Hz.</summary>
    public sealed class MovementConfig
    {
        public double Dt = 1.0 / SimTime.TicksPerSecond;

        // Speeds
        public double RunSpeed = 6.0;
        public double SprintSpeed = 8.5;
        public double CrouchSpeed = 3.0;

        // Acceleration: snappy, never floaty.
        public double GroundAccel = 60.0;     // 0 -> run in 0.1 s
        public double GroundDecel = 50.0;     // run -> stop in 0.12 s
        public double OverspeedDecel = 30.0;  // bleeding speed above the target (e.g. after a slide or landing)
        public double AirAccelFraction = 0.15;

        // Vertical
        public double Gravity = 30.0;
        public double JumpApex = 1.3;
        public double JumpSpeed => Math.Sqrt(2 * Gravity * JumpApex);
        public double MaxFallSpeed = 40.0;
        public int CoyoteTicks = 5;      // jump still allowed this long after walking off a ledge
        public int JumpBufferTicks = 6;  // a jump pressed this early before landing still fires

        // Body
        public double HalfWidth = 0.35;
        public double StandHeight = 1.8;
        public double StandEye = 1.62;
        public double CrouchHeight = 1.1;
        public double CrouchEye = 0.95;

        // Slide
        public double SlideBoostSpeed = 10.0;
        public double SlideFriction = 7.0;          // 10 -> 3 m/s in ~1 s on flat ground
        public double SlideMaxSpeed = 14.0;
        public double SlideEndSpeed = 3.0;
        public double SlideMinStartSpeed = 5.0;     // must be sprinting at about this speed to slide
        public double SlideTurnRate = 1.6;          // rad/s of steering while sliding
        public int SlideBoostCooldownTicks = 90;    // 1.5 s, flat ground only
        public int SlideLandWindowTicks = 9;        // 150 ms
        public double SlideLandMinSpeed = 5.0;

        // Vault and mantle
        public int VaultTicks = 8;
        public double VaultMinSpeed = 5.0;
        public int MantleRiseTicks = 16;
        public int MantleForwardTicks = 8;          // total 24 ticks = 0.4 s
        public double MantleForwardDistance = 0.45;
        public double MantleMaxHeight = 2.45;       // a 2-level ledge (2.4 m) plus tolerance
        public double AirMantleMinHeight = 0.3;
        public double MantleExitSpeedFraction = 0.5;

        // Sprint to aim
        public int SprintToAimTicks = 9;            // 0.15 s

        // Fall damage
        public double FallDamageFreeLevels = 6.0;
        public double FallDamagePerLevel = 10.0;

        // Ground detection and snapping
        public double GroundProbe = 0.02;
        public double MaxStepUp = 0.3;              // only ever climbs ramp slopes; full blocks are 1.2 m
    }
}
