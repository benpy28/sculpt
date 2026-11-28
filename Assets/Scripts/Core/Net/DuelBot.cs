using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    public enum BotLevel : byte { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>How a duel bot sees, aims and moves. Every number a human-like limit; nothing is ever read through walls.</summary>
    public sealed class DuelBotConfig
    {
        /// <summary>The bot acts on what it saw or heard this many ticks ago (12 = 200 ms).</summary>
        public int ReactionTicks = 12 * SimTime.Per60;
        /// <summary>Fastest turn, degrees per second.</summary>
        public double TurnDegPerSec = 360;
        /// <summary>Share of the remaining aim error closed each tick (smooth, so tracking a strafing target lags a little).</summary>
        public double AimGain = 0.2;
        /// <summary>Aim wobble amplitude in degrees (slow, smooth drift, so far targets get missed more).</summary>
        public double WobbleDeg = 1.3;
        /// <summary>How much of the target's movement over its reaction time it makes up for (1: perfect tracking of a steady
        /// strafe; a change of direction still fools it for a reaction time). 0 aims where it last saw them.</summary>
        public double Lead = 0.8;
        /// <summary>Share of the recoil it pulls down against.</summary>
        public double RecoilComp = 0.6;
        /// <summary>Extra random wait before the first shot at a newly seen target (on top of the reaction time).</summary>
        public int FirstShotDelayMaxTicks = 9 * SimTime.Per60;
        /// <summary>It only sees inside this horizontal cone around its view (like a screen plus a little side vision).</summary>
        public double ViewConeDeg = 110;
        /// <summary>Hears gunshots this far away, and running or sprinting footsteps this far.</summary>
        public double HearShots = 60, HearSteps = 12;
        /// <summary>Chance per engagement to aim at the head instead of the chest.</summary>
        public double HeadChance = 0.1;
        /// <summary>Strafe direction lasts this long (seconds, random in between).</summary>
        public double StrafeMin = 0.35, StrafeMax = 0.9;
        /// <summary>Chance per tick of a hop during a close fight.</summary>
        public double FightHopChance = 0.006;
        /// <summary>
        /// Battle royale: for this long after the match starts a bot loots instead of hunting (Ben 2026-11-26: a lobby
        /// was down to about 10 alive 10 seconds in, bots on neighbouring pads shooting each other on sight). It still
        /// fights back when hurt, and fights anyone who comes within <see cref="BrCalmCloseMetres"/>.
        /// </summary>
        public double BrCalmSeconds = 60;
        public double BrCalmCloseMetres = 3;
        /// <summary>Battle royale after the opening: the farthest enemy it starts a fight with (unless hurt).</summary>
        public double BrEngageMetres = 28;
        /// <summary>The farthest it can make anyone out.</summary>
        public double SightMetres = 160;
        /// <summary>Battle royale: how long it holds a spot it roamed to before moving on (seconds, random in between; careful bots up to 1.4x, pushy ones down to 0.6x).</summary>
        public double BrHoldMin = 8, BrHoldMax = 28;
        /// <summary>Battle royale temperament spread: each bot gets an aggression between these (0 careful, 1 pushy).</summary>
        public double BrAggroMin = 0.1, BrAggroMax = 0.95;
        /// <summary>Battle royale: gunfire this far away tells it there is a fight (third-partying, an enemy's health guess).</summary>
        public double BrHearFightMetres = 100;
        /// <summary>Battle royale: longest stay in cover (seconds; a heal under way may run up to 6 s past it), then a
        /// pause before it takes cover again unless hurt. Cover is for healing and breaking a bad fight, never for camping.</summary>
        public double BrCoverSeconds = 9, BrCoverCooldownSeconds = 5;
        /// <summary>Battle royale rotation: walking speed it plans with (m/s, paths wind) and its safety margin, careful bots
        /// leaving up to the max early, pushy ones the min (seconds before the edge reaches it).</summary>
        public double BrRotateSpeed = 6.5, BrRotateMarginMin = 8, BrRotateMarginMax = 40;
        /// <summary>Battle royale: chance to go after a fight it hears (careful .. pushy), and how long it keeps at it.</summary>
        public double BrThirdPartyMin = 0.1, BrThirdPartyMax = 0.65, BrThirdPartySeconds = 30;
        /// <summary>Battle royale: after the calm opening, the fights it starts grow from half range to full over this long.</summary>
        public double BrSettleSeconds = 180;

        public static DuelBotConfig For(BotLevel level)
        {
            switch (level)
            {
                case BotLevel.Easy:
                    return new DuelBotConfig { ReactionTicks = 18 * SimTime.Per60, TurnDegPerSec = 220, AimGain = 0.13, WobbleDeg = 2.4, Lead = 0.55, RecoilComp = 0.3, FirstShotDelayMaxTicks = 18 * SimTime.Per60, ViewConeDeg = 95, HeadChance = 0, StrafeMin = 0.6, StrafeMax = 1.4, FightHopChance = 0 };
                case BotLevel.Hard:
                    return new DuelBotConfig { ReactionTicks = 10 * SimTime.Per60, TurnDegPerSec = 540, AimGain = 0.28, WobbleDeg = 0.7, Lead = 0.95, RecoilComp = 0.85, FirstShotDelayMaxTicks = 5 * SimTime.Per60, ViewConeDeg = 120, HeadChance = 0.25, StrafeMin = 0.25, StrafeMax = 0.7, FightHopChance = 0.01 };
                default:
                    return new DuelBotConfig();
            }
        }
    }

    /// <summary>
    /// A scripted 1v1 opponent (not machine learning): a server player with no connection whose brain produces the same
    /// input commands a person would send, so every rule, the match and the netcode treat it like a human. It sees only
    /// what has a clear line from its eyes inside its view cone, hears gunshots and nearby running, notices being hit,
    /// and acts on all of it a reaction time late. Aim turns at a capped speed with smooth error and wobble, pulls down
    /// against only part of the recoil, and fires only when the crosshair is roughly on. It walks a nav grid of the real
    /// map (stairs, ramps, vaults), roams when it knows nothing, hunts where it last saw or heard you, strafes in fights,
    /// and picks pump, SMG or AR by range.
    /// </summary>
    public sealed partial class DuelBot : IBotBrain
    {
        private struct Obs
        {
            public int Tick;
            public bool Seen, Heard, Pinged;
            /// <summary>It was hit this tick (Heard holds roughly where from).</summary>
            public bool Hurt;
            /// <summary>The enemy observed.</summary>
            public int Id;
            /// <summary>The enemy observed is another bot (battle royale opening: bots leave each other alone).</summary>
            public bool Bot;
            public Vec3d Pos, Vel, HeardPos, PingPos;
            public double Height;
        }

        public readonly DuelBotConfig Config;
        public readonly BotNav Nav;
        private readonly Random _rng;
        private readonly Obs[] _ring = new Obs[64 * SimTime.Per60];

        // Aim (what the bot is trying to look at, before recoil compensation and wobble).
        private double _yaw;
        private double _pitch;
        private readonly double _wobPhase1, _wobPhase2, _wobPhase3, _wobPhase4;

        // Memory (all from delayed perception).
        private bool _engaged;
        private int _fireOkTick;
        private double _aimHeightOffset;
        private bool _aimHead;
        private Vec3d _lastSeenPos, _lastSeenVel;
        private int _lastSeenTick = int.MinValue / 2;
        private Vec3d _lastHeardPos;
        private int _lastHeardTick = int.MinValue / 2;
        // The anti-hiding ping: where the server showed the enemy through walls (a person sees the same marker).
        private Vec3d _pingPos;
        private int _pingTick = int.MinValue / 2;
        private int _lastDamage;
        private int _hurtTick = int.MinValue / 2;

        /// <summary>The battle royale's opening (see <see cref="DuelBotConfig.BrCalmSeconds"/>), and nobody has hurt it lately.</summary>
        private bool Calm(NetServer server, ServerPlayer me, int tick)
        {
            if (!server.Match.IsBr || server.Match.Phase != MatchPhase.Playing) return false;
            if (tick - server.Match.PhaseStartTick >= Config.BrCalmSeconds * SimTime.TicksPerSecond) return false;
            return tick - _hurtTick > 4 * SimTime.TicksPerSecond;
        }

        // Movement.
        private readonly List<int> _path = new List<int>();
        private int _pathAt;
        private Vec3d _pathGoal;
        private int _pathTick = int.MinValue / 2;
        private Vec3d _roamGoal;
        private bool _hasRoam;
        private int _strafe = 1;
        private int _strafeUntil;
        private Vec3d _stuckFrom;
        private int _stuckCheck;
        private int _stuckCount;
        private int _jumpUntil;
        private bool _wasDead = true;
        private Vec3d _lastPos;

        // Weapons.
        private int _switchTick = int.MinValue / 2;
        private int _reloadTick = int.MinValue / 2;
        private bool _firedLast;

        /// <summary>For tests: the gun it wants out, what it currently knows, and whether it is firing.</summary>
        public Weapon WantGun { get; private set; } = Weapon.Smg;
        public bool Engaged => _engaged;
        public bool Firing => _firedLast;
        public int LastSeenTick => _lastSeenTick;
        /// <summary>The tick of the newest ping it acted on (where the server showed the enemy through walls).</summary>
        public int LastPingTick => _pingTick;
        public IReadOnlyList<int> Path => _path;

        /// <summary>For measuring (Tests/BrPerf): what it was walking toward this tick and how its route stood.</summary>
        public enum GoalKind : byte { None, Fight, Cover, CoverHeal, Storm, Rotate, Ping, LastSeen, ThirdParty, Heard, Loot, Roam, Hold, Countdown }
        public GoalKind Goal { get; private set; }
        /// <summary>Why the last route ask got nothing: 0 none, 1 waited for the shared budget, 2 search failed, 3 no spot near the goal.</summary>
        public int PathNote { get; private set; }
        public double WishLength { get; private set; }
        public int PathFails, PathAsks, PathNoSpot;
        public int PathAt => _pathAt;
        public Vec3d GoalPos { get; private set; }

        public DuelBot(BotNav nav, DuelBotConfig config = null, int seed = 1)
        {
            Nav = nav ?? throw new ArgumentNullException(nameof(nav));
            Config = config ?? new DuelBotConfig();
            _rng = new Random(seed);
            _wobPhase1 = _rng.NextDouble() * 6.283;
            _wobPhase2 = _rng.NextDouble() * 6.283;
            _wobPhase3 = _rng.NextDouble() * 6.283;
            _wobPhase4 = _rng.NextDouble() * 6.283;
            _tac = new Random(unchecked(seed * 7919 + 17));
            Aggression = Config.BrAggroMin + _tac.NextDouble() * (Config.BrAggroMax - Config.BrAggroMin);
        }

        public bool CountsAsPlayer => true;

        public InputCmd Next(NetServer server, ServerPlayer me)
        {
            int tick = server.Tick;
            _server = server;
            MovementConfig mc = server.Movement;
            _wc = mc.Weapons;
            var cmd = new InputCmd { Tick = tick, ViewTick = tick };
            MotorState s = me.State;

            if (s.Dead)
            {
                _wasDead = true;
                _engaged = false;
                _path.Clear();
                Store(tick, default);
                return Finish(cmd, s);
            }
            if (_wasDead || (s.Position - _lastPos).Length > 4)
            {
                // (Re)spawned or teleported: start fresh from here, facing the spawn's way.
                _wasDead = false;
                _yaw = me.SpawnYaw;
                _pitch = 0;
                _path.Clear();
                _hasRoam = false;
                _lastDamage = s.Damage;
                _lastSeenTick = _lastHeardTick = _pingTick = int.MinValue / 2;
                Doing = Tactic.None;
                _rotPhase = -2;
                _rotating = false;
                Array.Clear(_noise, 0, _noise.Length);
                _stuckFrom = s.Position;
                _stuckCheck = tick;
            }
            _lastPos = s.Position;

            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            ServerPlayer enemy = FindEnemy(server, me);
            Store(tick, Observe(server, me, enemy, eye));
            bool brPlay = server.Match.IsBr && server.Match.Phase == MatchPhase.Playing;
            if (brPlay)
            {
                HearGunfire(server, me, tick);
                SettleRange(server, tick);
                CheckSecondEnemy(server, me, enemy, eye, tick);
            }

            // ---- What it knows now: what it perceived a reaction time ago.
            Obs p = Recall(tick - Config.ReactionTicks);
            if (Calm(server, me, tick) && !(p.Seen && !p.Bot && (p.Pos - s.Position).Length <= Config.BrCalmCloseMetres))
            {
                // Early in a battle royale: loot; an enemy seen or heard is left alone unless it hurt us, or is a person
                // who came close (two bots passing each other on the way to their loot leave each other alone: they used
                // to shotgun each other in the first seconds).
                p.Seen = false;
                p.Heard = false;
            }
            else if (brPlay)
            {
                // Later in a battle royale it plays for placement: it picks fights it can win (range, its temperament and
                // guns, both players' health as far as it can tell), answers anyone who hurt it close by, takes cover from
                // someone hurting it from far off, and doesn't go after far-off noise. (2026-11-27: 32-bot matches were
                // down to a third alive two minutes in, long AR fights at 60-80 m.)
                bool hurtLately = tick - _hurtTick <= 4 * SimTime.TicksPerSecond;
                bool fighting = _engaged && tick - _lastSeenTick < 30 * SimTime.Per60;
                double seenAt = p.Seen ? (p.Pos - s.Position).Length : 0;
                if (p.Seen && !fighting && !hurtLately && !TakeFight(server, me, s, seenAt, p.Id, tick))
                {
                    // Placement play: someone it won't take on. Most players get out of their sight and move on the other
                    // way rather than walk on into them (careful bots nearly always, pushy ones seldom). With every bot
                    // ignoring each other and carrying on, they met at point blank and the lobby was down to a handful
                    // by five minutes (2026-11-28).
                    if (seenAt < 50 && tick >= _avoidRollTick && Doing != Tactic.Cover && !(_rotating && _rotUrgent))
                    {
                        _avoidRollTick = tick + 8 * SimTime.TicksPerSecond;
                        if (_tac.NextDouble() < 1.1 - Aggression)
                        {
                            _avoidPos = p.Pos;
                            _avoidTick = tick;
                            _hasRoam = false;
                            if (StartCover(server, s.Position, p.Pos, tick)) AvoidCount++;
                        }
                    }
                    p.Seen = false;
                }
                if (p.Seen && hurtLately && !fighting && seenAt > EngageRange(s) + 15) p.Seen = false; // too far to win: cover instead
                if (p.Heard && (p.HeardPos - s.Position).Length > (hurtLately ? EngageRange(s) : Config.BrEngageMetres)) p.Heard = false;
                // Footsteps or shots nearby: pushy players go and look, careful ones keep to their own business unless
                // it is right on top of them (it hunted every noise, so careful bots walked into fights too).
                if (p.Heard && !hurtLately && Aggression < 0.5 && (p.HeardPos - s.Position).Length > 15) p.Heard = false;
                // Hit by someone it can't see or can't reach: get out of their line.
                if (p.Hurt && !p.Seen && Doing != Tactic.Cover) StartCover(server, s.Position, p.HeardPos, tick);
            }
            if (p.Seen)
            {
                if (!_engaged)
                {
                    _engaged = true;
                    _fireOkTick = tick + _rng.Next(Config.FirstShotDelayMaxTicks + 1);
                    _aimHead = _rng.NextDouble() < Config.HeadChance;
                    _aimHeightOffset = (_rng.NextDouble() - 0.5) * 0.35;
                }
                _lastSeenPos = p.Pos;
                _lastSeenVel = p.Vel;
                _lastSeenTick = p.Tick;
                _targetId = p.Id;
            }
            else _engaged = false;
            if (p.Heard) { _lastHeardPos = p.HeardPos; _lastHeardTick = p.Tick; }
            if (p.Pinged) { _pingPos = p.PingPos; _pingTick = p.Tick; }

            bool recentlySeen = tick - _lastSeenTick < 90 * SimTime.Per60;
            double dist = recentlySeen ? (_lastSeenPos - s.Position).Length : 30;
            if (brPlay)
            {
                if (_engaged && Doing != Tactic.Cover && ShouldRetreat(server, me, s, dist, _targetId, tick)) StartCover(server, s.Position, _lastSeenPos, tick);
                // Cornered in cover (the enemy came close, or keeps hitting it there): fight it out.
                if (Doing == Tactic.Cover && _engaged && (dist < 9 || tick - _hurtTick < 30 * SimTime.Per60 && (s.Position - _coverSpot).HorizontalLength < 1.5)) EndCover(tick);
                StepCover(server, s, tick);
            }
            else if (Doing != Tactic.None) Doing = Tactic.None;

            // ---- Aim.
            double wantYaw = _yaw, wantPitch = 0;
            double turnCap = Config.TurnDegPerSec / SimTime.TicksPerSecond * Math.PI / 180;
            double gain = Config.AimGain;
            Vec3d aimAt = default;
            bool aimEnemy = false;
            double aimUp = p.Height * 0.6; // the height it aims at on the target (chest, or the head)
            if (_engaged)
            {
                double ahead = Config.ReactionTicks / (double)SimTime.TicksPerSecond * Config.Lead;
                double h = p.Height;
                double up = _aimHead ? h - 0.18 : h * 0.62 + _aimHeightOffset;
                aimUp = up;
                aimAt = p.Pos + p.Vel.WithY(0) * ahead + new Vec3d(0, up, 0);
                aimEnemy = true;
            }
            else if (recentlySeen)
            {
                aimAt = _lastSeenPos + new Vec3d(0, 1.2, 0); // pre-aim where they went
                aimEnemy = true;
                gain *= 0.7;
            }
            if (aimEnemy)
            {
                Vec3d d = aimAt - eye;
                wantYaw = Math.Atan2(d.X, d.Z);
                // Its own strafing swings the target across its view; a person tracks that without thinking. The smooth
                // aim would trail it by a steady few degrees (never firing at 9 m), so it leads its own motion as well
                // as its tracking skill allows.
                double r2 = d.X * d.X + d.Z * d.Z;
                if (_engaged && r2 > 1)
                {
                    double rate = (d.X * s.Velocity.Z - d.Z * s.Velocity.X) / r2; // radians per second
                    double gt = Math.Max(0.02, 1 - Math.Pow(1 - Clamp(gain, 0, 1), 1.0 / SimTime.Per60)); // the per-tick gain used below
                    wantYaw += rate / SimTime.TicksPerSecond * (1 - gt) / gt * Config.Lead;
                }
                wantPitch = -Math.Asin(Math.Max(-1, Math.Min(1, d.Y / Math.Max(1e-6, d.Length)))) * 180 / Math.PI;
            }

            // ---- Move (and, when not fighting, look where it walks).
            Vec3d wish = Vec3d.Zero;
            bool sprint = false, jump = false, crouch = false, punch = false;
            int openDoor = -1;
            _push = brPlay && _engaged && EnemyHpGuess(server, me, _targetId, tick) < 0.35 && Hp(s, mc) > 0.4;
            Vec3d rotGoal = default;
            bool rotate = brPlay && RotateGoal(server, s, tick, out rotGoal);
            if (brPlay && Doing == Tactic.Cover)
            {
                // Into cover (still shooting back while it can see them), then crouch there and heal.
                if (_coverHealing && !_engaged) { wish = Vec3d.Zero; crouch = true; Goal = GoalKind.CoverHeal; }
                else
                {
                    Goal = GoalKind.Cover;
                    wish = FollowPath(s, _coverSpot, tick, ref jump, out Vec3d lookAt, out _);
                    if (!aimEnemy && wish.LengthSquared > 0.01)
                    {
                        Vec3d d = lookAt - eye;
                        wantYaw = Math.Atan2(d.X, d.Z);
                        wantPitch = 0;
                        gain = 0.15;
                    }
                    sprint = !_engaged && wish.LengthSquared > 0.01;
                }
                Unstick(s, tick, wish, ref jump);
            }
            else if (_engaged || (recentlySeen && tick - _lastSeenTick < 36 * SimTime.Per60))
            { wish = FightMove(server, s, tick, dist, ref jump); Goal = GoalKind.Fight; }
            else
            {
                Vec3d goal;
                bool br = server.Match.IsBr;
                // Battle royale: get out of the circle first, whatever else is going on; then into the next one in time.
                if (br && StormGoal(server, s, tick, out Vec3d safe)) { goal = safe; Goal = GoalKind.Storm; }
                else if (rotate) { goal = rotGoal; Doing = Tactic.Rotate; Goal = GoalKind.Rotate; }
                // A ping newer than its last sighting: go where it was shown (it still has to find and see them to fight).
                else if (!recentlySeen && _pingTick > _lastSeenTick && tick - _pingTick < 600 * SimTime.Per60) { goal = _pingPos; Goal = GoalKind.Ping; }
                else if (recentlySeen || tick - _lastSeenTick < 600 * SimTime.Per60) { goal = _lastSeenPos; Goal = GoalKind.LastSeen; }
                // Battle royale: a fight it heard and chose to join (third-partying).
                else if (brPlay && ThirdPartyGoal(server, s, tick, out Vec3d fight)) { goal = fight; Goal = GoalKind.ThirdParty; }
                else if (tick - _lastHeardTick < 600 * SimTime.Per60) { goal = _lastHeardPos; Goal = GoalKind.Heard; }
                // Battle royale: loot what is near (chests, a gun it lacks, a better rarity, a utility), else roam inside the circle.
                else if (br && LootGoal(server, me, s, tick, out Vec3d loot)) { goal = loot; Goal = GoalKind.Loot; }
                else
                {
                    goal = br ? RoamInCircle(server, s.Position, tick) : RoamGoal(s.Position);
                    Goal = goal.Equals(s.Position) || _shuffling && goal.Equals(_shuffleGoal) ? GoalKind.Hold : GoalKind.Roam;
                }
                if (br && server.Match.Phase == MatchPhase.Countdown) { goal = s.Position; Goal = GoalKind.Countdown; } // waits on its pad
                if (Doing == Tactic.Rotate && !(rotate && goal.Equals(rotGoal))) Doing = Tactic.None;
                GoalPos = goal;
                wish = FollowPath(s, goal, tick, ref jump, out Vec3d lookAt, out bool arrived);
                if (arrived)
                {
                    // Nothing here: forget it and look elsewhere.
                    if (goal.Equals(_pingPos)) _pingTick = int.MinValue / 2;
                    else if (goal.Equals(_lastSeenPos)) _lastSeenTick = int.MinValue / 2;
                    else if (goal.Equals(_lastHeardPos)) _lastHeardTick = int.MinValue / 2;
                    else if (Goal == GoalKind.Roam)
                    {
                        // Reached its roam point: hold it a while. Only a real arrival starts a hold: "arriving" at the spot
                        // it is holding (its own feet) used to restart the hold every tick, so a bot with nothing near to
                        // loot stood in one place for minutes (2026-11-28: half of all bot time in a 32-bot match).
                        _hasRoam = false;
                        if (br) _holdUntil = tick + (int)((Config.BrHoldMin + _rng.NextDouble() * (Config.BrHoldMax - Config.BrHoldMin)) * (1.4 - 0.8 * Aggression) * SimTime.TicksPerSecond);
                    }
                }
                if (Goal == GoalKind.Hold && !aimEnemy) LookAround(tick, ref wantYaw, ref wantPitch, ref gain, ref crouch);
                if (br && _lootId != 0) TakeLoot(server, s, tick, ref cmd);
                // At the end of its route and the item still out of reach (on a ledge or a floor the walking grid doesn't
                // reach): leave it rather than stand under it until the 25 s give-up.
                if (br && arrived && Goal == GoalKind.Loot && _lootId != 0)
                {
                    Vec3d ld = _lootPos - s.Position;
                    if (ld.HorizontalLength > 1.7 || Math.Abs(ld.Y) > 1.2) { _lootGaveUp.Add(_lootId); _lootId = 0; }
                }
                sprint = wish.LengthSquared > 0.01 && Goal != GoalKind.Hold;
                if (!aimEnemy && wish.LengthSquared > 0.01 && Goal != GoalKind.Hold)
                {
                    Vec3d d = lookAt - eye;
                    wantYaw = Math.Atan2(d.X, d.Z);
                    wantPitch = 0;
                    gain = 0.12;
                }
                // A shut door on the way: face it and open it like a person would (interact). If it won't open (someone
                // in the doorway, or it keeps shutting), punch it down: one punch breaks a leaf.
                if (wish.LengthSquared > 0.01 && DoorAhead(server.Grid, s, wish, mc, out Vec3d door, out int leaf))
                {
                    Vec3d d = door - eye;
                    wantYaw = Math.Atan2(d.X, d.Z);
                    wantPitch = Math.Atan2(d.Y, d.WithY(0).Length) * 180 / Math.PI;
                    gain = 0.25;
                    if (Math.Abs(Wrap(wantYaw - _yaw)) < 0.2 && tick - _punchTick > 30 * SimTime.Per60)
                    {
                        _punchTick = tick;
                        if (leaf == _doorLeaf && _doorTries >= 2) punch = true;
                        else
                        {
                            _doorTries = leaf == _doorLeaf ? _doorTries + 1 : 1;
                            _doorLeaf = leaf;
                            openDoor = leaf;
                        }
                    }
                }
                Unstick(s, tick, wish, ref jump);
            }
            if (tick < _jumpUntil) jump = true;
            WishLength = wish.Length;

            // The aim gains are tuned as "share of the error closed per sixtieth of a second": same pull at any tick rate.
            gain = 1 - Math.Pow(1 - Clamp(gain, 0, 1), 1.0 / SimTime.Per60);
            double dyaw = Wrap(wantYaw - _yaw);
            _yaw = Wrap(_yaw + Clamp(dyaw * gain, -turnCap, turnCap));
            _pitch += Clamp((wantPitch - _pitch) * gain, -turnCap * 180 / Math.PI, turnCap * 180 / Math.PI);
            _pitch = Clamp(_pitch, -80, 80);

            // ---- Guns.
            WeaponsConfig wc = mc.Weapons;
            Hand(ref cmd, s, tick, dist, recentlySeen);
            if (punch) cmd.Hand.Punch = true;
            if (openDoor >= 0) { cmd.Hand.Door = true; cmd.Hand.DoorTile = openDoor; cmd.Hand.DoorOpen = true; }
            bool fire = false;
            if (_engaged && tick >= _fireOkTick && s.Hand.HasGun && !s.Hand.InSculptMode)
            {
                Weapon held = s.Hand.Held;
                double range = held == Weapon.Pump ? 16 : held == Weapon.Smg ? 35 : 90;
                // Fires when the crosshair is on where it believes the target is now (seen late, moved on by its velocity).
                double ahead = Config.ReactionTicks / (double)SimTime.TicksPerSecond * Config.Lead;
                // Checked against the point it aims at: a head aim checked against the chest was always ~4 degrees off at 8 m.
                Vec3d chest = p.Pos + p.Vel.WithY(0) * ahead + new Vec3d(0, aimUp, 0);
                double dTrue = (chest - eye).Length;
                Vec3d look = WeaponRules.AimDir(_yaw + WobYaw(tick), _pitch + WobPitch(tick), 0, 0);
                double err = Math.Acos(Clamp(look.Dot((chest - eye) / Math.Max(1e-6, dTrue)), -1, 1)) * 180 / Math.PI;
                double tol = Math.Atan2(0.4, Math.Max(0.5, dTrue)) * 180 / Math.PI + 0.5 + (held == Weapon.Pump ? 2.0 : 0);
                bool burstPause = held == Weapon.Ar && dTrue > 20 && ((tick - _fireOkTick) % (30 * SimTime.Per60)) >= 15 * SimTime.Per60;
                fire = err < tol && dTrue < range && !burstPause && s.Weapon.UsedOf(held) < wc.Stats[(int)held].MagSize;
            }
            // ---- Heals: hurt, out of a fight for 2 s and nothing seen, it bandages up like a person would (it keeps walking).
            // In cover it starts sooner (1 s unseen): that is what it went there for.
            int healSlot = !_engaged && (tick - _lastSeenTick > 120 * SimTime.Per60 || (_coverHealing && tick - _lastSeenTick > 60 * SimTime.Per60)) ? HealSlot(s, mc) : -1;
            if (healSlot >= 0 && !s.Hand.InSculptMode)
            {
                Item want = s.Hand.Bag[healSlot];
                cmd.Hand.WeaponKey = s.Hand.HeldItem != want ? (byte)(healSlot + 1) : (byte)0; // never swapped back to a gun mid-use
                if (s.Hand.HeldItem == want && s.Hand.Mode == HandMode.Weapon) fire = true;
                cmd.Hand.Reload = false;
            }
            cmd.Move.WantsToShoot = _engaged;
            cmd.Move.FireHeld = fire;
            cmd.Move.FirePressed = fire && !_firedLast;
            _firedLast = fire;
            if (fire || _engaged) sprint = false;

            // World wish direction to strafe/forward axes of the view.
            double yawOut = _yaw + WobYaw(tick) - s.Weapon.RecoilYaw * Config.RecoilComp * Math.PI / 180;
            if (wish.LengthSquared > 1e-6)
            {
                Vec3d w = wish.WithY(0).Normalized * Math.Min(1, wish.Length);
                double sy = Math.Sin(yawOut), cy = Math.Cos(yawOut);
                cmd.Move.MoveZ = Clamp(w.X * sy + w.Z * cy, -1, 1);
                cmd.Move.MoveX = Clamp(w.X * cy - w.Z * sy, -1, 1);
                if (sprint && cmd.Move.MoveZ < 0.7) sprint = false;
            }
            cmd.Move.SprintHeld = sprint;
            cmd.Move.JumpPressed = jump;
            cmd.Move.JumpHeld = jump;
            cmd.Move.CrouchHeld = crouch;
            cmd.Move.Yaw = yawOut;
            cmd.Pitch = (float)Clamp(_pitch + WobPitch(tick) + s.Weapon.RecoilPitch * Config.RecoilComp, -InputCmd.MaxPitch, InputCmd.MaxPitch);
            return InputCmd.Quantize(cmd);
        }

        private InputCmd Finish(InputCmd cmd, in MotorState s)
        {
            cmd.Move.Yaw = _yaw;
            cmd.Pitch = (float)_pitch;
            _firedLast = false;
            return InputCmd.Quantize(cmd);
        }

        // ================================================================ Perception

        private static ServerPlayer FindEnemy(NetServer server, ServerPlayer me)
        {
            ServerPlayer best = null;
            double bestD = double.MaxValue;
            foreach (ServerPlayer o in server.Players)
            {
                if (o == me || o.Team == me.Team || o.State.Dead) continue;
                double d = (o.State.Position - me.State.Position).LengthSquared;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        private Obs Observe(NetServer server, ServerPlayer me, ServerPlayer enemy, Vec3d eye)
        {
            var o = new Obs();
            MotorState s = me.State;
            if (s.Damage > _lastDamage) _hurtTick = server.Tick;
            if (s.Damage > _lastDamage && enemy != null)
            {
                // Hit: it knows roughly where from (the damage direction; a person sees the same marker). In a battle
                // royale that is whoever hit it, not just the nearest enemy.
                bool br = server.Match.IsBr;
                ServerPlayer by = br && server.Tick - me.LastHurtTick <= 1 ? server.FindPlayer(me.LastHurtBy) : null;
                Vec3d from = by != null && !by.State.Dead ? by.State.Position : enemy.State.Position;
                o = Heard(o, from, br ? 2.0 + (from - s.Position).Length * 0.05 : 2.0);
                o.Hurt = true;
            }
            _lastDamage = s.Damage;
            if (enemy == null) return o;
            o.Bot = NetServer.IsBotConnection(enemy.ConnectionId);
            o.Id = enemy.Id;
            MotorState e = enemy.State;
            double h = PlayerMovement.HeightOf(e, server.Movement);
            o.Height = h;
            Vec3d to = e.Position - s.Position;
            double flat = to.HorizontalLength;
            double off = Math.Abs(Wrap(Math.Atan2(to.X, to.Z) - _yaw)) * 180 / Math.PI;
            bool inCone = off <= Config.ViewConeDeg * 0.5 || flat < 2.5;
            // Past SightMetres nobody is made out (the haze closes in by about 330 m): no sight lines tested that far,
            // which across the dense battle royale villages were most of a bot's cost.
            if (inCone && flat <= Config.SightMetres && MaySee(server, eye, e.Position, h) && CanSee(server.Grid, eye, e.Position, h))
            {
                o.Seen = true;
                o.Pos = e.Position;
                o.Vel = e.Velocity;
            }
            // The ping shows them through walls to everyone it concerns, the bot included: like a sighting of where
            // they are, but nothing to aim at (it acts on it a reaction time late and has to go and find them).
            if (!o.Seen && server.IsRevealed(me.Id, enemy.Id))
            {
                o.Pinged = true;
                o.PingPos = e.Position;
            }
            if (!o.Seen)
            {
                double d = to.Length;
                if (server.Tick - enemy.LastShotTick <= 1 && d < Config.HearShots) o = Heard(o, e.Position, 1.5 + d * 0.04);
                else if (e.Grounded && e.Velocity.HorizontalLength > 5 && d < Config.HearSteps) o = Heard(o, e.Position, 1.5);
            }
            return o;
        }

        private Obs Heard(Obs o, Vec3d at, double noise)
        {
            o.Heard = true;
            o.HeardPos = at + new Vec3d((_rng.NextDouble() * 2 - 1) * noise, 0, (_rng.NextDouble() * 2 - 1) * noise);
            return o;
        }

        /// <summary>Whether any of the three lines <see cref="CanSee"/> tests could be clear, from the server's sight grid
        /// (far cheaper than ray casts; in the battle royale's villages most lines are blocked by a building).</summary>
        private static bool MaySee(NetServer server, Vec3d eye, Vec3d feet, double height) =>
            server.SightMayBeClear(eye, feet + new Vec3d(0, height - 0.15, 0)) || server.SightMayBeClear(eye, feet + new Vec3d(0, height * 0.6, 0))
            || server.SightMayBeClear(eye, feet + new Vec3d(0, 0.35, 0));

        /// <summary>A clear line from the eye to the target's head, chest or knees (terrain only, like bullets).</summary>
        public static bool CanSee(ITerrain t, Vec3d eye, Vec3d feet, double height)
        {
            double[] ups = { height - 0.15, height * 0.6, 0.35 };
            foreach (double up in ups)
            {
                Vec3d d = feet + new Vec3d(0, up, 0) - eye;
                double len = d.Length;
                if (len < 1e-3) return true;
                TerrainHit hit = t.Raycast(eye, d / len, len);
                if (!hit.Hit || hit.Distance >= len - 0.05) return true;
            }
            return false;
        }

        private void Store(int tick, Obs o)
        {
            o.Tick = tick;
            _ring[tick & (_ring.Length - 1)] = o;
        }

        private Obs Recall(int tick)
        {
            Obs o = _ring[tick & (_ring.Length - 1)];
            return o.Tick == tick ? o : default;
        }

        // ================================================================ Weapons

        private void Hand(ref InputCmd cmd, in MotorState s, int tick, double dist, bool recentlySeen)
        {
            Weapon cur = WantGun;
            Weapon want = cur;
            if (recentlySeen)
            {
                switch (cur)
                {
                    // Balance pass 2 (2026-11-26): the SMG falls off from 7 m, so the AR takes over from ~16 m (was 26).
                    case Weapon.Pump: if (dist > 10) want = dist > 18 ? Weapon.Ar : Weapon.Smg; break;
                    case Weapon.Smg: if (dist < 7) want = Weapon.Pump; else if (dist > 16) want = Weapon.Ar; break;
                    case Weapon.Ar: if (dist < 12) want = dist < 7 ? Weapon.Pump : Weapon.Smg; break;
                    default: want = Weapon.Smg; break;
                }
            }
            else if (cur == Weapon.Pistol) want = Weapon.Smg;
            // Empty in a fight: swap instead of reloading (pump <-> SMG).
            if (_engaged && s.Hand.HasGun && s.Weapon.UsedOf(s.Hand.Held) >= Stats(s.Hand.Held).MagSize)
                want = s.Hand.Held == Weapon.Pump ? Weapon.Smg : Weapon.Pump;
            want = Owned(want, s.Hand.Bag);
            bool lacking = s.Hand.Bag.SlotOf(Items.Of(cur)) < 0;
            if (want != cur && (lacking || tick - _switchTick >= 60 * SimTime.Per60)) { WantGun = want; _switchTick = tick; }

            int slot = BestCopy(s.Hand.Bag, WantGun);
            bool holding = s.Hand.HasGun && s.Hand.Held == WantGun && (slot < 0 || s.Hand.Bag.RarityOf(s.Hand.Slot) >= s.Hand.Bag.RarityOf(slot));
            // In a fight with no gun out (a heal in hand), it gets a gun out at once.
            if (!holding && slot >= 0 && (tick % (20 * SimTime.Per60) == 0 || (_engaged && !s.Hand.HasGun && s.Hand.Mode == HandMode.Weapon)))
                cmd.Hand.WeaponKey = (byte)(slot + 1);
            else if (holding && !_engaged && !s.Weapon.Reloading && tick - _reloadTick > 60 * SimTime.Per60
                     && s.Weapon.UsedOf(WantGun) * 10 >= Stats(WantGun).MagSize * 4)
            {
                cmd.Hand.Reload = true;
                _reloadTick = tick;
            }
        }

        /// <summary>The slot of its best copy of a gun (highest rarity; -1 if it has none). Duplicate guns are allowed.</summary>
        private static int BestCopy(in Bag bag, Weapon w)
        {
            Item i = Items.Of(w);
            int best = -1;
            for (int k = 0; k < Items.Slots; k++)
                if (bag[k] == i && (best < 0 || bag.RarityOf(k) > bag.RarityOf(best))) best = k;
            return best;
        }

        /// <summary>The slot of the heal it would use now (a first-aid kit when badly hurt, else a bandage), or -1.</summary>
        private static int HealSlot(in MotorState s, MovementConfig mc)
        {
            if (s.Damage < 20) return -1;
            Bag b = s.Hand.Bag;
            int kit = b.SlotOf(Item.Medkit), band = b.SlotOf(Item.Bandage);
            if (kit >= 0 && b.CountIn(kit) == 0) kit = -1;
            if (band >= 0 && b.CountIn(band) == 0) band = -1;
            if (s.Damage >= 70 && kit >= 0) return kit;
            return band >= 0 ? band : kit;
        }

        /// <summary>The gun it carries nearest in use to the one it wants (a battle royale bag is not the full loadout).</summary>
        private static Weapon Owned(Weapon want, in Bag bag)
        {
            if (bag.SlotOf(Items.Of(want)) >= 0) return want;
            Weapon[] order;
            switch (want)
            {
                case Weapon.Pump: order = new[] { Weapon.Smg, Weapon.Pistol, Weapon.Ar }; break;
                case Weapon.Smg: order = new[] { Weapon.Pump, Weapon.Ar, Weapon.Pistol }; break;
                case Weapon.Ar: order = new[] { Weapon.Pistol, Weapon.Smg, Weapon.Pump }; break;
                default: order = new[] { Weapon.Ar, Weapon.Smg, Weapon.Pump }; break;
            }
            foreach (Weapon w in order) if (bag.SlotOf(Items.Of(w)) >= 0) return w;
            return want;
        }

        private WeaponsConfig _wc = new WeaponsConfig();
        private NetServer _server;
        private WeaponStats Stats(Weapon w) => _wc.Stats[(int)w];

        // ================================================================ Movement

        private Vec3d FightMove(NetServer server, in MotorState s, int tick, double dist, ref bool jump)
        {
            if (tick >= _strafeUntil)
            {
                _strafe = _rng.NextDouble() < 0.5 ? -_strafe : _strafe;
                if (_strafe == 0) _strafe = 1;
                _strafeUntil = tick + (int)((Config.StrafeMin + _rng.NextDouble() * (Config.StrafeMax - Config.StrafeMin)) * SimTime.TicksPerSecond);
            }
            Vec3d to = (_lastSeenPos - s.Position).WithY(0);
            double len = to.Length;
            Vec3d fwd = len > 1e-3 ? to / len : new Vec3d(Math.Sin(_yaw), 0, Math.Cos(_yaw));
            Vec3d side = new Vec3d(fwd.Z, 0, -fwd.X) * _strafe;
            double pref = WantGun == Weapon.Pump ? 4 : WantGun == Weapon.Smg ? 9 : 18;
            if (_push) pref = Math.Max(3, pref * 0.5); // battle royale: they're nearly down, close in
            Vec3d wish = side;
            if (dist > pref + 5)
            {
                Vec3d path = FollowPath(s, _lastSeenPos, tick, ref jump, out _, out _);
                wish = path.LengthSquared > 0.01 ? path * 0.8 + side * 0.5 : fwd * 0.8 + side * 0.5;
            }
            else if (dist < pref - 2) wish = side * 0.7 - fwd * 0.7;
            if (!Walkable(server.Grid, s.Position, wish))
            {
                _strafe = -_strafe;
                side = -side;
                wish = dist > pref + 5 ? fwd * 0.8 + side * 0.5 : dist < pref - 2 ? side * 0.7 - fwd * 0.7 : side;
                if (!Walkable(server.Grid, s.Position, wish)) wish = Walkable(server.Grid, s.Position, side) ? side : Vec3d.Zero;
            }
            if (s.Grounded && dist < 8 && _rng.NextDouble() < Config.FightHopChance / SimTime.Per60) jump = true; // the chance is per sixtieth of a second
            return wish;
        }

        /// <summary>Solid floor a step ahead, nothing in the way, and no drop (a ledge in a fight is a mistake).</summary>
        private static bool Walkable(ITerrain t, Vec3d feet, Vec3d wish)
        {
            if (wish.LengthSquared < 1e-6) return true;
            Vec3d d = wish.WithY(0).Normalized;
            Vec3d p = feet + d * 0.9;
            if (t.BoxOverlapsSolid(new Aabb(new Vec3d(p.X - 0.3, feet.Y + 0.45, p.Z - 0.3), new Vec3d(p.X + 0.3, feet.Y + 1.6, p.Z + 0.3)))) return false;
            TerrainHit h = t.Raycast(new Vec3d(p.X, feet.Y + 0.4, p.Z), new Vec3d(0, -1, 0), 1.4);
            return h.Hit;
        }

        /// <summary>A shut door leaf right in front along <paramref name="wish"/> (its middle, to face).</summary>
        private bool DoorAhead(ITerrain t, in MotorState s, Vec3d wish, MovementConfig mc, out Vec3d at, out int leaf)
        {
            at = default;
            leaf = -1;
            TileWalls walls = TileWalls.Find(t);
            if (walls == null) return false;
            Vec3d d = wish.WithY(0).Normalized;
            Vec3d p = s.Position + d * 0.9;
            var box = new Aabb(new Vec3d(p.X - 0.3, s.Position.Y + 0.5, p.Z - 0.3), new Vec3d(p.X + 0.3, s.Position.Y + 1.6, p.Z + 0.3));
            List<int> near = _doorNear ??= new List<int>();
            walls.TilesNear(box, near);
            foreach (int i in near)
            {
                WallTile tile = walls[i];
                if (tile.Kind != TileKind.Door || TileWalls.Gone(tile.State)) continue;
                Aabb b = walls.BoundsOf(i);
                leaf = i;
                at = new Vec3d((b.Min.X + b.Max.X) / 2, s.Position.Y + PlayerMovement.EyeOf(s, mc) - 0.1, (b.Min.Z + b.Max.Z) / 2);
                return true;
            }
            return false;
        }

        private List<int> _doorNear;
        private int _punchTick = int.MinValue / 2;
        private int _doorLeaf = -1, _doorTries;

        private Vec3d RoamGoal(Vec3d from)
        {
            if (_hasRoam) return _roamGoal;
            // A random open spot: the middle of the map first (where fights happen), then anywhere.
            IReadOnlyList<BotNav.Node> nodes = Nav.Nodes;
            Vec3d centre = new Vec3d(Nav.MinX + Nav.NX * Nav.Cell * 0.5, 0, Nav.MinZ + Nav.NZ * Nav.Cell * 0.5);
            for (int k = 0; k < 40; k++)
            {
                int i = _rng.Next(nodes.Count);
                BotNav.Node n = nodes[i];
                if (n.Open < 8) continue;
                Vec3d f = n.Feet(Nav);
                if ((f - from).HorizontalLength < 10) continue;
                if (_rng.NextDouble() < 0.6 && (f - centre).HorizontalLength > Nav.NX * Nav.Cell * 0.3) continue;
                _roamGoal = f;
                _hasRoam = true;
                return f;
            }
            _roamGoal = centre;
            _hasRoam = true;
            return centre;
        }

        /// <summary>World direction to walk this tick along a path to the goal (re-planned when the goal moves or the bot strays).</summary>
        /// <summary>Far goals are walked to in legs this long (see <see cref="FollowPath"/>).</summary>
        private const double LegMetres = 60;
        private bool _partial;
        private int _pathWaitSince = int.MinValue / 2, _failRun, _legNode = -1;
        private Vec3d _legGoal;

        private Vec3d FollowPath(in MotorState s, Vec3d goal, int tick, ref bool jump, out Vec3d lookAt, out bool arrived)
        {
            arrived = false;
            lookAt = goal;
            Vec3d feet = s.Position;
            if ((feet - goal).HorizontalLength < 1.2 && Math.Abs(feet.Y - goal.Y) < 1.5) { arrived = true; _path.Clear(); return Vec3d.Zero; }

            bool replan = _path.Count == 0 || _pathAt >= _path.Count || (goal - _pathGoal).Length > 2.5 || tick - _pathTick > 120 * SimTime.Per60;
            if (!replan)
            {
                Vec3d next = Nav.Feet(_path[_pathAt]);
                if ((next - feet).HorizontalLength > 4 || feet.Y < next.Y - 2.2) replan = true; // fell off or got pushed
            }
            double far = (goal - feet).HorizontalLength;
            // Bots share a small search budget per tick and are served in a fixed order, so one late in the order could
            // wait for minutes while earlier bots kept asking (2026-11-28: bots stuck in a pit or rotating blind for 3 min).
            // A bot that has waited a quarter second may go over the budget by two.
            if (!replan) _pathWaitSince = int.MinValue / 2;
            else if (_pathWaitSince == int.MinValue / 2) _pathWaitSince = tick;
            int budget = _server == null ? int.MaxValue : _server.BotPathsPerTick + (tick - _pathWaitSince >= 30 * SimTime.Per60 ? 2 : 0);
            bool mayAsk = tick - _pathTick >= 15 * SimTime.Per60 && (_server == null || _server.BotPathsThisTick < budget);
            if (replan && !mayAsk) PathNote = 1;
            if (replan && mayAsk)
            {
                _pathWaitSince = int.MinValue / 2;
                PathAsks++;
                PathNote = 0;
                if (_server != null) _server.BotPathsThisTick++;
                _pathTick = tick;
                _pathGoal = goal;
                _pathAt = 0;
                // A far goal (across the battle royale map: the circle, a roam point) is reached in legs of up to
                // LegMetres: one search over the whole map would give up before it got there (the search is capped).
                _partial = false;
                int a = Nav.Nearest(feet), b = -1;
                // Several searches in a row found nothing: the spot it was matched to may be a dead end (a sill, a ledge,
                // a corner). Start from a spot a step or two away instead.
                if (_failRun >= 3)
                {
                    double ang = _rng.NextDouble() * Math.PI * 2;
                    int alt = Nav.Nearest(new Vec3d(feet.X + Math.Sin(ang) * 1.5, feet.Y, feet.Z + Math.Cos(ang) * 1.5), 2);
                    if (alt >= 0) a = alt;
                }
                bool found = false;
                if (far > LegMetres)
                {
                    // Up to three tries, nearer each time. A leg's end at the bot's own height (not a roof over the
                    // spot: one a 280 m walk round ran past the search cap every time, and the bot stood jumping at a
                    // building corner until the circle came, 2026-11-28), in its own area (not a sealed room).
                    Vec3d dir = (goal - feet).WithY(0) / far;
                    int tries = 0;
                    // Keep the leg it is walking until it gets there: a fresh leg end every re-plan could fall on the
                    // other side of a building, and it ran back and forth between two routes until the circle caught it.
                    if (_legNode >= 0 && (goal - _legGoal).HorizontalLength < 30 && (Nav.Feet(_legNode) - feet).HorizontalLength > 6
                        && Nav.SameArea(a, _legNode))
                    {
                        tries++;
                        b = _legNode;
                        found = Nav.FindPath(a, b, _path);
                    }
                    for (double leg = LegMetres; leg >= 20 && !found && tries < 3; leg -= 10)
                    {
                        b = Nav.Nearest(new Vec3d(feet.X + dir.X * leg, feet.Y, feet.Z + dir.Z * leg), 8);
                        if (!Nav.SameArea(a, b) || Nav.Feet(b).Y - feet.Y > 3.5 && leg > 20) { b = -1; continue; } // down from upstairs is fine
                        if (tries > 0)
                        {
                            // Another search costs another share of this tick's budget; past it, try again next time.
                            if (_server != null && _server.BotPathsThisTick >= budget) break;
                            if (_server != null) _server.BotPathsThisTick++;
                            PathAsks++;
                        }
                        tries++;
                        found = Nav.FindPath(a, b, _path);
                    }
                    _partial = found;
                    if (found) { _legNode = b; _legGoal = goal; } else _legNode = -1;
                }
                else _legNode = -1;
                if (!found && far <= LegMetres)
                {
                    b = Nav.Nearest(goal, 6);
                    found = Nav.FindPath(a, b, _path);
                }
                // Loot whose walk round is far longer than it looks (the other side of a long wall, up a tower): not worth it.
                if (found && !_partial && _lootId != 0 && goal.Equals(_lootPos) && _path.Count * Nav.Cell > 2.5 * far + 25)
                {
                    _lootGaveUp.Add(_lootId);
                    _lootId = 0;
                }
                _failRun = found ? 0 : _failRun + 1;
                if (!found)
                {
                    PathFails++;
                    PathNote = a < 0 || b < 0 ? 3 : 2;
                    if (PathNote == 3) PathNoSpot++;
                    _path.Clear();
                    _partial = false;
                    _hasRoam = false; // unreachable (or a long way round): pick another roam point
                    if (far <= LegMetres)
                    {
                        if (goal.Equals(_lastSeenPos)) _lastSeenTick = int.MinValue / 2;
                        if (goal.Equals(_lastHeardPos)) _lastHeardTick = int.MinValue / 2;
                        if (goal.Equals(_pingPos)) _pingTick = int.MinValue / 2;
                        if (_lootId != 0 && goal.Equals(_lootPos)) { _lootGaveUp.Add(_lootId); _lootId = 0; } // can't get there: other loot
                    }
                }
            }
            if (_path.Count == 0 || _pathAt >= _path.Count)
            {
                if (_path.Count > 0 && !_partial) arrived = true;
                else if (_path.Count > 0) _path.Clear(); // the end of a leg: plan the next one
                // No path yet (or none found) to a far goal: head straight for it rather than stand still (a bot stuck
                // upstairs in the circle used to wait there until it died).
                // Same while it waits its turn for a route search (bots share a budget): a person doesn't stop to think.
                if (!arrived && (far > LegMetres || PathNote == 1 && far > 1.2)) return (goal - feet).WithY(0) / far;
                return Vec3d.Zero;
            }

            // Move on to the next node once close (and past nodes the bot already reached).
            while (_pathAt < _path.Count - 1)
            {
                Vec3d n = Nav.Feet(_path[_pathAt]);
                if ((n - feet).HorizontalLength < 0.55 && Math.Abs(n.Y - feet.Y) < 0.9) _pathAt++;
                else break;
            }
            int target = _path[_pathAt];
            Vec3d tp = Nav.Feet(target);
            if (_pathAt > 0 && Nav.LinkBetween(_path[_pathAt - 1], target) == BotNav.Link.Jump && (tp - feet).HorizontalLength < 1.4 && s.Grounded)
                jump = true;
            // Look and steer a little further ahead for smooth corners.
            int look = Math.Min(_path.Count - 1, _pathAt + 3);
            lookAt = Nav.Feet(_path[look]) + new Vec3d(0, 1.6, 0);
            Vec3d d = (tp - feet).WithY(0);
            // Right above (or below) the next spot but not on it: standing on the lip of a drop, held up by the edge of
            // the floor under one side of its box. Keep going the way the route runs so it steps off (it used to aim at
            // the spot straight below its own feet and stand on a roof edge until the circle killed it).
            if (d.Length < 0.35 && Math.Abs(tp.Y - feet.Y) >= 0.9 && _pathAt > 0)
            {
                Vec3d run = (tp - Nav.Feet(_path[_pathAt - 1])).WithY(0);
                if (run.Length < 0.1 && _pathAt + 1 < _path.Count) run = (Nav.Feet(_path[_pathAt + 1]) - tp).WithY(0);
                if (run.Length > 0.1) d = run;
            }
            if (_pathAt == _path.Count - 1 && d.Length < 0.4)
            {
                if (_partial) { _path.Clear(); return (goal - feet).WithY(0) / Math.Max(1e-6, far); } // next leg
                arrived = true;
                return Vec3d.Zero;
            }
            return d.Length > 1e-6 ? d.Normalized : Vec3d.Zero;
        }

        // ================================================================ Battle royale

        private int _lootId;
        private Vec3d _lootPos;
        private int _lootSince, _lootNearSince, _lootPickTick = int.MinValue / 2, _lootScanTick = int.MinValue / 2;
        private readonly HashSet<int> _lootGaveUp = new HashSet<int>();
        private int _lootMatch = -1;

        /// <summary>The circle it should be inside: the next one once its shrink is near, else the current one.</summary>
        private static void SafeCircle(NetServer server, int tick, out double x, out double z, out double r)
        {
            StormState st = server.Match.Storm;
            if (tick >= st.ShrinkStart - 15 * 60 * SimTime.Per60) { x = st.ToX; z = st.ToZ; r = st.ToRadius; }
            else { x = st.CentreXAt(tick); z = st.CentreZAt(tick); r = st.RadiusAt(tick); }
        }

        /// <summary>Outside (or at the edge of) the safe circle: a point well inside it to walk to.</summary>
        private bool StormGoal(NetServer server, in MotorState s, int tick, out Vec3d goal)
        {
            goal = default;
            StormState st = server.Match.Storm;
            if (server.Match.Phase != MatchPhase.Playing || !st.Active) return false;
            SafeCircle(server, tick, out double cx, out double cz, out double r);
            double dx = s.Position.X - cx, dz = s.Position.Z - cz;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d < Math.Max(0, r - 4)) return false;
            double into = Math.Max(0, Math.Min(r * 0.4, d));
            goal = d > 1e-3 ? new Vec3d(cx + dx / d * into, s.Position.Y, cz + dz / d * into) : new Vec3d(cx, s.Position.Y, cz);
            _lootId = 0;
            return true;
        }

        private bool Wants(in GroundItem g, in Bag bag)
        {
            if (g.Kind == Item.Chest) return true;
            bool free = bag.FirstEmpty() >= 0;
            if (Items.IsGun(g.Kind))
            {
                // A gun it lacks with room for it, or a better copy of one it has (a full bag swaps out the worse copy).
                int best = BestCopy(bag, Items.GunOf(g.Kind));
                if (best >= 0) return g.Rarity > bag.RarityOf(best);
                return free;
            }
            if (Items.Stacks(g.Kind))
            {
                // More of a stack it has room in, or a heal or a first utility when a slot is free.
                int have = bag.SlotOf(g.Kind);
                if (have >= 0) return bag.Count(have) < _server.Movement.Items.Carry(g.Kind);
                if (!free) return false;
                if (Items.IsHeal(g.Kind)) return true;
                for (int k = 0; k < Items.Slots; k++) if (Items.Throws(bag[k]) && bag.CountIn(k) > 0) return false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Someone else will get there first: another bot is heading for it or for something within 4 m of it (a chest and
        /// its spill count as one spot), or, in the calm opening, an enemy is already near it and nearer than this bot.
        /// Bots then pick other loot, so two bots rarely meet at one chest in the first seconds (2026-11-27: they used to,
        /// and shotgunned each other).
        /// </summary>
        private bool Contested(NetServer server, ServerPlayer me, in GroundItem g, int tick, bool calm)
        {
            foreach (var kv in server.BotLootClaims)
            {
                if (kv.Key == me.Id || tick - kv.Value.tick >= 90 * SimTime.Per60) continue;
                if ((kv.Value.at - g.Position).WithY(0).LengthSquared > 4 * 4 || Math.Abs(kv.Value.at.Y - g.Position.Y) > 2) continue;
                if (server.FindPlayer(kv.Key) is ServerPlayer holder && !holder.State.Dead) return true;
            }
            if (!calm) return false;
            double mine = (g.Position - me.State.Position).WithY(0).LengthSquared;
            foreach (ServerPlayer o in server.Players)
            {
                if (o == me || o.Team == me.Team || o.State.Dead || !server.InBrMatch(o.Id)) continue;
                double d = (g.Position - o.State.Position).WithY(0).LengthSquared;
                if (d < 10 * 10 && d < mine) return true;
            }
            return false;
        }

        /// <summary>The nearest thing worth picking up within 45 m (re-chosen every half second) that nobody else is
        /// about to take.</summary>
        private bool LootGoal(NetServer server, ServerPlayer me, in MotorState s, int tick, out Vec3d goal)
        {
            goal = default;
            if (server.Match.Phase != MatchPhase.Playing) { _lootId = 0; return false; }
            if (_lootMatch != server.BrMatchNumber) { _lootMatch = server.BrMatchNumber; _lootGaveUp.Clear(); _lootId = 0; }
            Bag bag = s.Hand.Bag;
            if (_lootId != 0 && (!server.Items.TryGet(_lootId, out GroundItem cur) || !Wants(cur, bag))) _lootId = 0;
            if (_lootId == 0 || tick - _lootScanTick >= 60 * SimTime.Per60)
            {
                _lootScanTick = tick;
                bool calm = Calm(server, me, tick);
                int best = 0;
                double bestD = 45 * 45;
                Vec3d bestPos = default;
                for (int i = 0; i < server.Items.Count; i++)
                {
                    GroundItem g = server.Items[i];
                    if (_lootGaveUp.Contains(g.Id) || !Wants(g, bag)) continue;
                    Vec3d d = g.Position - s.Position;
                    double d2 = d.X * d.X + d.Z * d.Z + 4 * d.Y * d.Y; // another floor is further than it looks
                    // What it is already heading for counts as nearer (to about 63 m): a route that first leads away
                    // took it past 45 m, it dropped the loot, turned round, came back in range and took it up again,
                    // running back and forth on one spot (2026-11-28).
                    if (g.Id == _lootId) d2 *= 0.5;
                    if (d2 >= bestD) continue;
                    if (server.InStorm(g.Position)) continue;
                    if (Contested(server, me, g, tick, calm)) continue;
                    // Out of reach without breaking a wall (a sealed room): bots don't breach, so they leave it.
                    if (!Nav.SameArea(Nav.Nearest(s.Position), Nav.Nearest(g.Position, 3))) { _lootGaveUp.Add(g.Id); continue; }
                    bestD = d2;
                    best = g.Id;
                    bestPos = g.Position;
                }
                if (best != _lootId) { _lootId = best; _lootPos = bestPos; _lootSince = tick; _lootNearSince = int.MinValue / 2; }
                if (best != 0) server.BotLootClaims[me.Id] = (bestPos, tick);
                else server.BotLootClaims.Remove(me.Id);
            }
            if (_lootId == 0) return false;
            if (tick - _lootSince > 25 * 60 * SimTime.Per60) { _lootGaveUp.Add(_lootId); _lootId = 0; return false; }
            goal = _lootPos;
            return true;
        }

        /// <summary>Close enough: press interact on it (the server checks reach and sight like anyone's).</summary>
        private void TakeLoot(NetServer server, in MotorState s, int tick, ref InputCmd cmd)
        {
            Vec3d d = _lootPos - s.Position;
            if (d.HorizontalLength > 1.7 || Math.Abs(d.Y) > 1.2) { _lootNearSince = int.MinValue / 2; return; }
            if (_lootNearSince < 0 && _lootNearSince == int.MinValue / 2) _lootNearSince = tick;
            if (tick - _lootNearSince > 3 * 60 * SimTime.Per60) { _lootGaveUp.Add(_lootId); _lootId = 0; return; }
            if (tick - _lootPickTick < 15 * SimTime.Per60) return;
            _lootPickTick = tick;
            // A better copy of a gun with a full bag: hold the worse copy first, so the pickup swaps that one out.
            if (server.Items.TryGet(_lootId, out GroundItem g) && Items.IsGun(g.Kind) && s.Hand.Bag.FirstEmpty() < 0)
            {
                int worst = -1;
                for (int k = 0; k < Items.Slots; k++)
                    if (s.Hand.Bag[k] == g.Kind && (worst < 0 || s.Hand.Bag.RarityOf(k) < s.Hand.Bag.RarityOf(worst))) worst = k;
                if (worst >= 0 && s.Hand.Slot != worst) { cmd.Hand.WeaponKey = (byte)(worst + 1); return; }
            }
            cmd.Hand.Pickup = true;
            cmd.Hand.PickId = _lootId;
        }

        private int _circleRoamTick = int.MinValue / 2;

        /// <summary>A random open spot inside the circle (the next one when it is known).</summary>
        private Vec3d RoamInCircle(NetServer server, Vec3d from, int tick)
        {
            StormState st = server.Match.Storm;
            if (server.Match.Phase != MatchPhase.Playing || !st.Active) return RoamGoal(from);
            SafeCircle(server, tick, out double cx, out double cz, out double r);
            if (_hasRoam && tick - _circleRoamTick < 20 * 60 * SimTime.Per60)
            {
                double gx = _roamGoal.X - cx, gz = _roamGoal.Z - cz;
                if (gx * gx + gz * gz < r * r) return _roamGoal;
            }
            // Like a person playing a battle royale (2026-11-27): it holds a spot for a while, then moves on a short way,
            // drifting toward the middle of the circle, instead of crossing the map to random points (32 bots doing that
            // met each other constantly and the lobby was down to a handful three minutes in).
            if (!_hasRoam && tick < _holdUntil)
            {
                // Holding: now and then it shifts a few steps (a new angle, out of a line it has been standing in),
                // like a person waiting out a spot, rather than standing on one tile.
                if (tick >= _shuffleTick)
                {
                    _shuffleTick = tick + (int)((4 + _rng.NextDouble() * 6) * SimTime.TicksPerSecond);
                    _shuffling = false;
                    double ang = _rng.NextDouble() * Math.PI * 2, d = 2 + _rng.NextDouble() * 3;
                    int here = Nav.Nearest(from), node = Nav.Nearest(new Vec3d(from.X + Math.Sin(ang) * d, from.Y, from.Z + Math.Cos(ang) * d), 1);
                    if (node >= 0 && Nav.SameArea(here, node) && Nav.Nodes[node].Open >= 4 && Math.Abs(Nav.Feet(node).Y - from.Y) < 1)
                    {
                        _shuffleGoal = Nav.Feet(node);
                        _shuffling = true;
                    }
                }
                return _shuffling ? _shuffleGoal : from;
            }
            _shuffling = false;
            _circleRoamTick = tick;
            for (int k = 0; k < 12; k++)
            {
                double a = _rng.NextDouble() * Math.PI * 2, d = 20 + _rng.NextDouble() * (20 + 35 * Aggression); // careful bots move on a shorter way
                double x = from.X + Math.Sin(a) * d, z = from.Z + Math.Cos(a) * d;
                // Drift toward the middle: pushy bots head in, careful ones keep to the quieter edges of the circle (all of
                // them pulling in 30% each move packed the lobby into the middle and made the middle minutes a brawl).
                double pull = 0.06 + 0.24 * Aggression;
                x += (cx - x) * pull;
                z += (cz - z) * pull;
                double fx = x - cx, fz = z - cz, lim = r * 0.8;
                double fd = Math.Sqrt(fx * fx + fz * fz);
                if (fd > lim && fd > 1e-6) { x = cx + fx / fd * lim; z = cz + fz / fd * lim; }
                int node = Nav.Nearest(new Vec3d(x, from.Y, z), 6);
                // Open ground near its own level, not a roof it would need a long way round to reach.
                if (node < 0 || Nav.Nodes[node].Open < 8 || Nav.Feet(node).Y - from.Y > 4) continue;
                // Slipped away from someone lately: not back toward them.
                if (k < 9 && tick - _avoidTick < 30 * SimTime.TicksPerSecond)
                {
                    Vec3d to = (Nav.Feet(node) - from).WithY(0), away = (_avoidPos - from).WithY(0);
                    if (to.Length > 1 && away.Length > 1 && to.Dot(away) / (to.Length * away.Length) > 0.2) continue;
                }
                _roamGoal = Nav.Feet(node);
                _hasRoam = true;
                return _roamGoal;
            }
            _roamGoal = new Vec3d(cx, from.Y, cz);
            _hasRoam = true;
            return _roamGoal;
        }

        private int _holdUntil = int.MinValue / 2, _shuffleTick = int.MinValue / 2;
        private bool _shuffling;
        private Vec3d _shuffleGoal;
        private int _avoidRollTick = int.MinValue / 2, _avoidTick = int.MinValue / 2;
        private Vec3d _avoidPos;
        /// <summary>For measuring: enemies it chose to slip away from (Tests/BrPerf).</summary>
        public int AvoidCount;
        private int _lookUntil = int.MinValue / 2;
        private double _lookYaw, _lookPitch;
        private bool _holdCrouch;

        /// <summary>
        /// Holding a spot like a person: it checks the angles (a new heading every 1-3 s, mostly ahead and to the sides,
        /// sometimes behind) and careful bots crouch, instead of staring at one wall.
        /// </summary>
        private void LookAround(int tick, ref double wantYaw, ref double wantPitch, ref double gain, ref bool crouch)
        {
            if (tick >= _lookUntil)
            {
                _lookUntil = tick + (int)((1.0 + _rng.NextDouble() * 2.0) * SimTime.TicksPerSecond);
                double turn = _rng.NextDouble() < 0.2 ? Math.PI * (0.7 + _rng.NextDouble() * 0.3) : (_rng.NextDouble() - 0.5) * 2.2;
                _lookYaw = Wrap(_yaw + (_rng.NextDouble() < 0.5 ? turn : -turn));
                _lookPitch = (_rng.NextDouble() - 0.6) * 10;
                _holdCrouch = _rng.NextDouble() < 0.6 * (1 - Aggression);
            }
            wantYaw = _lookYaw;
            wantPitch = _lookPitch;
            gain = 0.1;
            crouch = _holdCrouch;
        }
        private bool _push;

        private void Unstick(in MotorState s, int tick, Vec3d wish, ref bool jump)
        {
            if (wish.LengthSquared < 0.01) { _stuckFrom = s.Position; _stuckCheck = tick; return; }
            if (tick - _stuckCheck < 45 * SimTime.Per60) return;
            bool stuck = (s.Position - _stuckFrom).HorizontalLength < 0.6;
            _stuckFrom = s.Position;
            _stuckCheck = tick;
            if (!stuck) { _stuckCount = 0; return; }
            _stuckCount++;
            _jumpUntil = tick + 2 * SimTime.Per60;
            _pathTick = int.MinValue / 2; // re-plan from here
            _path.Clear();
            if (_stuckCount >= 3)
            {
                _hasRoam = false;
                _lastSeenTick = _lastHeardTick = int.MinValue / 2;
                _stuckCount = 0;
                if (_lootId != 0) { _lootGaveUp.Add(_lootId); _lootId = 0; } // stuck on the way to it (1.5 s x 3): other loot
            }
        }

        // ================================================================ Helpers

        private double WobYaw(int tick)
        {
            double t = tick / (double)SimTime.TicksPerSecond;
            return Config.WobbleDeg * Math.PI / 180 * (0.65 * Math.Sin(t * 4.1 + _wobPhase1) + 0.35 * Math.Sin(t * 10.3 + _wobPhase2));
        }

        private double WobPitch(int tick)
        {
            double t = tick / (double)SimTime.TicksPerSecond;
            return Config.WobbleDeg * 0.7 * (0.6 * Math.Sin(t * 3.3 + _wobPhase3) + 0.4 * Math.Sin(t * 8.7 + _wobPhase4));
        }

        private static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
