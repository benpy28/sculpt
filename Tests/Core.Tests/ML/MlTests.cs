using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.ML;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;

namespace Sculpt.Core.Tests.ML
{
    /// <summary>The learned-bot module: senses, observations, hands, the network runtime and the brain on a real server.</summary>
    public class MlTests
    {
        private static BotNav _nav;
        private static BreachMapInfo _info;

        private static BotNav Nav()
        {
            if (_nav != null) return _nav;
            DuelMap.BuildBrushes(out _info);
            return _nav = DuelMap.BuildNav();
        }

        private static NetServer DuelServer(out BreachMapInfo info)
        {
            Nav();
            SubGrid g = DuelMap.BuildGround();
            var s = new NetServer(DuelMap.Terrain(g, DuelMap.BuildBrushes(out info)), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            s.Sculpt = new SculptSystem(g, new SculptConfig());
            foreach (GroundItem gi in info.ItemSpawns) s.SpawnItem(gi.Kind, gi.Count, gi.Position);
            return s;
        }

        private static NetServer OpenField()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return new NetServer(g, new MovementConfig(), new NetConfig(), (c, d, r) => { });
        }

        /// <summary>An actor that always does the same thing.</summary>
        private sealed class Fixed : IMlActor
        {
            public int[] Action = new int[MlActions.Heads];
            public int Calls;
            public void Decide(MlBrain brain, float[] obs, int[] action) { Calls++; Array.Copy(Action, action, action.Length); }
        }

        private sealed class Still : IBotBrain
        {
            public bool CountsAsPlayer => true;
            public InputCmd Next(NetServer server, ServerPlayer me) => new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
        }

        private static MlModel RandomModel(int seed)
        {
            MlModel m = MlModel.Fresh(new[] { 32, 16 }, seed);
            var r = new MlRng((ulong)seed);
            for (int k = 0; k < MlObs.Size; k++) { m.Mean[k] = (float)(r.NextDouble() - 0.5); m.Std[k] = (float)(0.5 + r.NextDouble()); }
            foreach (float[] w in m.Policy.W) for (int i = 0; i < w.Length; i++) w[i] = (float)(r.Gauss() * 0.2);
            return m;
        }

        // ------------------------------------------------------------------ observations

        [Test]
        public void Observation_HasTheDeclaredSize_AndOnlyFiniteBoundedNumbers_ThroughAWholeBotFight()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            var seen = new List<float[]>();
            var a = new MlBrain(Nav(), new MlModelActor(RandomModel(1), 1), MlSkill.Normal, 1);
            a.OnLabelled = (b, o, act) => seen.Add((float[])o.Clone());
            a.Teacher = new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 3);
            s.AddBot(a, 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
            s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Hard), 4), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            for (int t = 0; t < 40 * SimTime.TicksPerSecond; t++) s.Step();
            Assert.That(seen.Count, Is.GreaterThan(1000), "about 30 decisions a second while alive");
            bool anyEnemy = false;
            int enemyStart = MlObs.SelfSize + MlObs.RaySize;
            foreach (float[] o in seen)
            {
                Assert.That(o.Length, Is.EqualTo(MlObs.Size));
                foreach (float v in o) Assert.That(!float.IsNaN(v) && v >= -5 && v <= 5, "every number finite and clipped");
                anyEnemy |= o[enemyStart] == 1;
            }
            Assert.That(anyEnemy, "two hard bots meet within 40 s: the enemy slot fills at some point");
            Assert.That(MlObs.Size, Is.EqualTo(MlObs.SelfSize + MlObs.RaySize + MlObs.EnemySlots * MlObs.EnemySize + MlObs.MateSlots * MlObs.MateSize
                + MlObs.HurtSize + MlObs.MatchSize + MlObs.RouteSize + MlObs.LootSlots * MlObs.LootSize + MlObs.SkillSize + MlObs.PrevSize));
        }

        // ------------------------------------------------------------------ senses (no wallhack, reaction time)

        [Test]
        public void Senses_SeeAnEnemyInTheOpen_OnlyAReactionTimeLate_AndNeverThroughAWall()
        {
            NetServer s = OpenField();
            var skill = new MlSkill { ReactionTicks = 24, ViewConeDeg = 110 };
            ServerPlayer me = s.AddBot(new Still(), 1, new Vec3d(20, 0, 10), 0);
            ServerPlayer enemy = s.AddBot(new Still(), 2, new Vec3d(20, 0, 30), 0);
            s.Step(); // spawn
            var senses = new MlSenses(1);
            int firstKnown = -1;
            for (int t = 0; t < 60; t++)
            {
                s.Step();
                senses.Update(s, me, 0, skill); // looking straight at them (+z)
                if (firstKnown < 0 && senses.Known.Count > 0) firstKnown = t;
            }
            Assert.That(firstKnown, Is.InRange(skill.ReactionTicks - 1, skill.ReactionTicks + MlSenses.SightEvery), "known a reaction time after first sight");
            Assert.That(senses.Known[0].Seen, Is.True);
            Assert.That((senses.Known[0].Pos - enemy.State.Position).Length, Is.LessThan(0.01), "a seen enemy is placed exactly");

            // Looking the other way: never seen (and a still player makes no sound).
            var behind = new MlSenses(2);
            for (int t = 0; t < 60; t++) { s.Step(); behind.Update(s, me, Math.PI, skill); }
            Assert.That(behind.Known.Count, Is.Zero, "outside the view cone and silent: unknown");
        }

        [Test]
        public void Senses_DoNotSeeThroughWalls_OnTheDuelMap()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            // Two players either side of a solid wall: find a spot where a straight ray between eyes hits terrain.
            Vec3d a = info.TeamSpawns[0][0], b = info.TeamSpawns[1][0];
            Assert.That(MlSenses.CanSee(s.Grid, a + new Vec3d(0, 1.62, 0), b, 1.8), Is.False, "the duel map's divide hides one spawn from the other");
            ServerPlayer me = s.AddBot(new Still(), 1, a, 0);
            ServerPlayer en = s.AddBot(new Still(), 2, b, 0);
            s.Step();
            var senses = new MlSenses(3);
            var skill = new MlSkill { ViewConeDeg = 360 };
            Vec3d d = b - a;
            for (int t = 0; t < 120; t++) { s.Step(); senses.Update(s, me, Math.Atan2(d.X, d.Z), skill); }
            Assert.That(senses.Known.Count, Is.Zero, "a silent enemy behind walls is never known");
        }

        [Test]
        public void SightGridShortcut_NeverSeesThroughWalls_AndRarelyMissesALine()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            s.AddBot(new Still(), 1, info.TeamSpawns[0][0], 0).Viewer = true;
            s.AddBot(new Still(), 2, info.TeamSpawns[1][0], 0);
            s.Step(); s.Step(); // the server builds its sight grid
            Assert.That(s.SightGrid, Is.Not.Null, "the duel map has a sight grid");
            var rng = new MlRng(7);
            int seen = 0, hidden = 0, missed = 0;
            for (int k = 0; k < 4000; k++)
            {
                // Real standing spots only (a ray from inside a wall passes out of it; the grid calls that closed).
                var af = new Vec3d(rng.NextDouble() * info.SizeX, 0, rng.NextDouble() * info.SizeZ);
                var b = new Vec3d(af.X + (rng.NextDouble() - 0.5) * 60, 0, af.Z + (rng.NextDouble() - 0.5) * 60);
                if (Blocked(af) || Blocked(b)) { k--; continue; }
                var a = af + new Vec3d(0, 1.62, 0);
                bool slow = MlSenses.CanSee(s.Grid, a, b, 1.8), fast = MlSenses.CanSee(s.Grid, s.SightGrid, a, b, 1.8);
                if (fast) Assert.That(slow, $"never sees what a ray can't: from {a} to {b}");
                if (slow && !fast) missed++;
                if (slow) seen++; else hidden++;
            }
            Assert.That(seen, Is.GreaterThan(400)); Assert.That(hidden, Is.GreaterThan(400));
            TestContext.WriteLine($"{seen} seen, {hidden} hidden, {missed} lines the grid closed that a ray passes");
            Assert.That(missed, Is.LessThan(seen / 100), "the shortcut costs under 1% of sightings (grazing corners)");
            bool Blocked(Vec3d f) => s.Grid.BoxOverlapsSolid(new Aabb(f + new Vec3d(-0.35, 0.05, -0.35), f + new Vec3d(0.35, 1.8, 0.35)));
        }

        [Test]
        public void Senses_HearGunfireThroughWalls_RoughlyWhereItCameFrom()
        {
            NetServer s = OpenField();
            ServerPlayer me = s.AddBot(new Still(), 1, new Vec3d(20, 0, 10), 0);
            var shooter = new Fixed();
            shooter.Action[(int)MlActions.Head.Fire] = 1;
            ServerPlayer en = s.AddBot(new MlBrain(null, shooter, MlSkill.Normal, 9), 2, new Vec3d(20, 0, 40), 0);
            for (int t = 0; t < 30; t++) s.Step();
            var senses = new MlSenses(4);
            var skill = new MlSkill { ReactionTicks = 24 };
            bool heard = false;
            for (int t = 0; t < 240 && !heard; t++)
            {
                s.Step();
                senses.Update(s, me, Math.PI, skill); // facing away: only sound can tell
                if (senses.Known.Count > 0) { heard = true; Assert.That(senses.Known[0].Seen, Is.False); Assert.That((senses.Known[0].Pos - en.State.Position).HorizontalLength, Is.LessThan(5)); }
            }
            Assert.That(heard, "30 m away, facing away, a gunshot is heard");
        }

        // ------------------------------------------------------------------ hands

        [Test]
        public void Hands_TurnNoFasterThanTheSkillAllows_AndPressOnTheFirstTickOnly()
        {
            NetServer s = OpenField();
            var actor = new Fixed();
            actor.Action[(int)MlActions.Head.Yaw] = MlActions.Nearest(MlActions.YawSteps, 40);
            actor.Action[(int)MlActions.Head.Jump] = 1;
            var skill = new MlSkill { TurnDegPerSec = 240, AimNoiseDeg = 0 };
            var brain = new MlBrain(null, actor, skill, 5);
            ServerPlayer me = s.AddBot(brain, 1, new Vec3d(20, 0, 20), 0);
            for (int t = 0; t < 10; t++) s.Step();
            int presses = 0;
            double turned = 0, last = brain.Hands.Yaw;
            for (int t = 0; t < 120; t++)
            {
                s.Step();
                if (me.LastCmd.Move.JumpPressed) presses++;
                turned += MlSenses.Wrap(brain.Hands.Yaw - last) * 180 / Math.PI;
                last = brain.Hands.Yaw;
            }
            // The action asks for 40 degrees a decision (1200 deg/s); the hand turns 240 deg/s: 240 degrees in one second.
            Assert.That(turned, Is.EqualTo(240).Within(1), "capped at the skill's turn speed");
            Assert.That(presses, Is.EqualTo(120 / MlActions.DecisionTicks).Within(1), "a jump press once per decision, not held into a stream");
            Assert.That(actor.Calls, Is.EqualTo(130 / MlActions.DecisionTicks).Within(2), "30 decisions a second");
        }

        [Test]
        public void FromCommands_LabelsATeachersTurnAndKeys()
        {
            var cmds = new List<InputCmd>();
            for (int k = 0; k < 4; k++)
                cmds.Add(new InputCmd { Move = new MoveInput { MoveZ = 1, MoveX = -1, Yaw = 0.1 + (k + 1) * 1.5 * Math.PI / 180, SprintHeld = true, FireHeld = k == 2 }, Pitch = -1 * (k + 1) * 0.25f });
            var a = new int[MlActions.Heads];
            MlActions.FromCommands(0.1, 0, cmds, a);
            Assert.That(MlActions.YawSteps[a[(int)MlActions.Head.Yaw]], Is.EqualTo(6), "6 degrees right over the decision");
            Assert.That(MlActions.PitchSteps[a[(int)MlActions.Head.Pitch]], Is.EqualTo(-1), "1 degree up");
            MlActions.MoveOf(a[(int)MlActions.Head.Move], out int x, out int z);
            Assert.That((x, z), Is.EqualTo((-1, 1)));
            Assert.That(a[(int)MlActions.Head.Fire], Is.EqualTo(1));
            Assert.That(a[(int)MlActions.Head.Sprint], Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ network runtime

        [Test]
        public void Mlp_Forward_MatchesAHandWorkedNetwork()
        {
            var n = new MlMlp(new[] { 2, 2, 1 });
            n.W[0] = new[] { 1f, -1f, 0.5f, 0.25f }; n.B[0] = new[] { 0f, 0.1f };
            n.W[1] = new[] { 2f, -3f }; n.B[1] = new[] { 0.5f };
            var o = new float[1];
            n.Forward(new[] { 0.3f, -0.2f }, o, new float[2], new float[2]);
            double h0 = Math.Tanh(0.3 + 0.2), h1 = Math.Tanh(0.15 - 0.05 + 0.1);
            Assert.That(o[0], Is.EqualTo(2 * h0 - 3 * h1 + 0.5).Within(1e-6));
        }

        [Test]
        public void Model_SavesAndLoads_Exactly_AndRefusesJunkAndOtherLayouts()
        {
            MlModel m = RandomModel(3);
            m.Note = "test";
            var ms = new MemoryStream();
            m.Save(ms);
            ms.Position = 0;
            MlModel back = MlModel.Load(ms);
            Assert.That(back.Note, Is.EqualTo("test"));
            var obs = new float[MlObs.Size];
            var rng = new MlRng(4);
            for (int k = 0; k < obs.Length; k++) obs[k] = (float)rng.Gauss();
            var r1 = new MlPolicyRunner(m); var r2 = new MlPolicyRunner(back);
            var a1 = new int[MlActions.Heads]; var a2 = new int[MlActions.Heads];
            r1.Act(obs, 0, rng, a1); r2.Act(obs, 0, rng, a2);
            Assert.That(r2.Logits, Is.EqualTo(r1.Logits), "bit for bit the same network");

            byte[] bytes = ms.ToArray();
            bytes[8] ^= 0xFF; // the layout hash
            Assert.Throws<InvalidDataException>(() => MlModel.Load(new MemoryStream(bytes)));
            var junk = new byte[200];
            new Random(1).NextBytes(junk);
            Assert.Catch(() => MlModel.Load(new MemoryStream(junk)));
            for (int cut = 0; cut < 64; cut += 7)
                Assert.Catch(() => MlModel.Load(new MemoryStream(ms.ToArray(), 0, cut)), "a cut-short file is refused");
        }

        [Test]
        public void Sample_AtTemperatureZero_PicksTheLikeliest_AndAtOne_FollowsTheOdds()
        {
            var logits = new float[MlActions.TotalLogits];
            int off = MlActions.Offset((int)MlActions.Head.Fire);
            logits[off] = 0; logits[off + 1] = (float)Math.Log(3); // fire 75%
            var a = new int[MlActions.Heads];
            MlPolicyRunner.Sample(logits, 0, new MlRng(1), a);
            Assert.That(a[(int)MlActions.Head.Fire], Is.EqualTo(1));
            var rng = new MlRng(2);
            int fires = 0;
            for (int k = 0; k < 20000; k++) { MlPolicyRunner.Sample(logits, 1, rng, a); fires += a[(int)MlActions.Head.Fire]; }
            Assert.That(fires / 20000.0, Is.EqualTo(0.75).Within(0.015));
        }

        // ------------------------------------------------------------------ the brain on a real server

        [Test]
        public void Brain_PlaysTheDuelMap_WithARandomModel_SendingOnlyValidCommands_AndIsDeterministic()
        {
            ulong Run(out int decisions)
            {
                NetServer s = DuelServer(out BreachMapInfo info);
                var a = new MlBrain(Nav(), new MlModelActor(RandomModel(11), 11), MlSkill.Normal, 11);
                var b = new MlBrain(Nav(), new MlModelActor(RandomModel(12), 12), MlSkill.Hard, 12);
                ServerPlayer pa = s.AddBot(a, 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
                s.AddBot(b, 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
                decisions = 0;
                for (int t = 0; t < 20 * SimTime.TicksPerSecond; t++)
                {
                    s.Step();
                    if (a.DecidedThisTick) decisions++;
                    InputCmd c = pa.LastCmd;
                    Assert.That(InputCmd.Quantize(c).Move.Yaw, Is.EqualTo(c.Move.Yaw), "commands are already what the wire carries");
                }
                return MatchStream.StateHash(s);
            }
            ulong h1 = Run(out int d1), h2 = Run(out int d2);
            Assert.That(d1, Is.EqualTo(20 * SimTime.TicksPerSecond / MlActions.DecisionTicks).Within(5));
            Assert.That(h2, Is.EqualTo(h1), "same seeds, same match");
        }

        [Test]
        public void AMatchWithLearnedBots_ReRunsExactlyFromItsReplay()
        {
            NetServer s = DuelServer(out BreachMapInfo info);
            s.MatchConfig.Enabled = true;
            s.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
            var ms = new MemoryStream();
            MatchStream stream = s.StartStream();
            var writer = new ReplayWriter(ms, new ReplayHeader { Map = "Duel", Mode = "test", Build = "tests", StartedUtcTicks = 1 }, stream);
            s.AddBot(new MlBrain(Nav(), new MlModelActor(RandomModel(21), 21), MlSkill.Normal, 21), 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
            s.AddBot(new MlBrain(Nav(), new MlModelActor(RandomModel(22), 22), MlSkill.Normal, 22), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
            for (int t = 0; t < 30 * SimTime.TicksPerSecond; t++) s.Step();
            writer.Finish();
            ReplayFile f = ReplayFile.Read(ms.ToArray());
            ReplayCheck check = ReplayVerifier.Run(f, () =>
            {
                NetServer r = DuelServer(out _);
                r.MatchConfig.Enabled = true;
                r.MatchConfig.CountdownTicks = 60 * SimTime.Per60;
                return r;
            });
            TestContext.WriteLine(check.ToString());
            Assert.That(check.Passed, check.ToString());
            Assert.That(check.HashesChecked, Is.GreaterThanOrEqualTo(30 * 4 - 8), "four state hashes a second, all equal");
        }

        [Test]
        public void TeacherMode_LabelsWhatTheScriptedBotDid_AndPlaysExactlyLikeIt()
        {
            ulong Run(bool watch, out int labels)
            {
                NetServer s = DuelServer(out BreachMapInfo info);
                int n = 0;
                IBotBrain a = new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 5);
                if (watch) a = new MlBrain(Nav(), null, MlSkill.Normal, 5) { Teacher = a, OnLabelled = (b, o, act) => n++ };
                s.AddBot(a, 1, info.TeamSpawns[0][0], info.SpawnYaw[0]);
                s.AddBot(new DuelBot(Nav(), DuelBotConfig.For(BotLevel.Normal), 6), 2, info.TeamSpawns[1][0], info.SpawnYaw[1]);
                for (int t = 0; t < 15 * SimTime.TicksPerSecond; t++) s.Step();
                labels = n;
                return MatchStream.StateHash(s);
            }
            ulong plain = Run(false, out _), watched = Run(true, out int labels);
            Assert.That(watched, Is.EqualTo(plain), "watching changes nothing about the teacher's play");
            Assert.That(labels, Is.GreaterThan(15 * 30 - 30));
        }
    }
}
