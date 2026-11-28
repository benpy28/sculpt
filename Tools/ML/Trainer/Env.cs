using System;
using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.ML;
using Sculpt.Core.Net;

namespace Sculpt.Trainer
{
    /// <summary>One learning bot's steps since the last update.</summary>
    public sealed class Traj
    {
        public readonly List<float[]> Obs = new List<float[]>();
        public readonly List<int[]> Act = new List<int[]>();
        public readonly List<float> LogP = new List<float>(), Val = new List<float>(), Rew = new List<float>();
        public readonly List<bool> Done = new List<bool>();
        public bool Open; // the newest step still collects reward
        public float Pending;
        public float Bootstrap;
        /// <summary>The aim-shaping potential at the newest step (see <see cref="LearnActor.AimShaping"/>).</summary>
        public float Phi;
        public void Clear() { Obs.Clear(); Act.Clear(); LogP.Clear(); Val.Clear(); Rew.Clear(); Done.Clear(); Open = false; Pending = 0; Bootstrap = 0; Phi = 0; }
        public int Closed => Open ? Obs.Count - 1 : Obs.Count;

        /// <summary>Closes the newest step with the reward collected so far.</summary>
        public void Close(bool done)
        {
            if (!Open) return;
            if (done) Pending -= Phi; // the potential ends at zero
            Rew[Rew.Count - 1] += Pending;
            Pending = 0;
            Done[Done.Count - 1] = done;
            Open = false;
        }
    }

    /// <summary>
    /// The trainer's actor: runs the current policy and value for a learning bot and records each step. Observations are
    /// stored already scaled by the model's (fixed) normalizer.
    /// </summary>
    public sealed class LearnActor : IMlActor
    {
        public MlModel Model;
        public readonly Traj Traj = new Traj();
        private readonly MlRng _rng;
        private readonly float[] _x = new float[MlObs.Size], _a, _b, _logits = new float[MlActions.TotalLogits], _v = new float[1];

        public LearnActor(MlModel model, ulong seed)
        {
            Model = model; _rng = new MlRng(seed);
            int w = Math.Max(model.Policy.Widest, model.Value.Widest);
            _a = new float[w]; _b = new float[w];
        }

        /// <summary>
        /// Potential-based shaping (it can't change which play is best, only how fast it is found): being on target with
        /// an enemy you can see is worth up to this much, so turning onto them pays a little at once instead of only when
        /// the shot lands.
        /// </summary>
        public float AimShaping = 0.3f;
        public float Gamma = 0.995f;
        private static readonly int EnemyAt = MlObs.SelfSize + MlObs.RaySize;

        public static float AimPotential(float[] obs)
        {
            if (obs[EnemyAt + 1] < 0.5f) return 0; // nobody in sight
            float err = Math.Abs(obs[EnemyAt + 10]) * 30 + Math.Abs(obs[EnemyAt + 12]) * 30;
            return 1 - Math.Min(err, 30) / 30;
        }

        public void Decide(MlBrain brain, float[] obs, int[] action)
        {
            float phi = AimShaping * AimPotential(obs);
            if (Traj.Open) Traj.Pending += Gamma * phi - Traj.Phi;
            Traj.Close(false);
            Traj.Phi = phi;
            Model.Normalize(obs, _x);
            Model.Policy.Forward(_x, _logits, _a, _b);
            Model.Value.Forward(_x, _v, _a, _b);
            MlPolicyRunner.Sample(_logits, 1.0, _rng, action);
            Traj.Obs.Add((float[])_x.Clone());
            Traj.Act.Add((int[])action.Clone());
            Traj.LogP.Add(LogProb(_logits, action));
            Traj.Val.Add(_v[0]);
            Traj.Rew.Add(0);
            Traj.Done.Add(false);
            Traj.Open = true;
        }

        public static float LogProb(float[] logits, int[] action)
        {
            double lp = 0; int off = 0;
            for (int h = 0; h < MlActions.Heads; h++)
            {
                int n = MlActions.HeadSizes[h];
                double max = double.MinValue;
                for (int k = 0; k < n; k++) max = Math.Max(max, logits[off + k]);
                double sum = 0;
                for (int k = 0; k < n; k++) sum += Math.Exp(logits[off + k] - max);
                lp += logits[off + action[h]] - max - Math.Log(sum);
                off += n;
            }
            return (float)lp;
        }
    }

    /// <summary>What plays the other side of a training match.</summary>
    public enum Opp { Self, Snapshot, ScriptEasy, ScriptNormal, ScriptHard }

    /// <summary>
    /// A 1v1 on the real duel map with the real server and rules (free play: respawn 3 s after a death, walls stay
    /// broken until the map is rebuilt every few minutes). Team 1 always learns; team 2 learns too in self-play, or is a
    /// past copy, or the scripted bot.
    /// </summary>
    public sealed class DuelEnv : ITrainEnv
    {
        public NetServer S;
        public readonly BotNav Nav;
        private BreachMapInfo _info;
        public readonly List<(MlBrain brain, LearnActor actor, ServerPlayer p)> Learners = new List<(MlBrain, LearnActor, ServerPlayer)>();
        public ServerPlayer Other;
        public Opp Opponent;
        private readonly MlRng _rng;
        private readonly int _seed;
        private int _builtTick;
        public int ResetTicks = 180 * SimTime.TicksPerSecond;
        public MlModel Model, SnapshotModel;
        public bool RandomSkill = true;
        public MlSkill FixedSkill = MlSkill.Normal;
        public MlSkill OppSkill = MlSkill.Normal;

        // Reward weights.
        public const float DealtPer = 1f / 100, TakenPer = 1f / 150, KillReward = 1f, DeathReward = -0.7f;

        // Stats since the last read.
        public int Kills, Deaths;
        public long Decisions;

        public DuelEnv(BotNav nav, int seed) { Nav = nav; _seed = seed; _rng = new MlRng((ulong)seed * 7919 + 13); }

        // ---- ITrainEnv (the PPO loop): opponents picked by these when the map is rebuilt.
        public Func<Opp> PickOpp = () => Opp.Self;
        public Func<MlModel> PickSnapshot = () => null;
        public void Run(int ticks) => Run(ticks, PickOpp, PickSnapshot);
        public IEnumerable<Traj> Trajs { get { foreach (var l in Learners) yield return l.actor.Traj; } }
        public long TakeDecisions() { long d = Decisions; Decisions = 0; return d; }
        public void SetModel(MlModel m) { Model = m; foreach (var l in Learners) l.actor.Model = m; }
        private readonly Dictionary<Opp, (int k, int d)> _kd = new Dictionary<Opp, (int, int)>();
        public void TakeStats(Dictionary<string, double> into)
        {
            foreach (var kv in _kd)
            {
                string n = kv.Key.ToString().ToLowerInvariant();
                into["k_" + n] = (into.TryGetValue("k_" + n, out double a) ? a : 0) + kv.Value.k;
                into["d_" + n] = (into.TryGetValue("d_" + n, out double b) ? b : 0) + kv.Value.d;
            }
            _kd.Clear();
        }

        public void Build(MlModel model, Opp opp, MlModel snapshot = null)
        {
            Model = model; Opponent = opp; SnapshotModel = snapshot;
            foreach (var l in Learners) l.actor.Traj.Close(false);
            Learners.Clear();
            var nc = new NetConfig();
            SubGrid ground = DuelMap.BuildGround();
            S = new NetServer(DuelMap.Terrain(ground, DuelMap.BuildBrushes(out _info)), new MovementConfig(), nc, (c, d, r) => { });
            TileWalls.Find(S.Grid)?.Prepare();
            S.Sculpt = new SculptSystem(ground, new SculptConfig());
            foreach (GroundItem g in _info.ItemSpawns) S.SpawnItem(g.Kind, g.Count, g.Position);
            int side = _rng.Next(2); // which spawn the learner gets
            ulong k = (ulong)_seed * 1000 + _rng.Next();
            AddLearner(1, side, k);
            switch (opp)
            {
                case Opp.Self: AddLearner(2, 1 - side, k + 1); break;
                case Opp.Snapshot:
                {
                    var b = new MlBrain(Nav, new MlModelActor(snapshot ?? model, k + 2), OppSkill.Clone(), k + 2);
                    b.OnSpawn = (br, p) => { if (RandomSkill) br.Skill = MlSkill.Random(_rng); };
                    Other = S.AddBot(b, 2, _info.TeamSpawns[1 - side][0], _info.SpawnYaw[1 - side]);
                    break;
                }
                default:
                {
                    BotLevel lv = opp == Opp.ScriptEasy ? BotLevel.Easy : opp == Opp.ScriptHard ? BotLevel.Hard : BotLevel.Normal;
                    Other = S.AddBot(new DuelBot(Nav, DuelBotConfig.For(lv), (int)(k + 3)), 2, _info.TeamSpawns[1 - side][0], _info.SpawnYaw[1 - side]);
                    break;
                }
            }
            _builtTick = S.Tick;
        }

        private void AddLearner(int team, int side, ulong seed)
        {
            var actor = new LearnActor(Model, seed * 31 + 7);
            var brain = new MlBrain(Nav, actor, RandomSkill ? MlSkill.Random(_rng) : FixedSkill.Clone(), seed);
            brain.OnSpawn = (b, p) => { if (RandomSkill) b.Skill = MlSkill.Random(_rng); };
            ServerPlayer p = S.AddBot(brain, team, _info.TeamSpawns[side][0], _info.SpawnYaw[side]);
            Learners.Add((brain, actor, p));
            if (team == 2) Other = p;
        }

        /// <summary>Runs ticks, giving rewards. Rebuilds the map when it is due (training only resumes on whole steps).</summary>
        public void Run(int ticks, Func<Opp> pickOpp, Func<MlModel> pickSnapshot)
        {
            for (int t = 0; t < ticks; t++)
            {
                if (S.Tick - _builtTick >= ResetTicks)
                {
                    Opp o = pickOpp();
                    Build(Model, o, o == Opp.Snapshot ? pickSnapshot() : null);
                }
                S.Step();
                foreach (var l in Learners) if (l.brain.DecidedThisTick) Decisions++;
                Reward();
            }
        }

        private void Reward()
        {
            ServerPlayer self = Learners[0].p;
            foreach (CombatEvent e in S.CombatEvents)
            {
                if (e.Type == CombatEventType.Hit)
                {
                    foreach (var l in Learners)
                    {
                        if (e.Attacker == l.p.Id && e.Victim != l.p.Id) l.actor.Traj.Pending += e.Damage * DealtPer;
                        if (e.Victim == l.p.Id) l.actor.Traj.Pending -= e.Damage * TakenPer;
                    }
                }
                else if (e.Type == CombatEventType.Kill)
                {
                    foreach (var l in Learners)
                    {
                        if (e.Attacker == l.p.Id && e.Victim != l.p.Id) l.actor.Traj.Pending += KillReward;
                        if (e.Victim == l.p.Id) { l.actor.Traj.Pending += DeathReward; l.actor.Traj.Close(true); }
                    }
                    // In the 1v1 every death is the other player's kill (falls included), as in the match.
                    var c = _kd.TryGetValue(Opponent, out var v) ? v : (0, 0);
                    if (e.Victim == self.Id) { Deaths++; _kd[Opponent] = (c.Item1, c.Item2 + 1); }
                    else if (Other != null && e.Victim == Other.Id) { Kills++; _kd[Opponent] = (c.Item1 + 1, c.Item2); }
                }
            }
        }
    }
}
