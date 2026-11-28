using System;
using System.Numerics;
using System.Threading.Tasks;
using Sculpt.Core.ML;

namespace Sculpt.Trainer
{
    /// <summary>
    /// The training half of <see cref="MlMlp"/>: the same network (tanh hidden layers, linear output) with a batched
    /// forward pass that keeps its activations, a backward pass, and gradients. Its weights are the runtime network's own
    /// arrays, so what trains is exactly what the game runs.
    /// </summary>
    public sealed class TrainMlp
    {
        public readonly MlMlp Net;
        public readonly float[][] GW, GB;

        public TrainMlp(MlMlp net)
        {
            Net = net;
            GW = new float[net.W.Length][];
            GB = new float[net.B.Length][];
            for (int l = 0; l < net.W.Length; l++) { GW[l] = new float[net.W[l].Length]; GB[l] = new float[net.B[l].Length]; }
        }

        public int Layers => Net.W.Length;

        /// <summary>Per-thread buffers for a batch (activations per layer, gradients by layer).</summary>
        public sealed class Work
        {
            public readonly float[][] Act; // Act[0] = input copy reference, Act[l+1] = layer l output
            public readonly float[][] Delta;
            public readonly float[][] GW, GB;
            public readonly int Cap;
            public Work(MlMlp net, int cap)
            {
                Cap = cap;
                int L = net.W.Length;
                Act = new float[L + 1][];
                Delta = new float[L + 1][];
                for (int l = 0; l <= L; l++) { Act[l] = new float[cap * net.Sizes[l]]; Delta[l] = new float[cap * net.Sizes[l]]; }
                GW = new float[L][]; GB = new float[L][];
                for (int l = 0; l < L; l++) { GW[l] = new float[net.W[l].Length]; GB[l] = new float[net.B[l].Length]; }
            }
            public void ZeroGrad() { foreach (var g in GW) Array.Clear(g); foreach (var g in GB) Array.Clear(g); }
        }

        /// <summary>Forward for rows [0, n) of <paramref name="x"/> (already in Act[0]); outputs in Act[L].</summary>
        public void Forward(Work w, int n)
        {
            MlMlp net = Net;
            for (int l = 0; l < net.W.Length; l++)
            {
                int nin = net.Sizes[l], nout = net.Sizes[l + 1];
                float[] W = net.W[l], B = net.B[l], src = w.Act[l], dst = w.Act[l + 1];
                bool last = l == net.W.Length - 1;
                for (int b = 0; b < n; b++)
                {
                    var xs = new ReadOnlySpan<float>(src, b * nin, nin);
                    for (int o = 0; o < nout; o++)
                    {
                        float acc = B[o] + Dot(new ReadOnlySpan<float>(W, o * nin, nin), xs);
                        dst[b * nout + o] = last ? acc : MathF.Tanh(acc);
                    }
                }
            }
        }

        /// <summary>Backward from dLoss/dOutput (in Delta[L], rows [0, n)); accumulates into w.GW/GB.</summary>
        public void Backward(Work w, int n)
        {
            MlMlp net = Net;
            for (int l = net.W.Length - 1; l >= 0; l--)
            {
                int nin = net.Sizes[l], nout = net.Sizes[l + 1];
                float[] W = net.W[l], src = w.Act[l], dOut = w.Delta[l + 1], dIn = w.Delta[l], gW = w.GW[l], gB = w.GB[l];
                bool hidden = l < net.W.Length - 1;
                if (hidden)
                {
                    // Through tanh: d = d * (1 - y^2), y = this layer's output.
                    float[] y = w.Act[l + 1];
                    for (int k = 0; k < n * nout; k++) dOut[k] *= 1 - y[k] * y[k];
                }
                bool needIn = l > 0;
                if (needIn) Array.Clear(dIn, 0, n * nin);
                for (int b = 0; b < n; b++)
                {
                    var xs = new ReadOnlySpan<float>(src, b * nin, nin);
                    Span<float> di = needIn ? new Span<float>(dIn, b * nin, nin) : Span<float>.Empty;
                    for (int o = 0; o < nout; o++)
                    {
                        float g = dOut[b * nout + o];
                        if (g == 0) continue;
                        gB[o] += g;
                        Axpy(g, xs, new Span<float>(gW, o * nin, nin));
                        if (needIn) Axpy(g, new ReadOnlySpan<float>(W, o * nin, nin), di);
                    }
                }
            }
        }

        public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            int n = a.Length, i = 0;
            float s = 0;
            if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
            {
                var acc = Vector<float>.Zero;
                int vn = n - n % Vector<float>.Count;
                for (; i < vn; i += Vector<float>.Count) acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
                s = Vector.Dot(acc, Vector<float>.One);
            }
            for (; i < n; i++) s += a[i] * b[i];
            return s;
        }

        public static void Axpy(float g, ReadOnlySpan<float> x, Span<float> y)
        {
            int n = x.Length, i = 0;
            if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
            {
                var gv = new Vector<float>(g);
                int vn = n - n % Vector<float>.Count;
                for (; i < vn; i += Vector<float>.Count) (new Vector<float>(y.Slice(i)) + gv * new Vector<float>(x.Slice(i))).CopyTo(y.Slice(i));
            }
            for (; i < n; i++) y[i] += g * x[i];
        }

        public void ZeroGrad() { foreach (var g in GW) Array.Clear(g); foreach (var g in GB) Array.Clear(g); }

        public void AddGrad(Work w)
        {
            for (int l = 0; l < GW.Length; l++) { Add(GW[l], w.GW[l]); Add(GB[l], w.GB[l]); }
        }

        private static void Add(float[] into, float[] from)
        {
            var a = new Span<float>(into);
            Axpy(1, from, a);
        }

        public double GradNormSq()
        {
            double s = 0;
            foreach (var g in GW) foreach (float v in g) s += v * v;
            foreach (var g in GB) foreach (float v in g) s += v * v;
            return s;
        }

        public void ScaleGrad(float k)
        {
            foreach (var g in GW) for (int i = 0; i < g.Length; i++) g[i] *= k;
            foreach (var g in GB) for (int i = 0; i < g.Length; i++) g[i] *= k;
        }
    }

    /// <summary>Adam over a network's weights.</summary>
    public sealed class Adam
    {
        private readonly TrainMlp _n;
        private readonly float[][] _mW, _vW, _mB, _vB;
        private int _t;
        public double Lr = 3e-4, Beta1 = 0.9, Beta2 = 0.999, Eps = 1e-5;

        public Adam(TrainMlp n)
        {
            _n = n;
            int L = n.Layers;
            _mW = new float[L][]; _vW = new float[L][]; _mB = new float[L][]; _vB = new float[L][];
            for (int l = 0; l < L; l++)
            {
                _mW[l] = new float[n.GW[l].Length]; _vW[l] = new float[n.GW[l].Length];
                _mB[l] = new float[n.GB[l].Length]; _vB[l] = new float[n.GB[l].Length];
            }
        }

        public void Step()
        {
            _t++;
            double c1 = 1 - Math.Pow(Beta1, _t), c2 = 1 - Math.Pow(Beta2, _t);
            float a = (float)(Lr * Math.Sqrt(c2) / c1);
            float b1 = (float)Beta1, b2 = (float)Beta2, eps = (float)Eps;
            Parallel.For(0, _n.Layers, l =>
            {
                Upd(_n.Net.W[l], _n.GW[l], _mW[l], _vW[l], a, b1, b2, eps);
                Upd(_n.Net.B[l], _n.GB[l], _mB[l], _vB[l], a, b1, b2, eps);
            });
        }

        private static void Upd(float[] p, float[] g, float[] m, float[] v, float a, float b1, float b2, float eps)
        {
            for (int i = 0; i < p.Length; i++)
            {
                float gi = g[i];
                m[i] = b1 * m[i] + (1 - b1) * gi;
                v[i] = b2 * v[i] + (1 - b2) * gi * gi;
                p[i] -= a * m[i] / (MathF.Sqrt(v[i]) + eps);
            }
        }
    }
}
