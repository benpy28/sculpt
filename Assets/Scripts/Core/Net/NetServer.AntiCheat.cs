using System;
using System.Collections.Generic;
using Sculpt.Core.AntiCheat;

namespace Sculpt.Core.Net
{
    /// <summary>What the server's checks remember about one player between ticks. Server only, never sent.</summary>
    internal sealed class PlayerWatch
    {
        // Look direction for the last few ticks (aim snap), by tick.
        public const int LookRing = 16;
        public readonly double[] Yaw = new double[LookRing], Pitch = new double[LookRing];
        public readonly int[] LookTick = new int[LookRing];

        // View lag (command tick - view tick) of recent real commands (backtracking).
        public int[] Lag = new int[64];
        public int LagCount, LagNext;
        private int[] _sort = new int[64];

        // View tick behind the packet's ack, for commands that were the newest in their packet, by tick.
        public const int AckRing = 256;
        public readonly int[] AckGap = new int[AckRing];
        public readonly int[] AckGapTick = new int[AckRing];

        // Integrity.
        public readonly int[] LastShot = new int[8];

        // Interest: how far this player can be heard right now, and until when the loudest recent sound counts.
        public double MoveNoise;
        public int MoveNoiseUntil = int.MinValue;
        public int ShotNoiseUntil = int.MinValue;
        public int JumpedTick = int.MinValue;   // moved more than a teleport's worth this tick (respawn, warp)
        public int LastShotTickAny = int.MinValue / 2;

        // Aim snap waiting for the next tick's look (did the turn stop dead?).
        public bool SnapPending;
        public int SnapTick, SnapVictim, SnapDeciDeg;

        public PlayerWatch()
        {
            for (int i = 0; i < LookRing; i++) LookTick[i] = int.MinValue;
            for (int i = 0; i < AckRing; i++) AckGapTick[i] = int.MinValue;
            for (int i = 0; i < LastShot.Length; i++) LastShot[i] = int.MinValue;
        }

        public void PushLook(int tick, double yaw, double pitch)
        {
            int i = tick & (LookRing - 1);
            Yaw[i] = yaw;
            Pitch[i] = pitch;
            LookTick[i] = tick;
        }

        public bool TryLook(int tick, out double yaw, out double pitch)
        {
            int i = tick & (LookRing - 1);
            yaw = Yaw[i];
            pitch = Pitch[i];
            return LookTick[i] == tick;
        }

        public void PushLag(int lag, int window)
        {
            if (Lag.Length != window) { Lag = new int[Math.Max(1, window)]; _sort = new int[Lag.Length]; LagCount = 0; LagNext = 0; }
            Lag[LagNext] = lag;
            LagNext = (LagNext + 1) % Lag.Length;
            if (LagCount < Lag.Length) LagCount++;
        }

        /// <summary>Median of the recent view lags (lower middle), or int.MinValue with too few to judge.</summary>
        public int MedianLag()
        {
            if (LagCount < 8) return int.MinValue;
            Array.Copy(Lag, _sort, LagCount);
            Array.Sort(_sort, 0, LagCount);
            return _sort[(LagCount - 1) / 2];
        }
    }

    /// <summary>
    /// Anti-cheat groundwork (2026-11-26): input checks, integrity checks on the server's own results, lag compensation
    /// checks and aim statistics into <see cref="Flags"/> (logged, never acted on), and interest management (who goes in
    /// whose snapshots). See <see cref="AntiCheatConfig"/> for what is and isn't checked and why.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Every suspicious event since the server started, with per-player counts.</summary>
        public readonly FlagLog Flags = new FlagLog();

        private AntiCheatConfig AC => Config.AntiCheat;

        /// <summary>Line-of-sight rays interest management cast (stats, tests).</summary>
        public long InterestRays { get; private set; }
        /// <summary>Pair line-of-sight tests run (stats, tests).</summary>
        public long InterestTests { get; private set; }

        private void Flag(ServerPlayer p, FlagKind kind, int value = 0, int other = 0)
        {
            if (!AC.Checks || p == null) return;
            Flags.RateEveryTicks = AC.FlagEveryTicks;
            Flags.MaxFlags = AC.MaxFlags;
            if (Flags.Add(Tick, p.Id, kind, value, other)) Stream?.AddFlag(Flags.Flags[Flags.Flags.Count - 1]);
        }

        // ================================================================ inputs

        /// <summary>
        /// Called on the command the tick will simulate, before it is recorded: refuses what this server doesn't allow
        /// (dev respawn, dev warp) and remembers the look direction.
        /// </summary>
        private void CheckCommand(ServerPlayer p, ref InputCmd cmd)
        {
            if (cmd.Respawn && !cmd.ForcedRespawn && !Config.AllowDevRespawn)
            {
                cmd.Respawn = false;
                if (!IsBotConnection(p.ConnectionId)) Flag(p, FlagKind.DevRespawnRefused);
            }
            if (cmd.Warp && !Config.AllowDevWarp)
            {
                cmd.Warp = false;
                if (!IsBotConnection(p.ConnectionId)) Flag(p, FlagKind.DevWarpRefused);
            }
            p.Watch.PushLook(Tick, cmd.Move.Yaw, cmd.Pitch);
            if (p.Watch.SnapPending) FinishSnap(p);
        }

        /// <summary>A packet's commands as they arrived: lead, sanitizing, and the view tick against the packet's ack.</summary>
        private void CheckPacket(ServerPlayer p, InputsMsg im, List<InputCmd> cmds, int sanitized)
        {
            if (sanitized > 0) Flag(p, FlagKind.BadView, 0);
            InputCmd newest = cmds[cmds.Count - 1];
            if (newest.Tick <= Tick) return; // only commands still in the future were just made by the client
            int gap = im.AckSnapshotTick - newest.ViewTick;
            int i = newest.Tick & (PlayerWatch.AckRing - 1);
            if (p.Watch.AckGapTick[i] != newest.Tick)
            {
                p.Watch.AckGap[i] = gap;
                p.Watch.AckGapTick[i] = newest.Tick;
            }
        }

        // ================================================================ integrity

        private struct Before
        {
            public Vec3d Position;
            public double Speed;
            public int MotorTick;
            public bool Dead;
        }

        private static Before TakeBefore(ServerPlayer p) => new Before
        {
            Position = p.State.Position,
            Speed = p.State.Velocity.Length,
            MotorTick = p.State.Tick,
            Dead = p.State.Dead,
        };

        /// <summary>After a player's simulation: the server's own result must be possible. Never fires on legit play.</summary>
        private void CheckIntegrity(ServerPlayer p, in Before b)
        {
            bool respawned = p.State.Tick <= b.MotorTick;
            if ((p.State.Position - b.Position).LengthSquared > 9.0) p.Watch.JumpedTick = Tick;
            if (respawned)
            {
                for (int i = 0; i < p.Watch.LastShot.Length; i++) p.Watch.LastShot[i] = int.MinValue;
                return;
            }
            if (!AC.Checks) return;
            if (!b.Dead && !p.State.Dead)
            {
                double speed = p.State.Velocity.Length;
                if (speed > AC.MaxSpeed) Flag(p, FlagKind.SpeedOverCap, (int)Math.Min(int.MaxValue, speed * 100));
                double moved = (p.State.Position - b.Position).Length;
                double allowed = Math.Max(speed, b.Speed) / SimTime.TicksPerSecond + AC.MoveSlack;
                if (moved > allowed) Flag(p, FlagKind.Teleport, (int)Math.Min(int.MaxValue, moved * 100));
            }
            WeaponsConfig wc = Weapons;
            foreach (WeaponShot s in p.Shots)
            {
                int g = (int)s.Weapon;
                if (g < 0 || g >= p.Watch.LastShot.Length) continue;
                int last = p.Watch.LastShot[g];
                int interval = (int)(wc.Of(s.Weapon).FireIntervalMilliTicks / 1000);
                if (last != int.MinValue && s.Tick - last < interval) Flag(p, FlagKind.FireTooFast, s.Tick - last);
                p.Watch.LastShot[g] = s.Tick;
            }
            foreach (Weapon w in Guns)
            {
                int used = p.State.Weapon.UsedOf(w);
                if (used > wc.Of(w).MagSize) Flag(p, FlagKind.MagOverflow, used);
            }
        }

        private static readonly Weapon[] Guns = { Weapon.Pump, Weapon.Smg, Weapon.Ar, Weapon.Pistol };

        // ================================================================ shots

        /// <summary>A shot was fired (before it is resolved): counts, and the shooter is heard.</summary>
        private void NoteShot(ServerPlayer p, in WeaponShot shot)
        {
            Flags.StatsOf(p.Id).Shots++;
            p.Watch.ShotNoiseUntil = Tick + AC.Interest.ShotNoiseTicks;
        }

        /// <summary>
        /// A shot hit a player. Checks the lag compensation it used (view tick against the ack and the player's usual
        /// view lag), whether the victim was being sent to the shooter, and the aim statistics.
        /// </summary>
        private void NoteHit(ServerPlayer p, ServerPlayer victim, in WeaponShot shot, bool head, bool firstOfShot, int lastShotBefore)
        {
            if (!firstOfShot) return; // one shot, several victims: the shooter-side checks once
            PlayerFlagStats st = Flags.StatsOf(p.Id);
            st.Hits++;
            if (head) st.HeadHits++;
            if (!AC.Checks) return;
            InputCmd cmd = p.LastCmd;
            bool bot = IsBotConnection(p.ConnectionId);

            // Lag compensation: the view the hit was checked against.
            if (!bot && p.LastCmdWasReal)
            {
                int i = cmd.Tick & (PlayerWatch.AckRing - 1);
                if (p.Watch.AckGapTick[i] == cmd.Tick)
                {
                    int over = p.Watch.AckGap[i] - (int)Math.Ceiling(Config.InterpDelayTicks) - AC.ViewBehindAckTicks;
                    if (over > 0) Flag(p, FlagKind.ViewBehindAck, p.Watch.AckGap[i], victim.Id);
                }
                int median = p.Watch.MedianLag();
                int lag = cmd.Tick - cmd.ViewTick;
                if (median != int.MinValue && lag - median > AC.ViewLagJumpTicks) Flag(p, FlagKind.ViewLagJump, lag - median, victim.Id);
            }

            // Interest: the shooter must have been sent the victim around the view the hit was checked against.
            if (AC.Interest.Enabled && TryPair(p.Id, victim.Id, out InterestPair pair))
            {
                int sent = pair.A == p.Id ? pair.SentToA : pair.SentToB;
                double view = Math.Max(Tick - Weapons.MaxRewindTicks, Math.Min(Tick, cmd.ViewTick + cmd.ViewFrac / 256.0));
                if (sent < (int)Math.Floor(view) - 1) Flag(p, FlagKind.HitUnseen, sent == int.MinValue ? -1 : (int)Math.Floor(view) - sent, victim.Id);
            }
            else if (AC.Interest.Enabled) Flag(p, FlagKind.HitUnseen, -1, victim.Id);

            // Aim snap: a big turn into the shot; it's judged next tick (did it stop dead?).
            if (p.Watch.TryLook(Tick, out double y1, out double p1) && p.Watch.TryLook(Tick - AC.SnapTicks, out double y0, out double p0))
            {
                double turn = AngleDeg(y0, p0, y1, p1);
                if (turn >= AC.SnapDegrees)
                {
                    p.Watch.SnapPending = true;
                    p.Watch.SnapTick = Tick;
                    p.Watch.SnapVictim = victim.Id;
                    p.Watch.SnapDeciDeg = (int)Math.Round(turn * 10);
                }
            }

            // Reaction: a burst's first shot that hits someone who only just came into sight.
            if (Tick - lastShotBefore >= AC.FirstShotGapTicks) CheckReaction(p, victim, cmd);
        }

        private void FinishSnap(ServerPlayer p)
        {
            PlayerWatch w = p.Watch;
            w.SnapPending = false;
            if (Tick != w.SnapTick + 1) return;
            if (!w.TryLook(w.SnapTick, out double y0, out double p0) || !w.TryLook(Tick, out double y1, out double p1)) return;
            if (AngleDeg(y0, p0, y1, p1) <= AC.SnapStopDegrees) Flag(p, FlagKind.AimSnap, w.SnapDeciDeg, w.SnapVictim);
        }

        private static double AngleDeg(double yawA, double pitchA, double yawB, double pitchB)
        {
            Vec3d a = WeaponRules.AimDir(yawA, pitchA, 0, 0), b = WeaponRules.AimDir(yawB, pitchB, 0, 0);
            double d = Math.Max(-1, Math.Min(1, a.Dot(b)));
            return Math.Acos(d) * 180 / Math.PI;
        }

        /// <summary>
        /// How long the victim had been in the shooter's sight (shooter's eye then, victim as the shooter saw them then):
        /// under the reaction threshold after being hidden a while is flagged.
        /// </summary>
        private void CheckReaction(ServerPlayer p, ServerPlayer victim, in InputCmd cmd)
        {
            int lag = Math.Max(0, Math.Min(Weapons.MaxRewindTicks, cmd.Tick - cmd.ViewTick));
            int fast = AC.FastReactionTicks, hidden = AC.FastReactionHiddenTicks;
            int seenFor = -1;
            for (int k = 1; k <= fast; k++)
            {
                if (!SeenAt(p, victim, Tick - k, Tick - k - lag, out bool known)) { seenFor = k - 1; break; }
                if (!known) return;
            }
            if (seenFor < 0) return; // in sight for longer than a fast reaction: normal
            int from = Tick - seenFor - 1;
            for (int k = 0; k < hidden; k++)
            {
                if (SeenAt(p, victim, from - k, from - k - lag, out bool known) || !known) return; // a re-peek, or history ran out
            }
            Flag(p, FlagKind.FastReaction, (int)Math.Round((seenFor + 1) * 1000.0 / SimTime.TicksPerSecond), victim.Id);
        }

        private bool SeenAt(ServerPlayer p, ServerPlayer victim, int shooterTick, int victimTick, out bool known)
        {
            known = false;
            if (!HistoryAt(p, shooterTick, out Vec3d feet, out double height, out bool dead) || dead) return false;
            if (!HistoryAt(victim, victimTick, out Vec3d vf, out double vh, out bool vdead) || vdead) return false;
            known = true;
            double eye = height >= Movement.StandHeight - 1e-6 ? Movement.StandEye : Movement.CrouchEye;
            return DuelBot.CanSee(Grid, feet + new Vec3d(0, eye, 0), vf, vh);
        }

        private bool HistoryAt(ServerPlayer p, int tick, out Vec3d feet, out double height, out bool dead)
        {
            int n = p.PosHistory.Length;
            int h = ((tick % n) + n) % n;
            feet = p.PosHistory[h];
            height = p.HeightHistory[h];
            dead = p.DeadHistory[h];
            return p.PosHistoryTick[h] == tick;
        }

        // ================================================================ interest management

        private sealed class InterestPair
        {
            public int A, B;                         // ids, A < B
            public int LastSeen = int.MinValue;      // last test that found a line between them
            public int LastTest = int.MinValue;
            public int SentToA = int.MinValue;       // last tick B was relevant to A
            public int SentToB = int.MinValue;
        }

        private readonly Dictionary<long, InterestPair> _interest = new Dictionary<long, InterestPair>();
        private bool[] _sendMask = new bool[64];
        private readonly Vec3d[] _ptsA = new Vec3d[7], _ptsB = new Vec3d[7];

        private bool TryPair(int a, int b, out InterestPair pair) => _interest.TryGetValue(PairKey(a, b), out pair);

        private InterestPair InterestOf(ServerPlayer a, ServerPlayer b)
        {
            long k = PairKey(a.Id, b.Id);
            if (!_interest.TryGetValue(k, out InterestPair p))
            {
                p = new InterestPair { A = Math.Min(a.Id, b.Id), B = Math.Max(a.Id, b.Id) };
                _interest[k] = p;
            }
            return p;
        }

        /// <summary>Forgets the pairs of a player who left.</summary>
        private void ForgetInterest(int id)
        {
            _forget.Clear();
            foreach (var kv in _interest) if (kv.Value.A == id || kv.Value.B == id) _forget.Add(kv.Key);
            foreach (long k in _forget) _interest.Remove(k);
        }

        private readonly List<long> _forget = new List<long>();

        /// <summary>How far away this player can be heard right now (movement, recent shots), metres.</summary>
        private double NoiseOf(ServerPlayer p)
        {
            InterestConfig ic = AC.Interest;
            if (Tick <= p.Watch.ShotNoiseUntil) return ic.ShotNoiseRange;
            double r = 0;
            if (!p.State.Dead)
            {
                Audio.MoveSoundConfig sc = ic.Sounds;
                MotorState s = p.State;
                double sp = s.Velocity.HorizontalLength;
                switch (s.Mode)
                {
                    case MoveMode.Slide: r = sc.SlideRange; break;
                    case MoveMode.Vault:
                    case MoveMode.Mantle: r = sc.GrabRange; break;
                    case MoveMode.Air:
                    {
                        double fall = -s.Velocity.Y;
                        double k = Audio.FootstepClock.Clamp01((fall - sc.HopFallSpeed) / Math.Max(1e-6, sc.BigFallSpeed - sc.HopFallSpeed));
                        r = Math.Max(sc.JumpRange, sc.LandRangeMin + (sc.LandRangeMax - sc.LandRangeMin) * k);
                        break;
                    }
                    default:
                        if (sp > sc.MinStepSpeed)
                        {
                            double k = Audio.FootstepClock.Clamp01((sp - sc.JogSpeed) / Math.Max(1e-6, sc.SprintSpeed - sc.JogSpeed));
                            r = s.Crouched ? sc.CrouchRange : sc.JogRange + (sc.SprintRange - sc.JogRange) * k;
                        }
                        break;
                }
                r *= ic.NoiseSlack;
            }
            if (r >= p.Watch.MoveNoise || Tick > p.Watch.MoveNoiseUntil)
            {
                p.Watch.MoveNoise = r;
                p.Watch.MoveNoiseUntil = Tick + ic.MoveNoiseTicks;
            }
            return p.Watch.MoveNoise;
        }

        /// <summary>
        /// Once a tick, after everything has moved and fought: test the pairs that are due for a line of sight, then
        /// decide for every receiver which enemies it gets this tick. Reads the simulation only (bots included, so a
        /// re-run of a replay decides the same).
        /// </summary>
        private void StepInterest()
        {
            InterestConfig ic = AC.Interest;
            if (!ic.Enabled) return;
            UpdateSightGrid();
            int n = _players.Count;
            if (_noise.Length < n) { _noise = new double[Math.Max(n, _noise.Length * 2)]; }
            for (int i = 0; i < n; i++) _noise[i] = NoiseOf(_players[i]);
            int every = Math.Max(1, ic.EveryTicks);
            for (int i = 0; i < n; i++)
            {
                ServerPlayer a = _players[i];
                for (int j = i + 1; j < n; j++)
                {
                    ServerPlayer b = _players[j];
                    InterestPair pair = InterestOf(a, b);
                    bool friends = a.Team == b.Team && !Weapons.FriendlyFire;
                    if (friends) { pair.SentToA = pair.SentToB = Tick; continue; }
                    bool toA = Cheap(a, b, _noise[j]), toB = Cheap(b, a, _noise[i]);
                    bool due = pair.LastTest == int.MinValue || (Tick + pair.A * 7 + pair.B * 13) % every == 0
                        || a.Watch.JumpedTick == Tick || b.Watch.JumpedTick == Tick;
                    if (due)
                    {
                        pair.LastTest = Tick;
                        // Both already sent for another reason (heard, close, pinged): no rays needed, and the hold
                        // carries them for a moment after that reason ends, until the next test.
                        if ((toA && toB) || InSight(a, b)) pair.LastSeen = Tick;
                    }
                    bool held = pair.LastSeen != int.MinValue && Tick - pair.LastSeen <= ic.HoldTicks;
                    if (toA || held) pair.SentToA = Tick;
                    if (toB || held) pair.SentToB = Tick;
                }
            }
        }

        private double[] _noise = new double[16];

        /// <summary>Whether <paramref name="o"/> goes to <paramref name="to"/> for a reason other than sight: dead, very close, heard, or pinged.</summary>
        private bool Cheap(ServerPlayer to, ServerPlayer o, double noiseOfO)
        {
            InterestConfig ic = AC.Interest;
            if (o.State.Dead) return true; // a dead player shows nothing
            double d2 = (o.State.Position - to.State.Position).LengthSquared;
            if (d2 <= ic.AlwaysRange * ic.AlwaysRange) return true;
            if (noiseOfO > 0 && d2 <= noiseOfO * noiseOfO) return true;
            return IsRevealed(to.Id, o.Id);
        }

        /// <summary>
        /// Could these two see each other soon? Rays between a few points on each: eyes, body and knees now, where each will be
        /// after the look-ahead, a peek reach either side of that, and a jump above. Symmetric (a ray is a ray).
        /// </summary>
        private bool InSight(ServerPlayer a, ServerPlayer b)
        {
            InterestConfig ic = AC.Interest;
            Vec3d d = b.State.Position - a.State.Position;
            if (d.LengthSquared > ic.MaxSightRange * ic.MaxSightRange) return false;
            InterestTests++;
            int na = Points(a, d, _ptsA), nb = Points(b, -d, _ptsB);
            for (int i = 0; i < na; i++)
                for (int j = 0; j < nb; j++)
                {
                    InterestRays++;
                    if (Clear(_ptsA[i], _ptsB[j])) return true;
                }
            return false;
        }

        private int Points(ServerPlayer p, Vec3d towardOther, Vec3d[] into)
        {
            InterestConfig ic = AC.Interest;
            MotorState s = p.State;
            double eyeH = PlayerMovement.EyeOf(s, Movement), h = PlayerMovement.HeightOf(s, Movement);
            Vec3d feet = s.Position;
            Vec3d ahead = new Vec3d(s.Velocity.X, 0, s.Velocity.Z) * ic.LookaheadSeconds;
            Vec3d flat = new Vec3d(towardOther.X, 0, towardOther.Z);
            double fl = flat.Length;
            Vec3d side = fl > 1e-6 ? new Vec3d(-flat.Z / fl, 0, flat.X / fl) * ic.PeekReach : new Vec3d(ic.PeekReach, 0, 0);
            Vec3d eye = feet + new Vec3d(0, eyeH, 0);
            Vec3d chest = feet + new Vec3d(0, h * 0.6, 0);
            int n = 0;
            into[n++] = eye;
            into[n++] = chest;
            into[n++] = eye + ahead;
            into[n++] = eye + ahead + side;
            into[n++] = eye + ahead - side;
            into[n++] = eye + new Vec3d(0, ic.JumpReach, 0);
            // Knees: legs seen (and shot) through a hole near the floor or under a beam must count, or the server would
            // hide a player a bullet can still hit there.
            into[n++] = feet + new Vec3d(0, Math.Min(ic.KneeHeight, h * 0.5), 0);
            return n;
        }

        private Audio.SoundGrid _sight;
        private bool _sightTried;

        /// <summary>
        /// The sight tests walk an air grid (the same one sound is routed on: 0.5 m cells read straight from the shapes)
        /// instead of casting rays: about fifty times cheaper, and it only ever errs toward "can see". Built on first use,
        /// and redone where a tile changed this tick.
        /// </summary>
        private void UpdateSightGrid()
        {
            if (!_sightTried)
            {
                _sightTried = true;
                var g = new Audio.SoundGrid(Grid);
                if (g.Supported) { g.Build(); _sight = g; }
            }
            if (_sight == null || Walls == null) return;
            foreach (TileChange c in Walls.Changes) _sight.Rebuild(Walls.BoundsOf(c.Tile));
        }

        private bool Clear(Vec3d from, Vec3d to)
        {
            if (_sight != null) return _sight.LineOpen(from, to);
            Vec3d d = to - from;
            double len = d.Length;
            if (len < 1e-3) return true;
            TerrainHit hit = Grid.Raycast(from, d / len, len);
            return !hit.Hit || hit.Distance >= len - 0.05;
        }

        /// <summary>True when <paramref name="target"/> was in <paramref name="viewer"/>'s snapshot this tick (or interest management is off).</summary>
        public bool IsSentTo(int viewerId, int targetId)
        {
            if (SentDirect(viewerId, targetId)) return true;
            ServerPlayer v = Br != null ? FindById(viewerId) : null;
            return v != null && WatchesTeammates(v, _watchMates) && SentToAny(_watchMates, targetId);
        }

        private bool SentDirect(int viewerId, int targetId)
        {
            if (!AC.Interest.Enabled || viewerId == targetId) return true;
            if (!TryPair(viewerId, targetId, out InterestPair p)) return false;
            return (p.A == viewerId ? p.SentToA : p.SentToB) == Tick;
        }

        private readonly List<ServerPlayer> _watchMates = new List<ServerPlayer>(4);

        /// <summary>
        /// Battle royale: a player who is out watches their living teammates (BrDeathCamera), so they get what those
        /// teammates get, not just what can be seen from where they went down. Fills <paramref name="mates"/>; false
        /// when <paramref name="v"/> is alive, not in the match, or has nobody left to watch.
        /// </summary>
        private bool WatchesTeammates(ServerPlayer v, List<ServerPlayer> mates)
        {
            mates.Clear();
            if (Br == null || !v.State.Dead || !_brIn.Contains(v.Id)) return false;
            foreach (ServerPlayer o in _players)
                if (o != v && o.Team == v.Team && !o.State.Dead && _brIn.Contains(o.Id)) mates.Add(o);
            return mates.Count > 0;
        }

        private bool SentToAny(List<ServerPlayer> mates, int targetId)
        {
            foreach (ServerPlayer m in mates) if (m.Id == targetId || SentDirect(m.Id, targetId)) return true;
            return false;
        }

        /// <summary>The snapshot mask for one receiver: by player id, who it gets this tick. Null = everyone.</summary>
        private bool[] MaskFor(ServerPlayer to)
        {
            if (!AC.Interest.Enabled) return null;
            int max = 0;
            foreach (ServerPlayer o in _players) if (o.Id > max) max = o.Id;
            if (_sendMask.Length <= max) _sendMask = new bool[Math.Max(max + 1, _sendMask.Length * 2)];
            // Only current players' entries are read (and every one is set below), so no clearing of the whole array
            // (ids go up to 65535).
            _sendMask[to.Id] = false;
            bool watching = WatchesTeammates(to, _watchMates);
            foreach (ServerPlayer o in _players)
            {
                if (o == to) continue;
                _sendMask[o.Id] = SentDirect(to.Id, o.Id) || (watching && SentToAny(_watchMates, o.Id));
            }
            return _sendMask;
        }
    }
}
