using System;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Battle royale tactics for the duel bot (2026-11-28, Ben: bots should play for placement like people: rotate with
    /// the circle, take cover, pick fights they can win and leave bad ones, third-party sometimes, heal; never hide for
    /// good or camp, and a match shouldn't turn into a slaughter). Hand-written, deterministic, built only from what a
    /// person could know: what it sees, gunfire it hears, its own hit markers (the attacker is told its victim's health),
    /// the damage direction when it is hit, and the circle on the map. None of this runs outside a battle royale.
    /// </summary>
    public sealed partial class DuelBot
    {
        // ---- Personality: one number per bot, so a lobby has careful and pushy players.
        /// <summary>0 careful .. 1 pushy (seeded; a separate random stream, so the 1v1 bot plays exactly as before).</summary>
        public double Aggression { get; internal set; }
        private readonly Random _tac;

        // ---- Gunfire it heard (any enemy, up to BrHearFightMetres): a small ring, newest overwrites oldest.
        private struct Noise { public int Id, Tick; public Vec3d Pos; }
        private readonly Noise[] _noise = new Noise[16];
        private int _noiseNext;

        // ---- What it is doing.
        public enum Tactic : byte { None, Cover, Rotate, ThirdParty }
        /// <summary>For tests and the dev HUD: what the battle royale tactics have it doing now.</summary>
        public Tactic Doing { get; private set; }
        /// <summary>For tests and the BrPerf tool: how often it took cover, started a rotation and went to a fight it heard.</summary>
        public int CoverCount, RotateCount, ThirdPartyCount;

        private Vec3d _coverSpot, _coverThreat;
        private int _coverUntil, _coverReadyTick = int.MinValue / 2, _coverSearchTick = int.MinValue / 2;
        private bool _coverHealing;
        private int _rotPhase = -2;
        private double _rotDepth;
        private Vec3d _rotGoal;
        private bool _rotUrgent;
        private int _rotCheckTick = int.MinValue / 2;
        private bool _rotating;
        private Vec3d _tpGoal;
        private int _tpUntil, _tpKey = int.MinValue, _tpRollTick = int.MinValue / 2;
        private bool _tpGo;
        private int _secondSeenTick = int.MinValue / 2, _secondCheckTick;
        private int _targetId;

        /// <summary>
        /// Shortens the fights it starts just after the calm opening (half range, growing to full over
        /// BrSettleSeconds; 40% at first since 2026-11-28): with the whole lobby still spread over the loot, every bot starting fights at once made the
        /// minute after the opening the bloodiest of the match.
        /// </summary>
        private double _engageScale = 1;

        private void SettleRange(NetServer server, int tick)
        {
            double since = (tick - server.Match.PhaseStartTick) / (double)SimTime.TicksPerSecond - Config.BrCalmSeconds;
            _engageScale = since >= Config.BrSettleSeconds ? 1 : 0.4 + 0.6 * Math.Max(0, since) / Config.BrSettleSeconds;
        }

        /// <summary>Health share left (1 full).</summary>
        private static double Hp(in MotorState s, MovementConfig mc) => s.Health(mc.Weapons) / (double)mc.Weapons.MaxHealth;

        private static bool HasHeals(in Bag b)
        {
            for (int k = 0; k < Items.Slots; k++) if (Items.IsHeal(b[k]) && b.CountIn(k) > 0) return true;
            return false;
        }

        private static bool HasLongGun(in Bag b) => b.SlotOf(Item.Ar) >= 0 || b.SlotOf(Item.Pistol) >= 0;

        // ================================================================ Hearing fights

        /// <summary>Every enemy that fired this tick within earshot goes in the ring (one entry per player per half second).</summary>
        private void HearGunfire(NetServer server, ServerPlayer me, int tick)
        {
            double far = Config.BrHearFightMetres * Config.BrHearFightMetres;
            foreach (ServerPlayer o in server.Players)
            {
                if (o == me || o.Team == me.Team || o.State.Dead || tick - o.LastShotTick > 1) continue;
                if ((o.State.Position - me.State.Position).LengthSquared > far) continue;
                NoteGunfire(o.Id, o.State.Position, tick);
            }
        }

        /// <summary>Remembers a shot it heard (tests feed it directly).</summary>
        internal void NoteGunfire(int id, Vec3d at, int tick)
        {
            int slot = -1;
            for (int i = 0; i < _noise.Length; i++)
                if (_noise[i].Id == id && _noise[i].Tick > 0) { slot = i; break; }
            if (slot >= 0 && tick - _noise[slot].Tick < 60 * SimTime.Per60) return;
            if (slot < 0) { slot = _noiseNext; _noiseNext = (_noiseNext + 1) % _noise.Length; }
            _noise[slot] = new Noise { Id = id, Tick = tick, Pos = at };
        }

        /// <summary>Whether this player fired recently (heard, a reaction time ago or earlier).</summary>
        private bool HeardFiring(int id, int tick, int withinTicks)
        {
            for (int i = 0; i < _noise.Length; i++)
            {
                Noise n = _noise[i];
                if (n.Id == id && n.Tick > 0 && tick - n.Tick >= Config.ReactionTicks && tick - n.Tick < withinTicks) return true;
            }
            return false;
        }

        /// <summary>
        /// A fight it isn't part of: two different enemies firing within 40 m of each other in the last 4 s (heard a reaction
        /// time late). Returns the nearest such fight's middle.
        /// </summary>
        internal bool HeardFight(NetServer server, Vec3d from, int tick, out Vec3d mid)
        {
            mid = default;
            double best = double.MaxValue;
            int window = 4 * SimTime.TicksPerSecond;
            for (int i = 0; i < _noise.Length; i++)
            {
                Noise a = _noise[i];
                if (a.Tick <= 0 || tick - a.Tick < Config.ReactionTicks || tick - a.Tick > window) continue;
                for (int j = i + 1; j < _noise.Length; j++)
                {
                    Noise b = _noise[j];
                    if (b.Tick <= 0 || b.Id == a.Id || tick - b.Tick < Config.ReactionTicks || tick - b.Tick > window) continue;
                    if ((a.Pos - b.Pos).HorizontalLength > 40) continue;
                    Vec3d m = (a.Pos + b.Pos) * 0.5;
                    double d = (m - from).HorizontalLength;
                    if (d < 15 || d > Config.BrHearFightMetres || d >= best || server.InStorm(m)) continue;
                    best = d;
                    mid = m;
                }
            }
            return best < double.MaxValue;
        }

        // ================================================================ Judging a fight

        /// <summary>
        /// How much health it thinks the enemy has left (1 full): its own hit marker says exactly, if it hit them lately;
        /// someone it heard shooting at somebody else is probably hurt.
        /// </summary>
        private double EnemyHpGuess(NetServer server, ServerPlayer me, int enemyId, int tick)
        {
            if (enemyId != 0 && me.HitVictim == enemyId && tick - me.HitTick < 15 * SimTime.TicksPerSecond)
                return me.HitVictimHealth / (double)server.Movement.Weapons.MaxHealth;
            if (enemyId != 0 && tick - _hurtTick > 3 * SimTime.TicksPerSecond && HeardFiring(enemyId, tick, 6 * SimTime.TicksPerSecond)) return 0.65;
            return 1;
        }

        /// <summary>The farthest it starts a fight (unless it was hurt): its temperament, its guns, and how pressed it is.</summary>
        private double EngageRange(in MotorState s)
        {
            double r = Config.BrEngageMetres * (0.6 + 0.7 * Aggression) * _engageScale; // about 23..45 m once settled
            if (!HasLongGun(s.Hand.Bag)) r = Math.Min(r, 22); // pump and SMG only: no long fights
            if (Doing == Tactic.ThirdParty) r += 15;
            if (_rotating && _rotUrgent) r = Math.Min(r, 12); // the circle comes first
            else if (_rotating) r *= 0.7; // on the move: fights on the way only, nothing out of its way
            return r;
        }

        /// <summary>Whether to take on an enemy it just made out (not one that hurt it: those it answers or leaves).</summary>
        internal bool TakeFight(NetServer server, ServerPlayer me, in MotorState s, double dist, int enemyId, int tick)
        {
            if (dist > EngageRange(s)) return false;
            if (dist < 6) return true; // right on top of it: fight
            double mine = Hp(s, server.Movement), theirs = EnemyHpGuess(server, me, enemyId, tick);
            // Starting a fight wants enough health to win it: careful players want more (80% at the most careful).
            if (mine >= 0.55 + 0.25 * (1 - Aggression) || theirs < mine - 0.05) return true;
            // Hurt: heal first if it can, else only a fight it is likely to win, close.
            return !HasHeals(s.Hand.Bag) && dist < 15 && mine >= 0.3;
        }

        /// <summary>Mid-fight: whether to break off and get into cover (to heal, or because it is outnumbered).</summary>
        internal bool ShouldRetreat(NetServer server, ServerPlayer me, in MotorState s, double dist, int enemyId, int tick)
        {
            if (dist < 5) return false; // running at point blank is death
            if (tick < _coverReadyTick) return false;
            double mine = Hp(s, server.Movement), theirs = EnemyHpGuess(server, me, enemyId, tick);
            bool outnumbered = tick - _secondSeenTick < 2 * SimTime.TicksPerSecond;
            if (outnumbered && mine < 0.55 + 0.25 * (1 - Aggression)) return true;
            if (theirs < 0.25 && mine > 0.2) return false; // nearly down: finish it
            // Losing the trade at range (it took more than it gave): get out of their line while it still can, as a
            // sensible player would, instead of trading to the death. Careful players leave sooner. Close in, running
            // is worse than fighting, so it stays.
            if (dist > 9 && theirs > mine + 0.15 && mine < 0.5 + 0.3 * (1 - Aggression)) return true;
            // An even trade at range that has gone on a while (both hurt): careful players call it off and live.
            if (dist > 12 && theirs >= mine - 0.05 && mine < 0.35 + 0.3 * (1 - Aggression)) return true;
            if (HasHeals(s.Hand.Bag))return mine < 0.3 + 0.25 * (1 - Aggression) && theirs >= mine - 0.1;
            return mine < 0.2 && theirs > mine + 0.25;
        }

        /// <summary>A second enemy in sight (checked ten times a second, nearest after the one it watches, within 50 m).</summary>
        private void CheckSecondEnemy(NetServer server, ServerPlayer me, ServerPlayer first, Vec3d eye, int tick)
        {
            if (tick - _secondCheckTick < 12 * SimTime.Per60 / 2) return;
            _secondCheckTick = tick;
            ServerPlayer best = null;
            double bestD = 50 * 50;
            foreach (ServerPlayer o in server.Players)
            {
                if (o == me || o == first || o.Team == me.Team || o.State.Dead) continue;
                double d = (o.State.Position - me.State.Position).LengthSquared;
                if (d < bestD) { bestD = d; best = o; }
            }
            if (best == null) return;
            double h = PlayerMovement.HeightOf(best.State, server.Movement);
            Vec3d to = best.State.Position - me.State.Position;
            double off = Math.Abs(Wrap(Math.Atan2(to.X, to.Z) - _yaw)) * 180 / Math.PI;
            if (off > Config.ViewConeDeg * 0.5) return;
            if (MaySee(server, eye, best.State.Position, h) && CanSee(server.Grid, eye, best.State.Position, h)) _secondSeenTick = tick;
        }

        // ================================================================ Cover

        /// <summary>
        /// A walkable spot near it that the threat can't see (head and chest out of its line), reachable on foot, inside
        /// the circle, not toward the threat. Rings out to 14 m; the nearest good one wins, ties go away from the threat.
        /// </summary>
        internal bool FindCover(NetServer server, Vec3d from, Vec3d threat, out Vec3d spot)
        {
            spot = default;
            int start = Nav.Nearest(from);
            if (start < 0) return false;
            Vec3d threatEye = threat + new Vec3d(0, 1.6, 0);
            Vec3d away = (from - threat).WithY(0);
            double awayLen = away.Length;
            away = awayLen > 1e-3 ? away / awayLen : new Vec3d(Math.Sin(_yaw), 0, Math.Cos(_yaw));
            double bestScore = double.MaxValue;
            double[] rings = { 2.5, 5, 8, 11, 14 };
            double turn = _tac.NextDouble() * 0.5;
            foreach (double r in rings)
            {
                for (int k = 0; k < 12; k++)
                {
                    double a = (k + turn) * Math.PI / 6;
                    Vec3d dir = new Vec3d(Math.Sin(a), 0, Math.Cos(a));
                    int n = Nav.Nearest(new Vec3d(from.X + dir.X * r, from.Y, from.Z + dir.Z * r), 2);
                    if (n < 0 || Nav.Nodes[n].Open < 4 || !Nav.SameArea(start, n)) continue;
                    Vec3d f = Nav.Feet(n);
                    if (Math.Abs(f.Y - from.Y) > 3.5 || (f - threat).HorizontalLength < 7 || server.InStorm(f)) continue;
                    if (!Hidden(server, threatEye, f)) continue;
                    double toward = -(f - from).WithY(0).Dot(away); // positive: closer to the threat
                    double score = (f - from).HorizontalLength + Math.Max(0, toward) * 1.5;
                    if (score < bestScore) { bestScore = score; spot = f; }
                }
                if (bestScore < double.MaxValue) return true; // the nearest ring with a hidden spot
            }
            return false;
        }

        /// <summary>Head (standing) and chest at this spot are out of the line of an eye.</summary>
        internal static bool Hidden(NetServer server, Vec3d eye, Vec3d feet)
        {
            foreach (double up in new[] { 1.5, 0.9 })
            {
                Vec3d p = feet + new Vec3d(0, up, 0);
                if (!server.SightMayBeClear(eye, p)) continue; // the sight grid says blocked
                Vec3d d = p - eye;
                double len = d.Length;
                if (len < 1e-3) return false;
                TerrainHit hit = server.Grid.Raycast(eye, d / len, len);
                if (!hit.Hit || hit.Distance >= len - 0.05) return false;
            }
            return true;
        }

        /// <summary>Goes for cover from a threat (if it finds any). Cover is never for good: at most BrCoverSeconds.</summary>
        private bool StartCover(NetServer server, Vec3d from, Vec3d threat, int tick)
        {
            if (tick - _coverSearchTick < 30 * SimTime.Per60) return false;
            _coverSearchTick = tick;
            if (!FindCover(server, from, threat, out Vec3d spot)) return false;
            _coverSpot = spot;
            _coverThreat = threat;
            _coverUntil = tick + (int)(Config.BrCoverSeconds * SimTime.TicksPerSecond);
            _coverHealing = false;
            Doing = Tactic.Cover;
            CoverCount++;
            return true;
        }

        private void EndCover(int tick)
        {
            if (Doing != Tactic.Cover) return;
            Doing = Tactic.None;
            _coverReadyTick = tick + (int)(Config.BrCoverCooldownSeconds * SimTime.TicksPerSecond);
        }

        /// <summary>Stays in cover until healed (or nothing to heal with), then gets on with it; never past its time.</summary>
        private void StepCover(NetServer server, in MotorState s, int tick)
        {
            if (Doing != Tactic.Cover) return;
            bool hurt = s.Damage >= 20 && HasHeals(s.Hand.Bag);
            bool usingHeal = Items.IsHeal(s.Hand.HeldItem) && s.Hand.Using;
            // A heal under way may run a little past the time (a first-aid kit takes 3.5 s), never more than 6 s.
            if (tick > _coverUntil + (usingHeal ? 6 * SimTime.TicksPerSecond : 0)) { EndCover(tick); return; }
            if (server.InStorm(s.Position) || server.InStorm(_coverSpot)) { EndCover(tick); return; }
            bool there = (s.Position - _coverSpot).HorizontalLength < 1.5;
            if (there && !hurt && !usingHeal && tick - _hurtTick > 2 * SimTime.TicksPerSecond) EndCover(tick);
            _coverHealing = there && (hurt || usingHeal);
        }

        // ================================================================ Rotating with the circle

        /// <summary>
        /// Whether it should be heading into the next circle now: it works out when the moving edge will reach it and
        /// how long the walk in takes, and leaves with its own safety margin (careful bots early, pushy ones late).
        /// The spot it heads for sits at its own depth inside the next circle (some play the edge, some the middle).
        /// </summary>
        private bool RotateGoal(NetServer server, in MotorState s, int tick, out Vec3d goal)
        {
            goal = default;
            StormState st = server.Match.Storm;
            if (server.Match.Phase != MatchPhase.Playing || !st.Active) { _rotating = false; return false; }
            if (st.Phase != _rotPhase)
            {
                _rotPhase = st.Phase;
                _rotDepth = 0.25 + _tac.NextDouble() * 0.45; // distance from the next centre, share of its radius
                _rotating = false;
                _rotCheckTick = int.MinValue / 2;
                _rotGoal = default;
            }
            if (tick - _rotCheckTick >= SimTime.TicksPerSecond)
            {
                _rotCheckTick = tick;
                double nx = st.ToX, nz = st.ToZ, nr = st.ToRadius;
                double dx = s.Position.X - nx, dz = s.Position.Z - nz;
                double d = Math.Sqrt(dx * dx + dz * dz);
                double want = nr * _rotDepth;
                if (d <= Math.Max(want, nr * 0.85 - 3)) _rotating = false; // inside already: nothing to do
                else
                {
                    // When does the edge get here? (the circle moves and shrinks between ShrinkStart and ShrinkEnd)
                    double secsToEdge = double.MaxValue;
                    for (int t = Math.Max(tick, st.ShrinkStart); t <= st.ShrinkEnd; t += 2 * SimTime.TicksPerSecond)
                        if (st.OutsideBy(s.Position, t) > -6) { secsToEdge = (t - tick) / (double)SimTime.TicksPerSecond; break; }
                    if (st.OutsideBy(s.Position, tick) > -6) secsToEdge = 0;
                    double walk = (d - want) / Config.BrRotateSpeed * 1.3;
                    double margin = Config.BrRotateMarginMin + (Config.BrRotateMarginMax - Config.BrRotateMarginMin) * (1 - Aggression);
                    if (!_rotating && secsToEdge < walk + margin)
                    {
                        _rotating = true;
                        RotateCount++;
                        Vec3d dir = d > 1e-3 ? new Vec3d(dx / d, 0, dz / d) : Vec3d.Zero;
                        Vec3d p = new Vec3d(nx + dir.X * want, s.Position.Y, nz + dir.Z * want);
                        int node = Nav.Nearest(p, 8);
                        _rotGoal = node >= 0 ? Nav.Feet(node) : p;
                    }
                    _rotUrgent = secsToEdge < walk + 5;
                }
            }
            if (!_rotating) return false;
            goal = _rotGoal;
            return true;
        }

        // ================================================================ Third-partying

        /// <summary>
        /// Heard a fight it isn't in: some bots go and clean up (pushy ones more often), if healthy and armed for it.
        /// Decided once per fight; it gives up after BrThirdPartySeconds or when the shooting stops.
        /// </summary>
        private bool ThirdPartyGoal(NetServer server, in MotorState s, int tick, out Vec3d goal)
        {
            goal = default;
            if (Doing == Tactic.ThirdParty)
            {
                if (tick > _tpUntil) { Doing = Tactic.None; _tpRollTick = tick; }
                else if (HeardFight(server, s.Position, tick, out Vec3d m)) _tpGoal = m;
                else if ((s.Position - _tpGoal).HorizontalLength < 6) { Doing = Tactic.None; _tpRollTick = tick; }
                if (Doing == Tactic.ThirdParty) { goal = _tpGoal; return true; }
                return false;
            }
            // Only near full health: walking into someone else's fight is the most dangerous thing in the game.
            if (Hp(s, server.Movement) < 0.8 || (_rotating && _rotUrgent)) return false;
            if (!HeardFight(server, s.Position, tick, out Vec3d mid)) return false;
            int key = (int)Math.Floor(mid.X / 30) * 1000 + (int)Math.Floor(mid.Z / 30);
            if (key != _tpKey || tick - _tpRollTick > 20 * SimTime.TicksPerSecond)
            {
                _tpKey = key;
                _tpRollTick = tick;
                // Careful players (aggression under 0.4) never go looking for someone else's fight.
                double chance = Config.BrThirdPartyMin + (Config.BrThirdPartyMax - Config.BrThirdPartyMin) * Math.Max(0, (Aggression - 0.4) / 0.6);
                _tpGo = _tac.NextDouble() < chance && (HasLongGun(s.Hand.Bag) || (mid - s.Position).HorizontalLength < 45);
                if (_tpGo)
                {
                    Doing = Tactic.ThirdParty;
                    ThirdPartyCount++;
                    _tpGoal = mid;
                    _tpUntil = tick + (int)(Config.BrThirdPartySeconds * SimTime.TicksPerSecond);
                    goal = mid;
                    return true;
                }
            }
            return false;
        }
    }
}
