namespace Sculpt.Core.Net
{
    /// <summary>Every netcode number. Ticks are 60 Hz sim ticks. Game may override from a ScriptableObject.</summary>
    public sealed class NetConfig
    {
        /// <summary>Each input packet repeats this many of the newest commands, so lost packets cost nothing.</summary>
        public int InputRedundancy = 8;

        /// <summary>
        /// How many ticks early (at minimum, over the feedback window) a client's newest command should reach the server.
        /// 2 = one tick to arrive in time plus one tick of slack for a single lost packet. Higher is safer but adds
        /// server-side input delay (never felt by the shooter, who predicts, but seen by everyone else).
        /// </summary>
        public int TargetLeadTicks = 2;

        /// <summary>Lead feedback window: the server reports the worst lead seen in this many ticks.</summary>
        public int LeadWindowTicks = 60;

        /// <summary>Clock speed-up/slow-down per tick of lead error, as a fraction of tick rate.</summary>
        public double ClockGainPerTick = 0.01;

        /// <summary>Largest clock speed change (fraction). 3% is invisible to the player.</summary>
        public double MaxClockDilation = 0.03;

        /// <summary>Lead errors larger than this jump the clock at once instead of dilating.</summary>
        public int ClockSnapTicks = 8;

        /// <summary>Most ticks a client may run in one frame when catching up.</summary>
        public int MaxCatchUpTicksPerFrame = 10;

        /// <summary>Furthest ahead of the server tick a command may be. Anything later is ignored.</summary>
        public int MaxCommandLeadTicks = 60;

        /// <summary>Client prediction history length (ticks). Must cover the round trip plus lead.</summary>
        public int HistoryTicks = 128;

        /// <summary>Server sends a snapshot every N ticks (1 = 60 Hz).</summary>
        public int SnapshotEveryTicks = 1;

        /// <summary>Remote players are drawn this many ticks behind the newest snapshot (covers jitter and a lost snapshot).</summary>
        public double InterpDelayTicks = 3.0;

        /// <summary>When the snapshot stream stalls, remote players extrapolate at most this many ticks, then hold.</summary>
        public double MaxExtrapolateTicks = 6.0;

        /// <summary>Visual correction offset decays with this time constant (seconds). Sim state is never smoothed.</summary>
        public double CorrectionSmoothSeconds = 0.08;

        /// <summary>Corrections bigger than this (metres) snap instead of smoothing.</summary>
        public double CorrectionSnapDistance = 2.0;

        /// <summary>Server keeps this many ticks of every player's position for lag compensation (M5+).</summary>
        public int ServerHistoryTicks = 64;

        /// <summary>Falling below this height respawns the player (server and client run the same rule).</summary>
        public double KillY = -30.0;

        /// <summary>
        /// Dev warp (F6 movement showcase): where a warp command puts the player, and whether the server accepts it.
        /// The game only turns it on in the Editor and development builds.
        /// </summary>
        public bool AllowDevWarp;
        public Vec3d DevWarpPoint;
    }
}
