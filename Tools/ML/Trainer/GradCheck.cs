using System;
using Sculpt.Core.ML;

namespace Sculpt.Trainer
{
    /// <summary>
    /// Checks the hand-written backward passes against finite differences: imitation loss, and the whole PPO loss
    /// (clipped surrogate away from the clip edges, entropy bonus, pull toward the reference policy, value loss).
    /// Run with "check"; exits non-zero if any gradient is off.
    /// </summary>
    internal static class GradCheck
    {
        public static int Run()
        {
            bool ok = true;
            ok &= Case(ppo: false, withRef: false);
            ok &= Case(ppo: true, withRef: false);
            ok &= Case(ppo: true, withRef: true);
            Console.WriteLine(ok ? "gradient check: all good" : "gradient check: FAILED");
            return ok ? 0 : 1;
        }

        private static bool Case(bool ppo, bool withRef)
        {
            var rng = new MlRng(ppo ? (withRef ? 3UL : 2UL) : 1UL);
            MlModel m = MlModel.Fresh(new[] { 12, 9 }, 5);
            // Bigger last-layer weights so the softmaxes are not flat (a flat start hides mistakes).
            foreach (float[] w in m.Policy.W) for (int i = 0; i < w.Length; i++) w[i] = (float)(rng.Gauss() * 0.4);
            int n = 6;
            var b = new Batch();
            b.Reserve(n); b.N = n;
            for (int i = 0; i < n * MlObs.Size; i++) b.Obs[i] = (float)rng.Gauss();
            var logits = new float[MlActions.TotalLogits];
            var row = new float[MlObs.Size];
            var sa = new float[64]; var sb = new float[64];
            var act = new int[MlActions.Heads];
            var adv = new float[n];
            for (int k = 0; k < n; k++)
            {
                for (int h = 0; h < MlActions.Heads; h++) { act[h] = rng.Next(MlActions.HeadSizes[h]); b.Act[k * MlActions.Heads + h] = act[h]; }
                Array.Copy(b.Obs, k * MlObs.Size, row, 0, MlObs.Size);
                m.Policy.Forward(row, logits, sa, sb);
                b.OldLogP[k] = LearnActor.LogProb(logits, act) + (float)((rng.NextDouble() - 0.5) * 0.2); // ratio within the clip
                b.Ret[k] = (float)rng.Gauss();
                adv[k] = (float)rng.Gauss();
            }
            var learner = new Learner(m, 1, n) { EntropyCoef = 0.05, RefCoef = 0.3 };
            if (withRef)
            {
                MlModel r = m.Clone();
                foreach (float[] w in r.Policy.W) for (int i = 0; i < w.Length; i++) w[i] += (float)(rng.Gauss() * 0.3);
                learner.Reference = r;
            }
            learner.Probe(b, adv, n, ppo);
            // Copy the analytic gradients before probing again.
            var gp = Copy(learner.PolicyGrad.GW);
            var gv = Copy(learner.ValueGrad.GW);
            double worst = 0;
            int checks = 0;
            void Test(MlMlp net, float[][] g)
            {
                for (int c = 0; c < 25; c++)
                {
                    int l = rng.Next(net.W.Length);
                    int i = rng.Next(net.W[l].Length);
                    float keep = net.W[l][i];
                    const float eps = 2e-3f;
                    net.W[l][i] = keep + eps; double up = learner.Probe(b, adv, n, ppo);
                    net.W[l][i] = keep - eps; double dn = learner.Probe(b, adv, n, ppo);
                    net.W[l][i] = keep;
                    double num = (up - dn) / (2 * eps), ana = g[l][i];
                    double err = Math.Abs(num - ana) / Math.Max(1e-3, Math.Abs(num) + Math.Abs(ana));
                    worst = Math.Max(worst, err);
                    checks++;
                }
            }
            Test(m.Policy, gp);
            if (ppo) Test(m.Value, gv);
            bool good = worst < 0.02;
            Console.WriteLine($"  {(ppo ? "PPO" : "imitation")}{(withRef ? " + reference pull" : "")}: {checks} weights, worst relative error {worst:0.00000} {(good ? "ok" : "BAD")}");
            return good;
        }

        private static float[][] Copy(float[][] a)
        {
            var c = new float[a.Length][];
            for (int i = 0; i < a.Length; i++) c[i] = (float[])a[i].Clone();
            return c;
        }
    }
}
