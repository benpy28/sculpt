using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.ML
{
    /// <summary>Chooses a bot's action from its observation: a trained model in the game, the trainer during training.</summary>
    public interface IMlActor
    {
        void Decide(MlBrain brain, float[] obs, int[] action);
    }

    /// <summary>The game's actor: a trained model.</summary>
    public sealed class MlModelActor : IMlActor
    {
        private readonly MlPolicyRunner _run;
        private readonly MlRng _rng;
        public MlModelActor(MlModel model, ulong seed) { _run = new MlPolicyRunner(model); _rng = new MlRng(seed); }
        public void Decide(MlBrain brain, float[] obs, int[] action) => _run.Act(obs, brain.Skill.Temperature, _rng, action);
    }

    /// <summary>
    /// A learned bot. It is a server player with no connection (like the scripted bot): every tick it records what it can
    /// see and hear, 30 times a second it builds its observation and asks its actor (a trained model) what to do, and its
    /// hands turn that into the same input commands a person sends. So every rule, the match, anti-cheat and replays treat
    /// it like anyone else; replays record its commands, so they re-run exactly without the model.
    /// </summary>
    public sealed class MlBrain : IBotBrain
    {
        public MlSkill Skill;
        public IMlActor Actor;
        public readonly MlSenses Senses;
        public readonly MlHands Hands;
        public readonly MlObs.Context Context;
        private readonly MlRng _rng;
        private readonly float[] _obs = new float[MlObs.Size];
        private readonly int[] _action = new int[MlActions.Heads];
        private int _step = MlActions.DecisionTicks;
        private bool _wasDead = true;
        private Vec3d _lastPos;
        private Vec3d _roam;
        private int _roamTick = int.MinValue / 2;
        private int _lootTick = int.MinValue / 2;
        private Vec3d _lootGoal;
        private bool _hasLoot;

        public MlBrain(BotNav nav, IMlActor actor, MlSkill skill, ulong seed)
        {
            Actor = actor;
            Skill = skill ?? new MlSkill();
            Senses = new MlSenses(seed * 3 + 1);
            Hands = new MlHands(seed * 3 + 2);
            _rng = new MlRng(seed * 3 + 3);
            Context = new MlObs.Context(Senses) { Nav = nav };
        }

        public bool CountsAsPlayer => true;

        /// <summary>The observation and action of the newest decision (training reads these).</summary>
        public float[] LastObs => _obs;
        public int[] LastAction => _action;
        /// <summary>Server tick of the newest decision, and whether this tick made one.</summary>
        public int DecisionTick { get; private set; } = int.MinValue;
        public bool DecidedThisTick { get; private set; }
        /// <summary>Called when it (re)spawns: the trainer picks a new skill here.</summary>
        public Action<MlBrain, ServerPlayer> OnSpawn;

        /// <summary>
        /// Imitation (behaviour cloning): another brain plays (the scripted bot now, a person's replay later) and this one
        /// only watches through its own senses, then labels each decision with the action the teacher's next four
        /// commands amount to (<see cref="MlActions.FromCommands"/>) and hands (observation, action) to <see cref="OnLabelled"/>.
        /// </summary>
        public IBotBrain Teacher;
        public Action<MlBrain, float[], int[]> OnLabelled;
        private readonly List<InputCmd> _teacherCmds = new List<InputCmd>(MlActions.DecisionTicks);
        private bool _pending;
        private double _yawBefore, _pitchBefore;

        public InputCmd Next(NetServer server, ServerPlayer me)
        {
            if (Teacher != null) return Watch(server, me);
            DecidedThisTick = false;
            MotorState s = me.State;
            if (s.Dead)
            {
                _wasDead = true;
                Senses.Clear();
                return InputCmd.Quantize(new InputCmd { Tick = server.Tick, ViewTick = server.Tick, Move = new MoveInput { Yaw = Hands.Yaw }, Pitch = (float)Hands.Pitch });
            }
            if (_wasDead || (s.Position - _lastPos).Length > 4)
            {
                _wasDead = false;
                Hands.Reset(me.SpawnYaw, 0);
                Context.MainRoute.Clear(); Context.LootRoute.Clear(); Context.RaysValid = false;
                Array.Clear(Context.PrevAction, 0, Context.PrevAction.Length);
                _roamTick = int.MinValue / 2;
                _step = MlActions.DecisionTicks;
                OnSpawn?.Invoke(this, me);
            }
            _lastPos = s.Position;
            Senses.Update(server, me, Hands.Yaw, Skill);

            if (_step >= MlActions.DecisionTicks)
            {
                _step = 0;
                MlObs.Goals goals = Goals(server, me);
                MlObs.Build(server, me, Hands.Yaw, Hands.Pitch, Skill, Context, goals, _obs);
                Actor.Decide(this, _obs, _action);
                Hands.Set(_action);
                Array.Copy(_action, Context.PrevAction, _action.Length);
                DecisionTick = server.Tick;
                DecidedThisTick = true;
            }
            _step++;
            return Hands.Tick(server, me, Skill);
        }

        private InputCmd Watch(NetServer server, ServerPlayer me)
        {
            DecidedThisTick = false;
            MotorState s = me.State;
            double yaw = me.LastCmd.Move.Yaw, pitch = me.LastCmd.Pitch;
            if (s.Dead) { _wasDead = true; _pending = false; Senses.Clear(); return Teacher.Next(server, me); }
            if (_wasDead || (s.Position - _lastPos).Length > 4)
            {
                _wasDead = false;
                _pending = false;
                Context.MainRoute.Clear(); Context.LootRoute.Clear(); Context.RaysValid = false;
                Array.Clear(Context.PrevAction, 0, Context.PrevAction.Length);
                _roamTick = int.MinValue / 2;
                _step = MlActions.DecisionTicks;
                yaw = me.SpawnYaw; pitch = 0;
                OnSpawn?.Invoke(this, me);
            }
            _lastPos = s.Position;
            Senses.Update(server, me, yaw, Skill);
            if (_step >= MlActions.DecisionTicks)
            {
                if (_pending && _teacherCmds.Count == MlActions.DecisionTicks)
                {
                    MlActions.FromCommands(_yawBefore, _pitchBefore, _teacherCmds, _action);
                    OnLabelled?.Invoke(this, _obs, _action);
                    Array.Copy(_action, Context.PrevAction, _action.Length);
                }
                _step = 0;
                _teacherCmds.Clear();
                MlObs.Goals goals = Goals(server, me);
                MlObs.Build(server, me, yaw, pitch, Skill, Context, goals, _obs);
                _yawBefore = yaw; _pitchBefore = pitch;
                _pending = true;
                DecisionTick = server.Tick;
                DecidedThisTick = true;
            }
            _step++;
            InputCmd cmd = Teacher.Next(server, me);
            _teacherCmds.Add(InputCmd.Quantize(cmd));
            return cmd;
        }

        // ================================================================ Goals (where a person would head)

        private MlObs.Goals Goals(NetServer server, ServerPlayer me)
        {
            var g = new MlObs.Goals();
            MotorState s = me.State;
            int tick = server.Tick;
            MatchState m = server.Match;
            StormState st = m.Storm;
            bool br = m.IsBr && m.Phase == MatchPhase.Playing && st.Active;

            // Main: out of the next circle first, then the enemy it last knew of, else somewhere to look.
            if (br && st.OutsideBy(s.Position, tick) > -8 || br && Outside(s.Position, st.ToX, st.ToZ, st.ToRadius - 5))
            {
                double dx = s.Position.X - st.ToX, dz = s.Position.Z - st.ToZ, len = Math.Max(1e-3, Math.Sqrt(dx * dx + dz * dz));
                double r = Math.Max(0, st.ToRadius * 0.6);
                g.Main = new Vec3d(st.ToX + dx / len * r, s.Position.Y, st.ToZ + dz / len * r);
                g.HasMain = true;
            }
            else if (Senses.Known.Count > 0)
            {
                g.Main = Senses.Known[0].Pos;
                g.HasMain = true;
            }
            else
            {
                if (tick - _roamTick > 25 * SimTime.TicksPerSecond || (_roam - s.Position).HorizontalLength < 4) PickRoam(server, s.Position, br, st, tick);
                g.Main = _roam;
                g.HasMain = Context.Nav != null || _roamTick > int.MinValue / 2;
            }

            // Loot it can see (or that is right by it), wanted first, nearest first.
            if (tick - _lootTick >= 30)
            {
                _lootTick = tick;
                FindLoot(server, me);
                _hasLoot = false;
                foreach (GroundItem it in Context.Loot)
                    if (MlObs.Wanted(it, s.Hand.Bag, server.Movement)) { _hasLoot = true; _lootGoal = it.Position; break; }
            }
            g.HasLoot = _hasLoot;
            g.Loot = _lootGoal;
            return g;
        }

        private static bool Outside(Vec3d p, double x, double z, double r)
        {
            double dx = p.X - x, dz = p.Z - z;
            return dx * dx + dz * dz > r * r;
        }

        private void PickRoam(NetServer server, Vec3d from, bool br, StormState st, int tick)
        {
            _roamTick = tick;
            BotNav nav = Context.Nav;
            if (nav == null || nav.Nodes.Count == 0) { _roam = from; return; }
            for (int tries = 0; tries < 12; tries++)
            {
                Vec3d p = nav.Feet(_rng.Next(nav.Nodes.Count));
                if (br && Outside(p, st.ToX, st.ToZ, st.ToRadius * 0.85)) continue;
                if ((p - from).HorizontalLength < 10) continue;
                _roam = p;
                return;
            }
            _roam = nav.Feet(_rng.Next(nav.Nodes.Count));
        }

        private readonly List<(double d, GroundItem g)> _lootScratch = new List<(double, GroundItem)>();

        private void FindLoot(NetServer server, ServerPlayer me)
        {
            MotorState s = me.State;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, server.Movement), 0);
            _lootScratch.Clear();
            GroundItems items = server.Items;
            for (int i = 0; i < items.Count; i++)
            {
                GroundItem it = items[i];
                double d = (it.Position - s.Position).Length;
                if (d > 45) continue;
                if (d > 6)
                {
                    Vec3d to = it.Position + new Vec3d(0, 0.3, 0) - eye;
                    double len = to.Length;
                    TerrainHit h = server.Grid.Raycast(eye, to / len, len);
                    if (h.Hit && h.Distance < len - 0.2) continue;
                }
                bool want = MlObs.Wanted(it, s.Hand.Bag, server.Movement);
                _lootScratch.Add((want ? d : d + 1000, it));
            }
            _lootScratch.Sort((a, b) => a.d.CompareTo(b.d));
            Context.Loot.Clear();
            for (int i = 0; i < Math.Min(MlObs.LootSlots, _lootScratch.Count); i++) Context.Loot.Add(_lootScratch[i].g);
        }
    }
}
