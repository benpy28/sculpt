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
        public double MantleMaxHeight = 3.05;       // two 1.5 m wall tiles plus tolerance (Ben 2026-11-21: 1.5 m tiles)
        public double VaultMaxHeight = 1.55;        // one 1.5 m tile (sills, single tiles) plus tolerance
        public double AirMantleMinHeight = 0.3;
        public double MantleExitSpeedFraction = 0.5;  // of run speed: the minimum a mantle leaves you with
        public double MantleKeepSpeedFraction = 1.0;  // of the speed you hit the ledge with (momentum, Ben 2026-11-21)
        public int MantleCancelFromTick = 8;          // jump again from this rise tick on, once the ledge is close,
        public double MantleCancelMaxRemaining = 0.6; // within this many metres of the top, to pop over it early
        public double MantleCancelHopSpeed = 3.0;     // m/s up when popping out of a mantle

        // Momentum (Ben 2026-11-21). Skill is in timing and chaining; every move still works with a plain press.
        // Wall kick: jump in the air while touching a wall. You leave in the direction you look (mirrored off the
        // wall if you look into it) and keep your speed. Within the perfect window of hitting the wall you keep the
        // speed you hit it with; later you keep WallKickLateKeep of it.
        public double WallKickProbe = 0.12;           // how close a wall must be to kick off it
        public double WallKickUpSpeed = 6.5;          // m/s up (a full jump is ~8.8)
        public double WallKickPush = 2.5;             // m/s pushed away from the wall before keeping the speed
        public double WallKickMinSpeed = 6.0;         // a kick always leaves at least this fast
        public int WallKickPerfectTicks = 6;          // 100 ms after hitting the wall
        public double WallKickLateKeep = 0.85;
        public int WallKickGraceTicks = 30;           // the hit speed is remembered this long
        public int WallKickMaxPerAir = 3;             // until you touch the ground again: no wall climbing
        public int WallKickCooldownTicks = 12;
        // Counter-strafe brake: on the ground or sliding, pressing back or sideways against your motion while faster
        // than CounterBrakeMinSpeed brakes hard down to that speed. Only removes speed, never flips direction.
        public double CounterBrakeMinSpeed = 6.5;
        public double CounterBrakeDecel = 120.0;      // m/s^2 (letting go is 50)
        public double CounterBrakeMaxDot = 0.25;      // wish . motion at or below this counts (sideways or back)
        // Air: below run speed the old air control applies (a standing jump can still be steered). Above it the air
        // only nudges your heading a little per jump, never adds speed and can't be used to dodge.
        public double AirNudgeTurnRate = 0.6;         // rad/s, so about 10 degrees over a normal jump
        public double AirBrakeDecel = 3.0;            // m/s^2 when pressing against your motion in the air
        // Slide-hopping (slide-jump, slide-land, repeat) keeps your speed but each chained hop on its own loses a
        // little, so flats slowly cost speed and slopes, ramps and stairs are where speed is built.
        public double SlideHopKeep = 0.95;            // speed kept by a slide-land that follows a slide-jump
        public int SlideHopChainTicks = 60;           // a slide-land this soon after a slide-jump counts as a hop
        public double AirSoftCapSpeed = 14.0;         // above this, air speed bleeds (explosion boosts later)
        public double AirSoftCapDecel = 4.0;

        // Sprint to aim
        public int SprintToAimTicks = 9;            // 0.15 s

        // Fall damage
        public double FallDamageFreeLevels = 6.0;
        public double FallDamagePerLevel = 10.0;

        // Ground detection and snapping
        public double GroundProbe = 0.02;
        public double MaxStepUp = 0.3;              // one sub-cell: climbs 45 degree slopes, never a wall
        public double FilletIgnore = 0.16;          // a step that gains no more than this against a wall is the wall's base fillet: not taken
        public double FilletSlipPerTick = 0.05;     // stepping back off a wall's base fillet when standing on it
        public double FilletSlipMaxSpeed = 9.0;      // keeps slip + motion inside one tick's speed cap

        // Ramp launch off a chamfered crest
        public int RampLaunchMinTicks = 4;          // climbing at least this long (shorter bumps never launch)
        public double RampLaunchMinRiseSpeed = 1.0; // m/s of climb that counts as climbing
        public double RampLaunchProbeHalf = 0.05;   // minimum half-size of the probe just ahead of the feet
        public double RampLaunchProbeDepth = 0.05;  // no ground this close below the front means the crest has passed

        /// <summary>Weapon draw, sculpt enter and exit times. Stepped with movement so the netcode predicts them too.</summary>
        public HandConfig Hand = new HandConfig();

        /// <summary>Guns, health and hit rules. Stepped with movement so the netcode predicts firing too.</summary>
        public WeaponsConfig Weapons = new WeaponsConfig();
    }
}
