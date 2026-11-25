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
        /// <summary>Fire button held this tick (guns). Movement ignores it.</summary>
        public bool FireHeld;
        /// <summary>Fire pressed since the last tick (semi-auto and buffered shots).</summary>
        public bool FirePressed;
        /// <summary>Aim button pressed since the last tick (sets off your C4 while it is in hand).</summary>
        public bool AimPressed;
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
        /// <summary>Consecutive ground ticks spent climbing (for ramp launches off chamfered crests).</summary>
        public int UphillTicks;

        // Vault / mantle progress
        public int ActionTick;       // ticks since the vault or mantle started
        public double ActionStartY;
        public double ActionTargetY;
        public Vec3d ActionForward;
        public Vec3d ActionKeepVelocity;

        // Wall kick (Ben 2026-11-21)
        /// <summary>Tick the player last hit a wall in the air, the horizontal speed just before it, and its outward normal.</summary>
        public int WallHitTick;
        public double WallHitSpeed;
        public Vec3d WallHitNormal;
        /// <summary>Kicks since last on the ground, tick and outward normal of the last kick.</summary>
        public int WallKicks;
        public int LastWallKickTick;
        public Vec3d LastWallKickNormal;
        /// <summary>The counter-strafe brake acted last tick (so its sound plays once per brake).</summary>
        public bool Braking;
        /// <summary>Tick of the last slide-jump (chained slide-hops lose a little speed each).</summary>
        public int LastSlideJumpTick;

        /// <summary>First tick on which shooting is allowed again (sprint-to-aim, mantle).</summary>
        public int ShootReadyTick;

        /// <summary>Weapon / sculpt-mode hand state (enter and exit times, gun lock, disarm).</summary>
        public HandState Hand;

        /// <summary>Gun timers, bloom and recoil.</summary>
        public WeaponState Weapon;

        /// <summary>Your own grenades, C4 and launcher rounds out in the world (their flight and your own blast push are predicted).</summary>
        public GrenadeFlight Nade0, Nade1, Nade2, Nade3;
        /// <summary>Counts your throws (labels each grenade).</summary>
        public byte NadeSeq;
        /// <summary>First tick the launcher can fire its next round.</summary>
        public int LauncherReadyTick;

        /// <summary>Tick the last punch started, and of a punch press waiting for the punch to be ready.</summary>
        public int PunchTick;
        public int PunchBufferTick;
        /// <summary>Tick you last charged through a wall tile.</summary>
        public int ChargeTick;

        /// <summary>Damage taken since spawning; health = max - Damage. Fall damage is predicted, hits come from the server.</summary>
        public int Damage;
        public bool Dead;
        /// <summary>While dead: the tick the player respawns at their spawn.</summary>
        public int RespawnTick;

        public int Health(WeaponsConfig c) => c.MaxHealth - Damage;

        public static MotorState At(Vec3d feet) => new MotorState
        {
            Position = feet,
            Mode = MoveMode.Air,
            GroundNormal = new Vec3d(0, 1, 0),
            LastGroundedTick = int.MinValue / 2,
            JumpBufferedTick = int.MinValue / 2,
            LastSlideBoostTick = int.MinValue / 2,
            LandTick = int.MinValue / 2,
            WallHitTick = int.MinValue / 2,
            LastWallKickTick = int.MinValue / 2,
            LastSlideJumpTick = int.MinValue / 2,
            PunchTick = int.MinValue / 2,
            PunchBufferTick = int.MinValue / 2,
            ChargeTick = int.MinValue / 2,
            LauncherReadyTick = int.MinValue / 2,
            AirPeakY = feet.Y,
            Hand = HandState.Initial,
            Weapon = WeaponState.Initial,
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
        /// <summary>The player was put back at their spawn (respawn command, respawn after death, or fell out of the world).</summary>
        Respawned,
        /// <summary>Fall damage killed the player.</summary>
        Died,
        /// <summary>Kicked off a wall. Value 1 if inside the perfect window. Amount = speed kept.</summary>
        WallKick,
        /// <summary>Popped out of a mantle early with its speed.</summary>
        MantleCancel,
        /// <summary>Counter-strafe brake started this tick (first tick of a brake).</summary>
        Braked,
        /// <summary>Charged through one or more wall tiles. Value = tiles broken this tick.</summary>
        ChargedThrough,
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
