using System;
using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.ML;
using Sculpt.Core.Net;

namespace Sculpt.Trainer
{
    /// <summary>A training world the PPO loop can run: some learning bots, their steps, and a line of stats.</summary>
    public interface ITrainEnv
    {
        void Run(int ticks);
        IEnumerable<Traj> Trajs { get; }
        long TakeDecisions();
        /// <summary>Stats since the last call, added into the totals by key.</summary>
        void TakeStats(Dictionary<string, double> into);
        void SetModel(MlModel m);
    }

    /// <summary>
    /// A real 32-player battle royale on the real map, rules and circle: some learning bots, the rest scripted. The
    /// learning bots are paid for placement: every other player knocked out while they are alive is worth a little, the
    /// win is worth a lot, and dying ends their run. Damage and kills count as in the duel, and the circle's damage is
    /// damage taken, so getting caught outside it costs. Matches loop by themselves (lobby, countdown, play, results).
    /// </summary>
    public sealed class BrEnv : ITrainEnv
    {
        public readonly NetServer S;
        private readonly List<(MlBrain brain, LearnActor actor, ServerPlayer p)> _learners = new List<(MlBrain, LearnActor, ServerPlayer)>();
        private readonly MlRng _rng;
        private readonly HashSet<int> _alive = new HashSet<int>();
        private int _matchNumber = -1;
        public const float OutlivePer = 2f / 31, WinReward = 3f;
        private long _decisions;
        private double _place, _placeN, _wins, _kills;

        public BrEnv(MlModel model, int learners, BrMode mode, int seed)
        {
            _rng = new MlRng((ulong)seed * 104729 + 1);
            SubGrid g = BrMap.BuildGround();
            BrushWorld w = BrMap.BuildBrushes(out BreachMapInfo info, out BrMapInfo br);
            var nc = new NetConfig();
            S = new NetServer(BrMap.Terrain(g, w), new MovementConfig(), nc, (c, d, r) => { });
            S.Sculpt = new SculptSystem(g, new SculptConfig());
            TileWalls.Find(S.Grid)?.Prepare();
            BotNav nav = BrMap.BuildNav(1.0);
            var cfg = new BrConfig { Mode = mode, Seed = seed, FillWithoutHumans = true };
            cfg.MakeBot = i => new DuelBot(nav, DuelBotConfig.For((BotLevel)(i % 3)), seed + 31 * i);
            S.EnableBattleRoyale(cfg, br);
            BrSetup.PlaceItems(S, info, br);
            for (int k = 0; k < learners; k++)
            {
                ulong ks = (ulong)seed * 1000 + (ulong)k;
                var actor = new LearnActor(model, ks * 31 + 5);
                var brain = new MlBrain(nav, actor, MlSkill.Random(_rng), ks);
                brain.OnSpawn = (b, p) => b.Skill = MlSkill.Random(_rng);
                BrRegion r = br.Regions[k % br.Regions.Count];
                ServerPlayer p = S.AddBot(brain, 1, r.SquadSpots[k % 4], r.SquadYaw);
                _learners.Add((brain, actor, p));
            }
        }

        public IEnumerable<Traj> Trajs { get { foreach (var l in _learners) yield return l.actor.Traj; } }
        public void SetModel(MlModel m) { foreach (var l in _learners) l.actor.Model = m; }
        public long TakeDecisions() { long d = _decisions; _decisions = 0; return d; }

        public void TakeStats(Dictionary<string, double> into)
        {
            Add(into, "br_place_sum", _place); Add(into, "br_place_n", _placeN); Add(into, "br_wins", _wins); Add(into, "br_kills", _kills);
            _place = _placeN = _wins = _kills = 0;
        }

        private static void Add(Dictionary<string, double> d, string k, double v) => d[k] = (d.TryGetValue(k, out double o) ? o : 0) + v;

        public void Run(int ticks)
        {
            for (int t = 0; t < ticks; t++)
            {
                S.Step();
                foreach (var l in _learners) if (l.brain.DecidedThisTick) _decisions++;
                Reward();
            }
        }

        private void Reward()
        {
            MatchState m = S.Match;
            foreach (CombatEvent e in S.CombatEvents)
            {
                if (e.Type == CombatEventType.Hit)
                    foreach (var l in _learners)
                    {
                        if (e.Attacker == l.p.Id && e.Victim != l.p.Id) l.actor.Traj.Pending += e.Damage * DuelEnv.DealtPer;
                        if (e.Victim == l.p.Id) l.actor.Traj.Pending -= e.Damage * DuelEnv.TakenPer;
                    }
                else if (e.Type == CombatEventType.Kill)
                    foreach (var l in _learners)
                        if (e.Attacker == l.p.Id && e.Victim != l.p.Id) { l.actor.Traj.Pending += DuelEnv.KillReward * 0.5f; _kills++; }
            }
            if (m.Phase != MatchPhase.Playing && m.Phase != MatchPhase.Ended) { _matchNumber = -1; return; }
            if (m.Phase == MatchPhase.Playing && _matchNumber != S.BrMatchNumber)
            {
                _matchNumber = S.BrMatchNumber;
                _alive.Clear();
                foreach (ServerPlayer p in S.Players) if (S.InBrMatch(p.Id) && !p.State.Dead) _alive.Add(p.Id);
                return;
            }
            // Who went out this tick: each learner still in gets paid for outliving them.
            int outNow = 0;
            List<int> gone = null;
            foreach (int id in _alive)
            {
                ServerPlayer p = Find(id);
                if (p == null || p.State.Dead) { (gone ??= new List<int>()).Add(id); outNow++; }
            }
            if (gone == null && m.Phase != MatchPhase.Ended) return;
            if (gone != null) foreach (int id in gone) _alive.Remove(id);
            foreach (var l in _learners)
            {
                if (!S.InBrMatch(l.p.Id)) continue;
                bool wasIn = gone != null && gone.Contains(l.p.Id);
                if (wasIn)
                {
                    // Out: its place is how many were still in (plus the others out on the same tick share it).
                    int place = _alive.Count + 1;
                    _place += place; _placeN++;
                    l.actor.Traj.Close(true);
                    continue;
                }
                if (l.p.State.Dead) continue;
                l.actor.Traj.Pending += outNow * OutlivePer;
                if (m.Phase == MatchPhase.Ended && m.WinnerTeam == l.p.Team && _alive.Contains(l.p.Id))
                {
                    l.actor.Traj.Pending += WinReward;
                    l.actor.Traj.Close(true);
                    _wins++; _place += 1; _placeN++;
                    _alive.Remove(l.p.Id);
                }
            }
            if (m.Phase == MatchPhase.Ended) _alive.Clear();
        }

        private ServerPlayer Find(int id)
        {
            foreach (ServerPlayer p in S.Players) if (p.Id == id) return p;
            return null;
        }
    }
}
