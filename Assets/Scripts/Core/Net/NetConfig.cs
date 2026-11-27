namespace Sculpt.Core.Net
{
    /// <summary>Every netcode number. Ticks are 60 Hz sim ticks. Game may override from a ScriptableObject.</summary>
    public sealed class NetConfig
    {
        /// <summary>Each input packet repeats this many of the newest commands, so lost packets cost nothing.</summary>
        public int InputRedundancy = 12; // 100 ms of commands at 120 Hz (was 8 at 60 Hz = 133 ms)

        /// <summary>
        /// How many ticks early (at minimum, over the feedback window) a client's newest command should reach the server.
        /// 2 = one tick to arrive in time plus one tick of slack for a single lost packet. Higher is safer but adds
        /// server-side input delay (never felt by the shooter, who predicts, but seen by everyone else).
        /// </summary>
        public int TargetLeadTicks = 2;

        /// <summary>
        /// How many ticks above <see cref="TargetLeadTicks"/> the reported lead may sit before the clock slows down.
        /// Every tick of lead is a tick of delay on everything the server confirms and on what others see of you, so
        /// this is 0: the clock walks back until the worst lead in the window is the target itself. (It was a fixed 2
        /// until 2026-11-25, which left clean low-ping links resting two ticks, 33 ms, later than needed.) The report
        /// is in whole ticks and looks back over a window, which is all the damping the loop needs.
        /// </summary>
        public int ClockDeadbandTicks = 0;

        /// <summary>Lead feedback window: the server reports the worst lead seen in this many ticks.</summary>
        public int LeadWindowTicks = 60 * SimTime.Per60;

        /// <summary>Clock speed-up/slow-down per tick of lead error, as a fraction of tick rate.</summary>
        public double ClockGainPerTick = 0.01;

        /// <summary>Largest clock speed change (fraction). 3% is invisible to the player.</summary>
        public double MaxClockDilation = 0.03;

        /// <summary>Lead errors larger than this jump the clock at once instead of dilating.</summary>
        public int ClockSnapTicks = 8 * SimTime.Per60;

        /// <summary>Most ticks a client may run in one frame when catching up.</summary>
        public int MaxCatchUpTicksPerFrame = 10 * SimTime.Per60;

        /// <summary>Furthest ahead of the server tick a command may be. Anything later is ignored.</summary>
        public int MaxCommandLeadTicks = 60 * SimTime.Per60;

        /// <summary>Client prediction history length (ticks). Must cover the round trip plus lead.</summary>
        public int HistoryTicks = 128 * SimTime.Per60;

        /// <summary>
        /// Delta snapshots: each snapshot is coded against the newest one the client acked (see SnapshotCodec), so a
        /// still player costs a couple of bytes and your own state only what changed. Off = every snapshot full (the
        /// old size; for comparison). Either way the client decodes exactly the same state.
        /// </summary>
        public bool DeltaSnapshots = true;

        /// <summary>
        /// How far back (ticks) a delta base may be. Server and client keep this many snapshots each. An ack older than
        /// this (a long stall or a very bad link) gets a full snapshot. 128 ticks = 1.07 s at 120 Hz.
        /// </summary>
        public int SnapshotBaselineTicks = 64 * SimTime.Per60;

        /// <summary>Server sends a snapshot every N ticks (1 = every tick).</summary>
        public int SnapshotEveryTicks = 1;

        /// <summary>Remote players are drawn this many ticks behind the newest snapshot (covers jitter and a lost snapshot).</summary>
        public double InterpDelayTicks = 3.0; // 25 ms at 120 Hz. Kept at 3 ticks on purpose: at 60 Hz this was 50 ms of delay on every enemy you see.

        /// <summary>When the snapshot stream stalls, remote players extrapolate at most this many ticks, then hold.</summary>
        public double MaxExtrapolateTicks = 6.0 * SimTime.Per60;

        /// <summary>Visual correction offset decays with this time constant (seconds). Sim state is never smoothed.</summary>
        public double CorrectionSmoothSeconds = 0.08;

        /// <summary>Corrections bigger than this (metres) snap instead of smoothing.</summary>
        public double CorrectionSnapDistance = 2.0;

        /// <summary>Server keeps this many ticks of every player's position for lag compensation (M5+).</summary>
        public int ServerHistoryTicks = 64 * SimTime.Per60;

        /// <summary>Falling below this height respawns the player (server and client run the same rule).</summary>
        public double KillY = -30.0;

        /// <summary>
        /// Dev warp (F6 movement showcase): where a warp command puts the player, and whether the server accepts it.
        /// The game only turns it on in the Editor and development builds.
        /// </summary>
        public bool AllowDevWarp;

        /// <summary>
        /// Dev respawn (F1): whether a respawn press from a client is honoured. Off by default since 2026-11-26: before,
        /// any client could send it, so a modified client could skip the 3 s respawn wait or teleport home at full health
        /// mid-fight. The game turns it on only in the Editor and development builds, like the warp. The server's own
        /// respawns (match countdown, the respawn timer, falling out of the world) never need it.
        /// </summary>
        public bool AllowDevRespawn;

        /// <summary>Anti-cheat checks and interest management (who goes in whose snapshots).</summary>
        public AntiCheat.AntiCheatConfig AntiCheat = new AntiCheat.AntiCheatConfig();
        public Vec3d DevWarpPoint;
    }
}
