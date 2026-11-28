using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sculpt.Core.ML;

namespace Sculpt.Trainer
{
    /// <summary>A flat batch of steps (observations already scaled).</summary>
    public sealed class Batch
    {
        public int N;
        public float[] Obs = Array.Empty<float>();
        public int[] Act = Array.Empty<int>();
        public float[] OldLogP = Array.Empty<float>(), Adv = Array.Empty<float>(), Ret = Array.Empty<float>(), OldVal = Array.Empty<float>();

        public void Reserve(int n)
        {
            if (Obs.Length < n * MlObs.Size)
            {
                Obs = new float[n * MlObs.Size]; Act = new int[n * MlActions.Heads];
                OldLogP = new float[n]; Adv = new float[n]; Ret = new float[n]; OldVal = new float[n];
            }
        }

        /// <summary>Gathers every closed step of the trajectories, with advantages (GAE) and returns.</summary>
        public static Batch From(IList<Traj> trajs, double gamma, double lambda, Batch into = null)
        {
            var b = into ?? new Batch();
            int n = 0;
            foreach (Traj t in trajs) n += t.Closed;
            b.Reserve(n);
            b.N = n;
            int i = 0;
            foreach (Traj t in trajs)
            {
                int m = t.Closed;
                if (m == 0) continue;
                double next = t.Open ? t.Val[t.Val.Count - 1] : 0; // the open step's value bootstraps the rest
                double gae = 0;
                for (int k = m - 1; k >= 0; k--)
                {
                    bool done = t.Done[k];
                    double nv = done ? 0 : (k == m - 1 ? next : t.Val[k + 1]);
                    double delta = t.Rew[k] + gamma * nv - t.Val[k];
                    gae = delta + (done ? 0 : gamma * lambda * gae);
                    b.Adv[i + k] = (float)gae;
                    b.Ret[i + k] = (float)(gae + t.Val[k]);
                    b.OldVal[i + k] = t.Val[k];
                    b.OldLogP[i + k] = t.LogP[k];
                    Array.Copy(t.Obs[k], 0, b.Obs, (i + k) * MlObs.Size, MlObs.Size);
                    Array.Copy(t.Act[k], 0, b.Act, (i + k) * MlActions.Heads, MlActions.Heads);
                }
                i += m;
            }
            return b;
        }
    }

    /// <summary>Per-sample loss pieces, summed over a minibatch.</summary>
    public struct LossStats
    {
        public double Policy, Value, Entropy, Kl, ClipFrac, Bc, Acc;
        public int N;
        public void Add(LossStats o) { Policy += o.Policy; Value += o.Value; Entropy += o.Entropy; Kl += o.Kl; ClipFrac += o.ClipFrac; Bc += o.Bc; Acc += o.Acc; N += o.N; }
        public override string ToString() => N == 0 ? "-" :
            $"pol {Policy / N:0.0000} val {Value / N:0.000} ent {Entropy / N:0.000} kl {Kl / N:0.0000} clip {ClipFrac / N:0.00} bcKL {Bc / N:0.000}";
    }

    /// <summary>
    /// PPO (clipped) on the policy and value networks, plus an optional pull toward a reference policy (the imitation
    /// model) so self-play doesn't drift into play no person would recognise. Also does plain behaviour cloning.
    /// </summary>
    public sealed class Learner
    {
        public readonly MlModel Model;
        private readonly TrainMlp _pol, _val;
        private readonly Adam _polOpt, _valOpt;
        public MlModel Reference; // the imitation policy (pull toward it)
        public double Clip = 0.2, ValueCoef = 0.5, EntropyCoef = 0.003, RefCoef = 0.02, MaxGradNorm = 0.5;
        public int Threads;
        private readonly int _chunk;
        private readonly TrainMlp.Work[] _pw, _vw, _rw;
        private TrainMlp _refNet;

        public Learner(MlModel model, int threads, int minibatch)
        {
            Model = model;
            Threads = Math.Max(1, threads);
            _pol = new TrainMlp(model.Policy);
            _val = new TrainMlp(model.Value);
            _polOpt = new Adam(_pol);
            _valOpt = new Adam(_val);
            _chunk = (minibatch + Threads - 1) / Threads;
            _pw = new TrainMlp.Work[Threads]; _vw = new TrainMlp.Work[Threads]; _rw = new TrainMlp.Work[Threads];
            for (int t = 0; t < Threads; t++) { _pw[t] = new TrainMlp.Work(model.Policy, _chunk); _vw[t] = new TrainMlp.Work(model.Value, _chunk); }
        }

        public double Lr { set { _polOpt.Lr = value; _valOpt.Lr = value; } }

        /// <summary>PPO epochs over a batch. Returns the averaged losses of the last epoch.</summary>
        public LossStats Ppo(Batch b, int epochs, int minibatch, MlRng rng)
        {
            // Normalise advantages over the batch.
            double mean = 0, sq = 0;
            for (int i = 0; i < b.N; i++) mean += b.Adv[i];
            mean /= Math.Max(1, b.N);
            for (int i = 0; i < b.N; i++) sq += (b.Adv[i] - mean) * (b.Adv[i] - mean);
            double sd = Math.Sqrt(sq / Math.Max(1, b.N)) + 1e-8;
            var adv = new float[b.N];
            for (int i = 0; i < b.N; i++) adv[i] = (float)((b.Adv[i] - mean) / sd);
            if (Reference != null) { _refNet ??= new TrainMlp(Reference.Policy); for (int t = 0; t < Threads; t++) _rw[t] ??= new TrainMlp.Work(Reference.Policy, _chunk); }

            var order = new int[b.N];
            for (int i = 0; i < b.N; i++) order[i] = i;
            LossStats last = default;
            for (int ep = 0; ep < epochs; ep++)
            {
                Shuffle(order, rng);
                last = default;
                for (int s = 0; s < b.N; s += minibatch)
                {
                    int m = Math.Min(minibatch, b.N - s);
                    if (m < minibatch / 4 && s > 0) break; // a small tail: left for the next epoch's shuffle
                    last.Add(Step(b, adv, order, s, m, ppo: true));
                }
            }
            return last;
        }

        /// <summary>Behaviour cloning epochs on (observation, action) pairs (obs already scaled).</summary>
        public LossStats Clone(Batch b, int epochs, int minibatch, MlRng rng, Action<int, LossStats> report = null)
        {
            var order = new int[b.N];
            for (int i = 0; i < b.N; i++) order[i] = i;
            LossStats last = default;
            for (int ep = 0; ep < epochs; ep++)
            {
                Shuffle(order, rng);
                last = default;
                for (int s = 0; s < b.N; s += minibatch)
                {
                    int m = Math.Min(minibatch, b.N - s);
                    if (m < minibatch / 4 && s > 0) break; // a small tail: left for the next epoch's shuffle
                    last.Add(Step(b, null, order, s, m, ppo: false));
                }
                report?.Invoke(ep, last);
            }
            return last;
        }

        private LossStats Step(Batch b, float[] adv, int[] order, int start, int m, bool ppo)
        {
            _pol.ZeroGrad(); _val.ZeroGrad();
            var stats = new LossStats[Threads];
            int per = (m + Threads - 1) / Threads;
            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
            {
                int a = t * per, n = Math.Min(per, m - a);
                if (n <= 0) return;
                stats[t] = Chunk(b, adv, order, start + a, n, t, m, ppo);
            });
            var total = new LossStats();
            foreach (var s in stats) total.Add(s);
            for (int t = 0; t < Threads; t++) { _pol.AddGrad(_pw[t]); if (ppo) _val.AddGrad(_vw[t]); }
            Clip_(_pol); _polOpt.Step();
            if (ppo) { Clip_(_val); _valOpt.Step(); }
            return total;
        }

        private void Clip_(TrainMlp n)
        {
            double norm = Math.Sqrt(n.GradNormSq());
            if (norm > MaxGradNorm) n.ScaleGrad((float)(MaxGradNorm / norm));
        }

        /// <summary>
        /// For the gradient check: the whole loss over the first n rows (as the update defines it, divided by n), with
        /// its gradients left in the first thread's buffers (<see cref="PolicyGrad"/>, <see cref="ValueGrad"/>).
        /// </summary>
        public double Probe(Batch b, float[] adv, int n, bool ppo)
        {
            if (Reference != null) { _refNet ??= new TrainMlp(Reference.Policy); _rw[0] ??= new TrainMlp.Work(Reference.Policy, _chunk); }
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            LossStats st = Chunk(b, adv, order, 0, n, 0, n, ppo);
            if (!ppo) return st.Policy / n;
            return st.Policy / n - EntropyCoef * st.Entropy / n + (Reference != null ? RefCoef * st.Bc / n : 0) + ValueCoef * st.Value / n;
        }

        public TrainMlp.Work PolicyGrad => _pw[0];
        public TrainMlp.Work ValueGrad => _vw[0];

        private LossStats Chunk(Batch b, float[] adv, int[] order, int start, int n, int t, int m, bool ppo)
        {
            var st = new LossStats { N = n };
            int D = MlObs.Size, H = MlActions.Heads, L = MlActions.TotalLogits;
            TrainMlp.Work pw = _pw[t], vw = _vw[t], rw = _rw[t];
            pw.ZeroGrad(); vw.ZeroGrad();
            for (int k = 0; k < n; k++)
            {
                int idx = order[start + k];
                Array.Copy(b.Obs, idx * D, pw.Act[0], k * D, D);
            }
            _pol.Forward(pw, n);
            bool useRef = Reference != null && (ppo ? RefCoef > 0 : false);
            if (useRef) { Array.Copy(pw.Act[0], rw.Act[0], n * D); _refNet.Forward(rw, n); }
            float[] logits = pw.Act[pw.Act.Length - 1];
            float[] dLog = pw.Delta[pw.Delta.Length - 1];
            float invM = 1f / m;
            var p = new double[32];
            var q = new double[32];
            for (int k = 0; k < n; k++)
            {
                int idx = order[start + k];
                int lo = k * L;
                double lp = 0, ent = 0;
                int off = 0;
                // Per head: softmax, log-prob of the action taken, entropy.
                for (int h = 0; h < H; h++)
                {
                    int sz = MlActions.HeadSizes[h];
                    double max = double.MinValue;
                    for (int j = 0; j < sz; j++) max = Math.Max(max, logits[lo + off + j]);
                    double sum = 0;
                    for (int j = 0; j < sz; j++) { p[j] = Math.Exp(logits[lo + off + j] - max); sum += p[j]; }
                    double lsum = Math.Log(sum);
                    int a = b.Act[idx * H + h];
                    lp += logits[lo + off + a] - max - lsum;
                    double hh = 0;
                    for (int j = 0; j < sz; j++) { p[j] /= sum; if (p[j] > 1e-12) hh -= p[j] * Math.Log(p[j]); }
                    ent += hh;
                    if (!ppo)
                    {
                        // Cross-entropy with the teacher's action.
                        int best = 0;
                        for (int j = 0; j < sz; j++) { dLog[lo + off + j] = (float)(p[j] * invM); if (p[j] > p[best]) best = j; }
                        dLog[lo + off + a] -= invM;
                        st.Acc += best == a ? 1.0 / H : 0;
                    }
                    else
                    {
                        // Entropy bonus gradient (filled now; the policy term is added after the ratio is known).
                        for (int j = 0; j < sz; j++)
                        {
                            double lpj = Math.Log(Math.Max(p[j], 1e-12));
                            dLog[lo + off + j] = (float)(EntropyCoef * p[j] * (lpj + hh) * invM);
                        }
                        if (useRef)
                        {
                            float[] rl = rw.Act[rw.Act.Length - 1];
                            double rmax = double.MinValue, rsum = 0;
                            for (int j = 0; j < sz; j++) rmax = Math.Max(rmax, rl[lo + off + j]);
                            for (int j = 0; j < sz; j++) { q[j] = Math.Exp(rl[lo + off + j] - rmax); rsum += q[j]; }
                            double kl = 0;
                            for (int j = 0; j < sz; j++)
                            {
                                q[j] /= rsum;
                                if (q[j] > 1e-12) kl += q[j] * (Math.Log(q[j]) - Math.Log(Math.Max(p[j], 1e-12)));
                                dLog[lo + off + j] += (float)(RefCoef * (p[j] - q[j]) * invM);
                            }
                            st.Bc += kl;
                        }
                        // Remember p for the policy term: store temporarily in q-free way by recomputing below.
                    }
                    off += sz;
                }
                st.Entropy += ent;
                if (!ppo) { st.Policy += -lp; continue; }

                double ratio = Math.Exp(lp - b.OldLogP[idx]);
                double A = adv[idx];
                double s1 = ratio * A, s2 = Math.Max(1 - Clip, Math.Min(1 + Clip, ratio)) * A;
                st.Policy += -Math.Min(s1, s2);
                st.Kl += b.OldLogP[idx] - lp;
                bool clipped = (A > 0 && ratio > 1 + Clip) || (A < 0 && ratio < 1 - Clip);
                if (clipped) { st.ClipFrac += 1; continue; }
                // d(-ratio*A)/dlogit = -A * ratio * (onehot - p), per head.
                double g = -A * ratio * invM;
                off = 0;
                for (int h = 0; h < H; h++)
                {
                    int sz = MlActions.HeadSizes[h];
                    double max = double.MinValue;
                    for (int j = 0; j < sz; j++) max = Math.Max(max, logits[lo + off + j]);
                    double sum = 0;
                    for (int j = 0; j < sz; j++) { p[j] = Math.Exp(logits[lo + off + j] - max); sum += p[j]; }
                    int a = b.Act[idx * H + h];
                    for (int j = 0; j < sz; j++) dLog[lo + off + j] += (float)(g * ((j == a ? 1 : 0) - p[j] / sum));
                    off += sz;
                }
            }
            _pol.Backward(pw, n);
            if (!ppo) return st;

            // Value: 0.5 (v - ret)^2.
            for (int k = 0; k < n; k++) Array.Copy(b.Obs, order[start + k] * D, vw.Act[0], k * D, D);
            _val.Forward(vw, n);
            float[] v = vw.Act[vw.Act.Length - 1], dv = vw.Delta[vw.Delta.Length - 1];
            for (int k = 0; k < n; k++)
            {
                int idx = order[start + k];
                double e = v[k] - b.Ret[idx];
                st.Value += 0.5 * e * e;
                dv[k] = (float)(ValueCoef * e * invM);
            }
            _val.Backward(vw, n);
            return st;
        }

        private static void Shuffle(int[] a, MlRng rng)
        {
            for (int i = a.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        }
    }
}
