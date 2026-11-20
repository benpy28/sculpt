namespace Sculpt.Core
{
    public enum MoveMode : byte
    {
        Ground = 0,
        Air = 1,
        Slide = 2,
        Vault = 3,
        Mantle = 4,
    }

    /// <summary>One tick of player input. Built by the Game layer from the Input System; sent over the net in M3.</summary>
    public struct MoveInput
    {
        /// <summary>Strafe, -1 (left) .. 1 (right).</summary>
        public double MoveX;
        /// <summary>Forward, -1 (back) .. 1 (forward).</summary>
        public double MoveZ;
        /// <summary>View yaw in radians. 0 faces north (+Z), positive turns toward east (+X).</summary>
        public double Yaw;
        public bool JumpPressed;
        public bool JumpHeld;
        public bool CrouchHeld;
        public bool CrouchPressed;
        public bool SprintHeld;
        /// <summary>Fire or aim is held: sprint stops, and shooting waits for the sprint-to-aim delay.</summary>
        public bool WantsToShoot;
    }

    /// <summary>
    /// Complete movement state. A plain copyable struct so netcode can snapshot, rewind and replay it.
    /// </summary>
    public struct MotorState
    {
        public Vec3d Position;   // feet centre
        public Vec3d Velocity;
        public MoveMode Mode;
        public bool Crouched;    // box is at crouch height (crouch walk or slide)
        public bool Sprinting;
        public bool Grounded;
        public Vec3d GroundNormal;

        public int Tick;
        public int LastGroundedTick;
        public int JumpBufferedTick;
        public int LastSlideBoostTick;
        public int LandTick;
        public Vec3d LandVelocity;   // horizontal velocity at touchdown, for late slide-land
        public bool LandSlideUsed;
        public double AirPeakY;

        // Vault / mantle progress
        public int ActionTick;       // ticks since the vault or mantle started
        public double ActionStartY;
        public double ActionTargetY;
        public Vec3d ActionForward;
        public Vec3d ActionKeepVelocity;

        /// <summary>First tick on which shooting is allowed again (sprint-to-aim, mantle).</summary>
        public int ShootReadyTick;

        public static MotorState At(Vec3d feet) => new MotorState
        {
            Position = feet,
            Mode = MoveMode.Air,
            GroundNormal = new Vec3d(0, 1, 0),
            LastGroundedTick = int.MinValue / 2,
            JumpBufferedTick = int.MinValue / 2,
            LastSlideBoostTick = int.MinValue / 2,
            LandTick = int.MinValue / 2,
            AirPeakY = feet.Y,
        };

        public bool CanShoot => Tick >= ShootReadyTick && !Sprinting && Mode != MoveMode.Mantle;
        public double HorizontalSpeed => Velocity.HorizontalLength;
    }

    public enum MoveEventType : byte
    {
        Jumped,
        Landed,
        SlideStarted,
        SlideLandSuccess,
        SlideJump,
        Vaulted,
        MantleStarted,
        MantleFinished,
    }

    public struct MoveEvent
    {
        public MoveEventType Type;
        public int Tick;
        /// <summary>Landed: fall damage (0 if none). SlideStarted: 1 if boosted.</summary>
        public int Value;
        /// <summary>Landed: levels fallen.</summary>
        public double Amount;
    }
}
