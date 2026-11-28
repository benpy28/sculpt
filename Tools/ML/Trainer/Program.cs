using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sculpt.Core;
using Sculpt.Core.ML;
using Sculpt.Core.Net;

namespace Sculpt.Trainer
{
    /// <summary>
    /// Command line:
    ///   bench [seconds]                         speed of the training matches (decisions a second)
    ///   bc out.sbm [samples] [epochs]           imitate the scripted bot (a starting point that can walk and aim)
    ///   ppo in.sbm outdir [iterations]          self-play PPO from a model, checkpoints and a log in outdir
    ///   eval model.sbm [minutes] [skill]        1v1s against the scripted bot at each level: kills each way
    ///   br in.sbm outdir [iterations] [bots]    battle royale PPO (placement reward), learning bots plus scripted fill
    ///   train outdir [iterations]               bc, then ppo, then eval
    /// Options: --threads N (default: cores - 4, at most 12).
    /// </summary>
    internal static class Program
    {
        private static int Threads = Math.Max(2, Math.Min(12, Environment.ProcessorCount - 4));
        private static readonly int[] Hidden = { 256, 256 };

        private static int Main(string[] args)
        {
            var a = new List<string>(args);
            int ti = a.IndexOf("--threads");
            if (ti >= 0) { Threads = int.Parse(a[ti + 1]); a.RemoveRange(ti, 2); }
            string cmd = a.Count > 0 ? a[0] : "help";
            string Arg(int i, string d) => a.Count > i ? a[i] : d;
            Console.WriteLine($"SCULPT trainer: {cmd}, {Threads} threads, observation {MlObs.Size} numbers, {MlActions.TotalLogits} action choices in {MlActions.Heads} heads");
            switch (cmd)
            {
                case "bench": Bench(D(Arg(1, "20"))); return 0;
                case "profile": Profile.Run(D(Arg(1, "10"))); return 0;
                case "check": return GradCheck.Run();
                case "bc": Bc(Arg(1, "bc.sbm"), int.Parse(Arg(2, "300000")), int.Parse(Arg(3, "12"))); return 0;
                case "ppo": Ppo(MlModel.Load(Arg(1, "bc.sbm")), Arg(2, "run"), int.Parse(Arg(3, "200")), MlModel.Load(Arg(1, "bc.sbm"))); return 0;
                case "br": Ppo(MlModel.Load(Arg(1, "latest.sbm")), Arg(2, "run-br"), int.Parse(Arg(3, "200")), MlModel.Load(Arg(1, "latest.sbm")), br: true, brLearners: int.Parse(Arg(4, "24"))); return 0;
                case "eval": Eval(MlModel.Load(Arg(1, "model.sbm")), D(Arg(2, "10")), Arg(3, "normal")); return 0;
                case "train":
                {
                    string dir = Arg(1, "run");
                    Directory.CreateDirectory(dir);
                    string bc = Path.Combine(dir, "bc.sbm");
                    if (!File.Exists(bc)) Bc(bc, 300000, 12);
                    MlModel m = MlModel.Load(bc);
                    MlModel final = Ppo(m, dir, int.Parse(Arg(2, "300")), MlModel.Load(bc));
                    Eval(final, 10, "normal");
                    return 0;
                }
                default:
                    Console.WriteLine("bench | bc out.sbm [samples] [epochs] | ppo in.sbm outdir [iterations] | eval model.sbm [minutes] [easy|normal|hard|pro] | train outdir [iterations]   (--threads N)");
                    return 1;
            }
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        private static BotNav[] _navs;
        private static BotNav[] Navs(int n)
        {
            if (_navs != null && _navs.Length >= n) return _navs;
            var sw = Stopwatch.StartNew();
            var navs = new BotNav[n];
            // Path search keeps scratch inside the nav: one per thread.
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Threads }, i => navs[i] = DuelMap.BuildNav());
            Console.WriteLine($"nav x{n} in {sw.ElapsedMilliseconds} ms");
            return _navs = navs;
        }

        // ================================================================ Speed

        private static void Bench(double seconds)
        {
            MlModel m = MlModel.Fresh(Hidden, 1);
            int n = Threads;
            BotNav[] navs = Navs(n);
            var envs = new DuelEnv[n];
            for (int i = 0; i < n; i++) { envs[i] = new DuelEnv(navs[i], 100 + i); envs[i].Build(m, Opp.Self); }
            var sw = Stopwatch.StartNew();
            long ticks = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, i => { envs[i].Run(480, () => Opp.Self, () => null); foreach (var l in envs[i].Learners) l.actor.Traj.Clear(); });
                ticks += 480L * n;
            }
            double s = sw.Elapsed.TotalSeconds;
            long dec = envs.Sum(e => e.Decisions);
            Console.WriteLine($"{n} matches: {ticks / s:0} server ticks/s ({ticks / s / SimTime.TicksPerSecond:0.0} game seconds per second), {dec / s:0} decisions/s ({dec / s / n:0} per thread)");
        }

        // ================================================================ Imitation

        private static void Bc(string outPath, int samples, int epochs)
        {
            Console.WriteLine($"behaviour cloning: collecting {samples} decisions of the scripted bot playing itself");
            var sw = Stopwatch.StartNew();
            int n = Threads;
            BotNav[] navs = Navs(n);
            var per = new List<(float[] obs, int[] act)>[n];
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, i =>
            {
                var list = per[i] = new List<(float[], int[])>();
                var rng = new MlRng((ulong)(i + 1) * 977);
                while (list.Count < samples / n)
                {
                    NetServer s = new NetServer(DuelMap.Terrain(DuelMap.BuildGround(), DuelMap.BuildBrushes(out BreachMapInfo info)), new MovementConfig(), new NetConfig(), (c, d, r) => { });
                    TileWalls.Find(s.Grid)?.Prepare();
                    foreach (GroundItem g in info.ItemSpawns) s.SpawnItem(g.Kind, g.Count, g.Position);
                    for (int side = 0; side < 2; side++)
                    {
                        var lv = (BotLevel)rng.Next(3);
                        var watcher = new MlBrain(navs[i], null, MlSkill.Random(rng), rng.Next())
                        {
                            Teacher = new DuelBot(navs[i], DuelBotConfig.For(lv), (int)rng.Next()),
                        };
                        // The skill the watcher reports is the teacher's level (so the model ties dials to play).
                        watcher.Skill = lv == BotLevel.Easy ? MlSkill.Easy : lv == BotLevel.Hard ? MlSkill.Hard : MlSkill.Normal;
                        watcher.OnLabelled = (b, o, act) => list.Add(((float[])o.Clone(), (int[])act.Clone()));
                        s.AddBot(watcher, side + 1, info.TeamSpawns[side][0], info.SpawnYaw[side]);
                    }
                    for (int t = 0; t < 150 * SimTime.TicksPerSecond && list.Count < samples / n; t++) s.Step();
                }
            });
            var all = per.SelectMany(x => x).ToList();
            Console.WriteLine($"collected {all.Count} in {sw.Elapsed.TotalSeconds:0.0} s");
            LogLabels(all.Select(x => x.act));

            MlModel m = MlModel.Fresh(Hidden, 7);
            SetNormalizer(m, all.Select(x => x.obs));
            var b = new Batch();
            b.Reserve(all.Count);
            b.N = all.Count;
            var scaled = new float[MlObs.Size];
            for (int k = 0; k < all.Count; k++)
            {
                m.Normalize(all[k].obs, scaled);
                Array.Copy(scaled, 0, b.Obs, k * MlObs.Size, MlObs.Size);
                Array.Copy(all[k].act, 0, b.Act, k * MlActions.Heads, MlActions.Heads);
            }
            all.Clear();
            var learner = new Learner(m, Threads, 4096) { Lr = 1e-3 };
            var rng2 = new MlRng(5);
            sw.Restart();
            learner.Clone(b, epochs, 4096, rng2, (ep, st) =>
            {
                learner.Lr = 1e-3 * (1 - (ep + 1.0) / epochs) + 1e-4;
                Console.WriteLine($"  epoch {ep + 1}/{epochs}: loss {st.Policy / st.N:0.000} per decision, head accuracy {st.Acc / st.N:P1}, {sw.Elapsed.TotalSeconds:0} s");
            });
            m.Note = $"behaviour cloning of the scripted duel bot, {b.N} decisions, {epochs} epochs, {DateTime.Now:yyyy-MM-dd HH:mm}";
            m.Save(outPath);
            Console.WriteLine($"saved {outPath}");
        }

        private static void LogLabels(IEnumerable<int[]> acts)
        {
            var counts = new int[MlActions.TotalLogits];
            int n = 0;
            foreach (int[] a in acts) { n++; for (int h = 0; h < MlActions.Heads; h++) counts[MlActions.Offset(h) + a[h]]++; }
            for (int h = 0; h < MlActions.Heads; h++)
            {
                var parts = new List<string>();
                for (int k = 0; k < MlActions.HeadSizes[h]; k++) parts.Add($"{100.0 * counts[MlActions.Offset(h) + k] / Math.Max(1, n):0}");
                Console.WriteLine($"  {(MlActions.Head)h,-7} {string.Join(" ", parts)} %");
            }
        }

        private static void SetNormalizer(MlModel m, IEnumerable<float[]> obs)
        {
            var sum = new double[MlObs.Size]; var sq = new double[MlObs.Size]; long n = 0;
            foreach (float[] o in obs) { n++; for (int k = 0; k < MlObs.Size; k++) { sum[k] += o[k]; sq[k] += (double)o[k] * o[k]; } }
            for (int k = 0; k < MlObs.Size; k++)
            {
                double mean = sum[k] / Math.Max(1, n);
                double var = sq[k] / Math.Max(1, n) - mean * mean;
                m.Mean[k] = (float)mean;
                m.Std[k] = (float)Math.Max(0.05, Math.Sqrt(Math.Max(0, var)));
            }
        }

        // ================================================================ Self-play

        private static MlModel Ppo(MlModel m, string dir, int iterations, MlModel reference, bool br = false, int brLearners = 16)
        {
            Directory.CreateDirectory(dir);
            var rng = new MlRng(77);
            var pool = new List<MlModel> { m.Clone() };
            Opp Pick()
            {
                lock (rng)
                {
                    double u = rng.NextDouble();
                    return u < 0.4 ? Opp.Self : u < 0.6 ? Opp.Snapshot : u < 0.7 ? Opp.ScriptEasy : u < 0.85 ? Opp.ScriptNormal : Opp.ScriptHard;
                }
            }
            MlModel PickSnap() { lock (pool) lock (rng) return pool[pool.Count - 1 - (int)(Math.Pow(rng.NextDouble(), 2) * pool.Count)]; }
            var envs = new List<ITrainEnv>();
            int ticksPerIter;
            if (br)
            {
                // A battle royale server is big (a 614 m map): a few of them, each with many learning bots.
                int n = Math.Max(1, Threads / 4);
                var made = new BrEnv[n];
                Parallel.For(0, n, i => made[i] = new BrEnv(m, brLearners, BrMode.Solos, 300 + i));
                envs.AddRange(made);
                ticksPerIter = 1024;
                Console.WriteLine($"{n} battle royale servers, {brLearners} learning bots each");
            }
            else
            {
                int n = Math.Max(Threads * 2, 8);
                BotNav[] navs = Navs(n);
                for (int i = 0; i < n; i++)
                {
                    var e = new DuelEnv(navs[i], 500 + i) { PickOpp = Pick, PickSnapshot = PickSnap };
                    Opp o = (Opp)(i % 5);
                    e.Build(m, o, o == Opp.Snapshot ? pool[0] : null);
                    envs.Add(e);
                }
                ticksPerIter = 2048;
            }
            var learner = new Learner(m, Threads, 4096) { Reference = reference };
            var log = new StreamWriter(Path.Combine(dir, "log.csv"), append: true) { AutoFlush = true };
            log.WriteLine("iter,seconds,decisions,samples,stats,policy,value,entropy,kl,clip,refkl");
            var sw = Stopwatch.StartNew();
            var batch = new Batch();
            var trajs = new List<Traj>();
            long totalDec = 0;
            var stats = new Dictionary<string, double>();
            for (int it = 1; it <= iterations; it++)
            {
                learner.Lr = 3e-4 * Math.Max(0.1, 1 - (it - 1.0) / iterations);
                Parallel.ForEach(envs, new ParallelOptions { MaxDegreeOfParallelism = Threads }, e => e.Run(ticksPerIter));
                trajs.Clear();
                foreach (ITrainEnv e in envs) { trajs.AddRange(e.Trajs); totalDec += e.TakeDecisions(); e.TakeStats(stats); }
                Batch.From(trajs, br ? 0.998 : 0.995, 0.95, batch);
                LossStats st = batch.N > 256 ? learner.Ppo(batch, 3, 4096, rng) : default;
                foreach (Traj t in trajs) t.Clear();
                if (it % 10 == 0 || it == iterations)
                {
                    string line = br ? BrLine(stats) : DuelLine(stats);
                    Console.WriteLine($"iter {it}/{iterations} {sw.Elapsed.TotalMinutes:0.0} min, {totalDec / sw.Elapsed.TotalSeconds:0} dec/s, batch {batch.N}; {line}; {st}");
                    log.WriteLine(string.Join(",", it, sw.Elapsed.TotalSeconds.ToString("0"), totalDec, batch.N, line.Replace(",", ";"),
                        F(st.Policy / Math.Max(1, st.N)), F(st.Value / Math.Max(1, st.N)), F(st.Entropy / Math.Max(1, st.N)), F(st.Kl / Math.Max(1, st.N)), F(st.ClipFrac / Math.Max(1, st.N)), F(st.Bc / Math.Max(1, st.N))));
                    stats.Clear();
                }
                if (it % 25 == 0 || it == iterations)
                {
                    m.Note = $"PPO {(br ? "battle royale" : "1v1")} from: {reference.Note}; iteration {it}, {totalDec} decisions";
                    m.Save(Path.Combine(dir, $"ckpt-{it:0000}.sbm"));
                    m.Save(Path.Combine(dir, "latest.sbm"));
                    lock (pool) { pool.Add(m.Clone()); if (pool.Count > 12) pool.RemoveAt(1); }
                }
            }
            log.Dispose();
            return m;
        }

        private static string DuelLine(Dictionary<string, double> s)
        {
            string K(string o)
            {
                double k = s.TryGetValue("k_" + o, out double a) ? a : 0, d = s.TryGetValue("d_" + o, out double b) ? b : 0;
                return k + d == 0 ? "-" : $"{k / (k + d):P0} of {k + d}";
            }
            return $"kill share: self {K("self")} snap {K("snapshot")} easy {K("scripteasy")} normal {K("scriptnormal")} hard {K("scripthard")}";
        }

        private static string BrLine(Dictionary<string, double> s)
        {
            double V(string k) => s.TryGetValue(k, out double v) ? v : 0;
            double n = V("br_place_n");
            return n == 0 ? "no placements yet" : $"average place {V("br_place_sum") / n:0.0} of 32 over {n} runs, wins {V("br_wins")}, kills {V("br_kills")}";
        }

        private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

        // ================================================================ Rating

        private static void Eval(MlModel m, double minutes, string skillName)
        {
            MlSkill skill = skillName == "easy" ? MlSkill.Easy : skillName == "hard" ? MlSkill.Hard : skillName == "pro" ? MlSkill.Pro : MlSkill.Normal;
            int n = Threads;
            BotNav[] navs = Navs(n);
            Console.WriteLine($"eval: {minutes} game minutes per level, model skill {skillName}");
            foreach (Opp o in new[] { Opp.ScriptEasy, Opp.ScriptNormal, Opp.ScriptHard })
            {
                int k = 0, d = 0;
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, i =>
                {
                    var e = new DuelEnv(navs[i], 9000 + i) { RandomSkill = false, FixedSkill = skill, ResetTicks = int.MaxValue };
                    e.Build(m, o);
                    int ticks = (int)(minutes * 60 * SimTime.TicksPerSecond / n);
                    for (int done = 0; done < ticks; done += 1200) { e.Run(Math.Min(1200, ticks - done), () => o, () => null); foreach (var l in e.Learners) l.actor.Traj.Clear(); }
                    lock (navs) { k += e.Kills; d += e.Deaths; }
                });
                Console.WriteLine($"  vs scripted {o,-12}: {k} kills, {d} deaths, share of kills {(k + d == 0 ? 0 : (double)k / (k + d)):P0}");
            }
        }
    }
}
