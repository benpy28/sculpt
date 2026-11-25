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
    public sealed class DuelBot : IBotBrain
    {
        private struct Obs
        {
            public int Tick;
            public bool Seen, Heard;
            public Vec3d Pos, Vel, HeardPos;
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
        private int _lastDamage;

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
        public IReadOnlyList<int> Path => _path;

        public DuelBot(BotNav nav, DuelBotConfig config = null, int seed = 1)
        {
            Nav = nav ?? throw new ArgumentNullException(nameof(nav));
            Config = config ?? new DuelBotConfig();
            _rng = new Random(seed);
            _wobPhase1 = _rng.NextDouble() * 6.283;
            _wobPhase2 = _rng.NextDouble() * 6.283;
            _wobPhase3 = _rng.NextDouble() * 6.283;
            _wobPhase4 = _rng.NextDouble() * 6.283;
        }

        public bool CountsAsPlayer => true;

        public InputCmd Next(NetServer server, ServerPlayer me)
        {
            int tick = server.Tick;
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
                _lastSeenTick = _lastHeardTick = int.MinValue / 2;
                _stuckFrom = s.Position;
                _stuckCheck = tick;
            }
            _lastPos = s.Position;

            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            ServerPlayer enemy = FindEnemy(server, me);
            Store(tick, Observe(server, me, enemy, eye));

            // ---- What it knows now: what it perceived a reaction time ago.
            Obs p = Recall(tick - Config.ReactionTicks);
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
            }
            else _engaged = false;
            if (p.Heard) { _lastHeardPos = p.HeardPos; _lastHeardTick = p.Tick; }

            bool recentlySeen = tick - _lastSeenTick < 90 * SimTime.Per60;
            double dist = recentlySeen ? (_lastSeenPos - s.Position).Length : 30;

            // ---- Aim.
            double wantYaw = _yaw, wantPitch = 0;
            double turnCap = Config.TurnDegPerSec / SimTime.TicksPerSecond * Math.PI / 180;
            double gain = Config.AimGain;
            Vec3d aimAt = default;
            bool aimEnemy = false;
            if (_engaged)
            {
                double ahead = Config.ReactionTicks / (double)SimTime.TicksPerSecond * Config.Lead;
                double h = p.Height;
                double up = _aimHead ? h - 0.18 : h * 0.62 + _aimHeightOffset;
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
                    wantYaw += rate / SimTime.TicksPerSecond * (1 - gain) / Math.Max(0.05, gain) * Config.Lead;
                }
                wantPitch = -Math.Asin(Math.Max(-1, Math.Min(1, d.Y / Math.Max(1e-6, d.Length)))) * 180 / Math.PI;
            }

            // ---- Move (and, when not fighting, look where it walks).
            Vec3d wish = Vec3d.Zero;
            bool sprint = false, jump = false, crouch = false, punch = false;
            if (_engaged || (recentlySeen && tick - _lastSeenTick < 36 * SimTime.Per60))
                wish = FightMove(server, s, tick, dist, ref jump);
            else
            {
                Vec3d goal;
                if (recentlySeen || tick - _lastSeenTick < 600 * SimTime.Per60) goal = _lastSeenPos;
                else if (tick - _lastHeardTick < 600 * SimTime.Per60) goal = _lastHeardPos;
                else goal = RoamGoal(s.Position);
                wish = FollowPath(s, goal, tick, ref jump, out Vec3d lookAt, out bool arrived);
                if (arrived)
                {
                    // Nothing here: forget it and look elsewhere.
                    if (goal.Equals(_lastSeenPos)) _lastSeenTick = int.MinValue / 2;
                    else if (goal.Equals(_lastHeardPos)) _lastHeardTick = int.MinValue / 2;
                    else _hasRoam = false;
                }
                sprint = wish.LengthSquared > 0.01;
                if (!aimEnemy && wish.LengthSquared > 0.01)
                {
                    Vec3d d = lookAt - eye;
                    wantYaw = Math.Atan2(d.X, d.Z);
                    wantPitch = 0;
                    gain = 0.12;
                }
                // A shut door on the way: face it and punch it open (one punch breaks a leaf).
                if (wish.LengthSquared > 0.01 && DoorAhead(server.Grid, s, wish, mc, out Vec3d door))
                {
                    Vec3d d = door - eye;
                    wantYaw = Math.Atan2(d.X, d.Z);
                    wantPitch = Math.Atan2(d.Y, d.WithY(0).Length) * 180 / Math.PI;
                    gain = 0.25;
                    if (Math.Abs(Wrap(wantYaw - _yaw)) < 0.2 && tick - _punchTick > 30 * SimTime.Per60) { punch = true; _punchTick = tick; }
                }
                Unstick(s, tick, wish, ref jump);
            }
            if (tick < _jumpUntil) jump = true;

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
            bool fire = false;
            if (_engaged && tick >= _fireOkTick && s.Hand.HasGun && !s.Hand.InSculptMode)
            {
                Weapon held = s.Hand.Held;
                double range = held == Weapon.Pump ? 16 : held == Weapon.Smg ? 35 : 90;
                // Fires when the crosshair is on where it believes the target is now (seen late, moved on by its velocity).
                double ahead = Config.ReactionTicks / (double)SimTime.TicksPerSecond * Config.Lead;
                Vec3d chest = p.Pos + p.Vel.WithY(0) * ahead + new Vec3d(0, p.Height * 0.6, 0);
                double dTrue = (chest - eye).Length;
                Vec3d look = WeaponRules.AimDir(_yaw + WobYaw(tick), _pitch + WobPitch(tick), 0, 0);
                double err = Math.Acos(Clamp(look.Dot((chest - eye) / Math.Max(1e-6, dTrue)), -1, 1)) * 180 / Math.PI;
                double tol = Math.Atan2(0.4, Math.Max(0.5, dTrue)) * 180 / Math.PI + 0.5 + (held == Weapon.Pump ? 2.0 : 0);
                bool burstPause = held == Weapon.Ar && dTrue > 20 && ((tick - _fireOkTick) % (30 * SimTime.Per60)) >= 15 * SimTime.Per60;
                fire = err < tol && dTrue < range && !burstPause && s.Weapon.UsedOf(held) < wc.Stats[(int)held].MagSize;
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
            if (s.Damage > _lastDamage && enemy != null)
                o = Heard(o, enemy.State.Position, 2.0); // hit: it knows roughly where from
            _lastDamage = s.Damage;
            if (enemy == null) return o;
            MotorState e = enemy.State;
            double h = PlayerMovement.HeightOf(e, server.Movement);
            o.Height = h;
            Vec3d to = e.Position - s.Position;
            double flat = to.HorizontalLength;
            double off = Math.Abs(Wrap(Math.Atan2(to.X, to.Z) - _yaw)) * 180 / Math.PI;
            bool inCone = off <= Config.ViewConeDeg * 0.5 || flat < 2.5;
            if (inCone && CanSee(server.Grid, eye, e.Position, h))
            {
                o.Seen = true;
                o.Pos = e.Position;
                o.Vel = e.Velocity;
            }
            if (!o.Seen)
            {
                double d = to.Length;
                if (enemy.Shots.Count > 0 && d < Config.HearShots) o = Heard(o, e.Position, 1.5 + d * 0.04);
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
                    case Weapon.Pump: if (dist > 10) want = dist > 24 ? Weapon.Ar : Weapon.Smg; break;
                    case Weapon.Smg: if (dist < 7) want = Weapon.Pump; else if (dist > 26) want = Weapon.Ar; break;
                    case Weapon.Ar: if (dist < 18) want = dist < 7 ? Weapon.Pump : Weapon.Smg; break;
                    default: want = Weapon.Smg; break;
                }
            }
            else if (cur == Weapon.Pistol) want = Weapon.Smg;
            // Empty in a fight: swap instead of reloading (pump <-> SMG).
            if (_engaged && s.Hand.HasGun && s.Weapon.UsedOf(s.Hand.Held) >= Stats(s.Hand.Held).MagSize)
                want = s.Hand.Held == Weapon.Pump ? Weapon.Smg : Weapon.Pump;
            if (want != cur && tick - _switchTick >= 60 * SimTime.Per60) { WantGun = want; _switchTick = tick; }

            int slot = s.Hand.Bag.SlotOf(Items.Of(WantGun));
            bool holding = s.Hand.HasGun && s.Hand.Held == WantGun;
            if (!holding && slot >= 0 && tick % (20 * SimTime.Per60) == 0) cmd.Hand.WeaponKey = (byte)(slot + 1);
            else if (holding && !_engaged && !s.Weapon.Reloading && tick - _reloadTick > 60 * SimTime.Per60
                     && s.Weapon.UsedOf(WantGun) * 10 >= Stats(WantGun).MagSize * 4)
            {
                cmd.Hand.Reload = true;
                _reloadTick = tick;
            }
        }

        private WeaponsConfig _wc = new WeaponsConfig();
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

        /// <summary>An unbroken door leaf right in front along <paramref name="wish"/> (its middle, to punch at).</summary>
        private bool DoorAhead(ITerrain t, in MotorState s, Vec3d wish, MovementConfig mc, out Vec3d at)
        {
            at = default;
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
                if (tile.Kind != TileKind.Door || tile.State == TileState.Broken) continue;
                Aabb b = walls.BoundsOf(i);
                at = new Vec3d((b.Min.X + b.Max.X) / 2, s.Position.Y + PlayerMovement.EyeOf(s, mc) - 0.1, (b.Min.Z + b.Max.Z) / 2);
                return true;
            }
            return false;
        }

        private List<int> _doorNear;
        private int _punchTick = int.MinValue / 2;

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
            if (replan && tick - _pathTick >= 15 * SimTime.Per60)
            {
                _pathTick = tick;
                _pathGoal = goal;
                _pathAt = 0;
                int a = Nav.Nearest(feet), b = Nav.Nearest(goal, 6);
                if (!Nav.FindPath(a, b, _path))
                {
                    _path.Clear();
                    _hasRoam = false; // unreachable: pick something else
                    if (goal.Equals(_lastSeenPos)) _lastSeenTick = int.MinValue / 2;
                    if (goal.Equals(_lastHeardPos)) _lastHeardTick = int.MinValue / 2;
                }
            }
            if (_path.Count == 0 || _pathAt >= _path.Count) { if (_path.Count > 0) arrived = true; return Vec3d.Zero; }

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
            if (_pathAt == _path.Count - 1 && d.Length < 0.4) { arrived = true; return Vec3d.Zero; }
            return d.Length > 1e-6 ? d.Normalized : Vec3d.Zero;
        }

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
            if (_stuckCount >= 3) { _hasRoam = false; _lastSeenTick = _lastHeardTick = int.MinValue / 2; _stuckCount = 0; }
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
