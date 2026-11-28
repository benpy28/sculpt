using System;
using System.Diagnostics;
using Sculpt.Core;
using Sculpt.Core.ML;
using Sculpt.Core.Net;

namespace Sculpt.Trainer
{
    /// <summary>Where a training match's time goes (single thread): server tick, senses, observation, network.</summary>
    internal static class Profile
    {
        public static void Run(double seconds)
        {
            BotNav nav = DuelMap.BuildNav();
            MlModel m = MlModel.Fresh(new[] { 256, 256 }, 1);
            var e = new DuelEnv(nav, 1);
            e.Build(m, Opp.Self);
            // Warm up.
            e.Run(1200, () => Opp.Self, () => null);
            long obsT = 0, netT = 0, sensesT = 0;
            int obsN = 0;
            var x = new float[MlObs.Size];
            var sw = Stopwatch.StartNew();
            int ticks = 0;
            while (sw.Elapsed.TotalSeconds < seconds) { e.Run(120, () => Opp.Self, () => null); ticks += 120; foreach (var l in e.Learners) l.actor.Traj.Clear(); }
            double total = sw.Elapsed.TotalMilliseconds;
            // Pieces, measured on their own from the live state.
            var (brain, actor, p) = e.Learners[0];
            var t0 = Stopwatch.GetTimestamp();
            var ctx = brain.Context;
            for (int k = 0; k < 2000; k++) brain.Senses.Update(e.S, p, brain.Hands.Yaw, brain.Skill);
            sensesT = Stopwatch.GetTimestamp() - t0;
            t0 = Stopwatch.GetTimestamp();
            var goals = new MlObs.Goals();
            for (int k = 0; k < 2000; k++) { MlObs.Build(e.S, p, brain.Hands.Yaw, brain.Hands.Pitch, brain.Skill, ctx, goals, x); obsN++; }
            obsT = Stopwatch.GetTimestamp() - t0;
            var run = new MlPolicyRunner(m);
            var act = new int[MlActions.Heads];
            var rng = new MlRng(1);
            t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < 2000; k++) run.Act(x, 1, rng, act);
            netT = Stopwatch.GetTimestamp() - t0;
            double F(long t) => t * 1000.0 / Stopwatch.Frequency / 2000;
            Console.WriteLine($"{ticks} ticks in {total:0} ms: {total / ticks:0.000} ms per tick (2 learning bots)");
            Console.WriteLine($"per call: senses {F(sensesT):0.000} ms (every tick), observation {F(obsT):0.000} ms (every 4 ticks, routes not re-planned), policy {F(netT):0.000} ms");
        }
    }
}
