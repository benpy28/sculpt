using Sculpt.Core.Audio;

namespace Sculpt.Core.AntiCheat
{
    /// <summary>
    /// Every anti-cheat number (2026-11-26, Ben's queue item 5). The server simulates every player from their inputs, so
    /// movement speed, fire rate, ammo, damage and line of sight are the server's own results and can't be sent by a
    /// client. What is left is (a) what a client tells the server (inputs, view tick, acks: checked and logged), (b) what
    /// the server tells a client (interest management: enemies you can't see or hear aren't sent), and (c) statistics on
    /// how someone aims (logged for review, never acted on). Nothing here delays anything or changes a legit result.
    /// </summary>
    public sealed class AntiCheatConfig
    {
        public InterestConfig Interest = new InterestConfig();

        /// <summary>Master switch for the flag log and the checks that feed it (interest management has its own).</summary>
        public bool Checks = true;

        /// <summary>At most one flag of a kind per player in this many ticks goes in the log (all are still counted).</summary>
        public int FlagEveryTicks = SimTime.TicksPerSecond;

        /// <summary>The log keeps at most this many flags (counts keep going).</summary>
        public int MaxFlags = 20000;

        // ---------------------------------------------------------------- lag compensation

        /// <summary>
        /// A command's view tick (what it says it was drawing others at) may sit this many ticks behind the newest
        /// snapshot it acked in the same packet, on top of the interpolation delay, before it is flagged. An honest
        /// client draws InterpDelayTicks behind its newest snapshot (plus clock smoothing, a tick or two).
        /// </summary>
        public int ViewBehindAckTicks = 12;

        /// <summary>
        /// A hit whose command's view lag (command tick minus view tick) is this many ticks above the player's own median
        /// is flagged (backtracking: claiming an older view only on the shots that need it). Ping changes slowly.
        /// </summary>
        public int ViewLagJumpTicks = 12;

        /// <summary>Real commands the median view lag is taken over.</summary>
        public int ViewLagWindow = 64;

        /// <summary>
        /// Clamp the lag-compensation rewind so a shooter can never rewind a victim older than the snapshot the shooter
        /// claims (its acked snapshot tick) minus the interpolation delay and this margin. An honest client draws its
        /// newest acked snapshot InterpDelayTicks behind, so this never clamps legit play (view and ack lag together under
        /// ping or loss); it does stop a modified client that forges an old view while still acking a recent snapshot
        /// (the naive backtrack), turning the cheat from "flagged" into "prevented". A client that also backdates its ack
        /// to keep rewinding is then claiming a steady high latency, which ViewLagJump catches on the toggle.
        /// </summary>
        public bool ClampRewindToAck = true;
        /// <summary>
        /// Extra slack (ticks) BELOW the ViewBehindAck flag boundary for the rewind clamp. The clamp floor is
        /// ack - InterpDelayTicks - ViewBehindAckTicks - this, so the clamp is always looser than the flag: a view the
        /// flag treats as honest is never clamped, and anything the clamp caps was already flagged. Pure safety margin
        /// for clock smoothing and jitter; it never needs to be tight.
        /// </summary>
        public int ViewClampMarginTicks = 4;

        // ---------------------------------------------------------------- integrity (server rules)

        /// <summary>No legit combination of slides, boosts and blasts gets near this speed, m/s.</summary>
        public double MaxSpeed = 60;

        /// <summary>One tick may move a player at most their speed times the tick plus this much (step-ups, shoves, climbs), m.</summary>
        public double MoveSlack = 0.75;

        // ---------------------------------------------------------------- aim statistics (logged only)

        /// <summary>Aim snap: the view turned at least this far (degrees) in <see cref="SnapTicks"/> right before a hit...</summary>
        public double SnapDegrees = 25;
        public int SnapTicks = 2 * SimTime.Per60;
        /// <summary>...and then stopped dead (turned less than this, degrees) on the tick of the shot. A flick by hand overshoots or keeps moving.</summary>
        public double SnapStopDegrees = 0.3;

        /// <summary>A first shot that hits a target who came into sight less than this long ago, seconds.</summary>
        public double FastReactionSeconds = 0.09;
        /// <summary>The target must have been out of sight at least this long before (not a re-peek of someone you just saw), seconds.</summary>
        public double FastReactionHiddenSeconds = 0.3;
        /// <summary>A shot counts as a burst's first when the shooter hadn't fired for this long, seconds (holding the trigger through a doorway is legit).</summary>
        public double FirstShotGapSeconds = 0.25;

        public int FastReactionTicks => System.Math.Max(1, SimTime.SecondsToTicks(FastReactionSeconds));
        public int FastReactionHiddenTicks => System.Math.Max(1, SimTime.SecondsToTicks(FastReactionHiddenSeconds));
        public int FirstShotGapTicks => System.Math.Max(1, SimTime.SecondsToTicks(FirstShotGapSeconds));

        // ---------------------------------------------------------------- phantom-track honeypot (logged only)

        /// <summary>
        /// Phantom-track: the strongest wallhack tell that needs no shot. Interest management (NobodyPopsIn) proves an
        /// honest client was NOT sent a hidden enemy, so it has no way to know where that enemy is. If a player's aim
        /// ray nonetheless stays locked on a hidden enemy's head WHILE the bearing to that enemy is sweeping (the enemy
        /// has lateral motion, so holding the lock means actively following), that is through-wall tracking a fair
        /// client cannot do. Two players walking straight at each other down a corridor do NOT trip it: the bearing
        /// barely changes, so there is nothing to follow. Server-side, replay-verifiable (Aim category), logged only.
        /// </summary>
        public bool PhantomTrack = true;
        /// <summary>The aim ray must stay within this of the hidden enemy's head to count as tracking, degrees.</summary>
        public double PhantomAngleDegrees = 3.0;
        /// <summary>Sustained for this many ticks before a flag (one streak = one flag).</summary>
        public int PhantomWindowTicks = SimTime.TicksPerSecond / 2; // 0.5 s
        /// <summary>...and over that window the bearing to the enemy must have swept at least this far, so a static straight-ahead alignment never flags, degrees.</summary>
        public double PhantomMinBearingSweepDegrees = 15.0;
        /// <summary>Only test enemies within this range (a head further away is too small for a lock to mean anything), m.</summary>
        public double PhantomMaxRange = 60.0;
    }

    /// <summary>
    /// Interest management: an enemy goes in your snapshots only if you could see them soon, could hear them, are very
    /// close, or the ping shows them. Wallhacks and ESP can only show what the server sent, so hidden, quiet enemies
    /// are simply not there to show. Erring wide: an enemy is sent before they could come into view (look-ahead on both
    /// players' movement, a peek reach either side, a hold after they leave view), so nobody pops in.
    /// </summary>
    public sealed class InterestConfig
    {
        public bool Enabled = true;

        /// <summary>Always sent within this distance, m (collisions, punches, heals in the same doorway).</summary>
        public double AlwaysRange = 4;

        /// <summary>Line-of-sight tests stop past this distance (only sound, shots and the ping count further), m.</summary>
        public double MaxSightRange = 400;

        /// <summary>Each pair's line of sight is tested every this many ticks (pairs are spread over the ticks).</summary>
        public int EveryTicks = 3 * SimTime.Per60; // 20 Hz

        /// <summary>
        /// Both players are tested where they will be this far ahead (current velocity), seconds. Covers the test
        /// interval, the trip to the client and its interpolation delay, so an enemy is in your snapshots before a
        /// corner opens. At 120 Hz: 50 ms between tests + up to ~150 ms one way + 25 ms drawn behind.
        /// </summary>
        public double LookaheadSeconds = 0.3;

        /// <summary>Peek reach: also test from points this far either side of where each player will be, m.</summary>
        public double PeekReach = 1.0;

        /// <summary>Jump reach: also test from this far above each player's eye, m.</summary>
        public double JumpReach = 1.3;

        /// <summary>Once in sight, an enemy stays sent this long after the last test that saw them, seconds.</summary>
        public double HoldSeconds = 0.5;

        /// <summary>Movement sound ranges are stretched by this before deciding someone can be heard.</summary>
        public double NoiseSlack = 1.25;

        /// <summary>The loudest movement sound a player made is remembered this long (steps are a stride apart), seconds.</summary>
        public double MoveNoiseSeconds = 0.6;

        /// <summary>A shot, throw or blast makes the shooter heard this far for <see cref="ShotNoiseSeconds"/>, m.</summary>
        public double ShotNoiseRange = 250;
        public double ShotNoiseSeconds = 1.0;

        /// <summary>The movement sound numbers (the same the client plays footsteps with).</summary>
        public MoveSoundConfig Sounds = new MoveSoundConfig();

        public int HoldTicks => System.Math.Max(1, SimTime.SecondsToTicks(HoldSeconds));
        public int MoveNoiseTicks => System.Math.Max(1, SimTime.SecondsToTicks(MoveNoiseSeconds));
        public int ShotNoiseTicks => System.Math.Max(1, SimTime.SecondsToTicks(ShotNoiseSeconds));
    }
}
