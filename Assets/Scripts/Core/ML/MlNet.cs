using System;
using System.IO;

namespace Sculpt.Core.ML
{
    /// <summary>
    /// A small fully connected network (tanh between layers, plain output), the only kind the bots use. This is the
    /// runtime half: it reads a model file and runs forward passes, in plain C# with no packages, the same on the server in
    /// Unity and in the trainer. The trainer writes the file (Tools/ML) with the same layout.
    /// </summary>
    public sealed class MlMlp
    {
        public readonly int[] Sizes;
        /// <summary>Per layer: weights [out * in] row by row, then biases [out].</summary>
        public readonly float[][] W, B;

        public MlMlp(int[] sizes)
        {
            Sizes = (int[])sizes.Clone();
            W = new float[sizes.Length - 1][];
            B = new float[sizes.Length - 1][];
            for (int l = 0; l < sizes.Length - 1; l++) { W[l] = new float[sizes[l + 1] * sizes[l]]; B[l] = new float[sizes[l + 1]]; }
        }

        public int Inputs => Sizes[0];
        public int Outputs => Sizes[Sizes.Length - 1];
        public int Widest { get { int m = 0; foreach (int s in Sizes) m = Math.Max(m, s); return m; } }

        /// <summary>Forward pass. <paramref name="scratchA"/> and <paramref name="scratchB"/> hold at least <see cref="Widest"/> numbers.</summary>
        public void Forward(float[] input, float[] output, float[] scratchA, float[] scratchB)
        {
            float[] cur = input, next = scratchA;
            for (int l = 0; l < W.Length; l++)
            {
                int nin = Sizes[l], nout = Sizes[l + 1];
                bool last = l == W.Length - 1;
                float[] dst = last ? output : next;
                float[] w = W[l], b = B[l];
                for (int o = 0; o < nout; o++)
                {
                    float acc = b[o];
                    int row = o * nin;
                    for (int k = 0; k < nin; k++) acc += w[row + k] * cur[k];
                    dst[o] = last ? acc : (float)Math.Tanh(acc);
                }
                cur = dst;
                next = ReferenceEquals(dst, scratchA) ? scratchB : scratchA;
            }
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Sizes.Length);
            foreach (int s in Sizes) w.Write(s);
            for (int l = 0; l < W.Length; l++) { foreach (float v in W[l]) w.Write(v); foreach (float v in B[l]) w.Write(v); }
        }

        public static MlMlp Read(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 2 || n > 16) throw new InvalidDataException("bad layer count");
            var sizes = new int[n];
            for (int i = 0; i < n; i++) { sizes[i] = r.ReadInt32(); if (sizes[i] < 1 || sizes[i] > 8192) throw new InvalidDataException("bad layer size"); }
            var m = new MlMlp(sizes);
            for (int l = 0; l < m.W.Length; l++)
            {
                for (int k = 0; k < m.W[l].Length; k++) m.W[l][k] = r.ReadSingle();
                for (int k = 0; k < m.B[l].Length; k++) m.B[l][k] = r.ReadSingle();
            }
            return m;
        }

        public MlMlp Clone()
        {
            var m = new MlMlp(Sizes);
            for (int l = 0; l < W.Length; l++) { Array.Copy(W[l], m.W[l], W[l].Length); Array.Copy(B[l], m.B[l], B[l].Length); }
            return m;
        }
    }

    /// <summary>
    /// A trained bot: observation scaling plus the policy network (and the value network, kept for further training).
    /// File (.sbm): "SBML", format version, observation layout hash, observation size, mean[], std[], policy, has value, value.
    /// </summary>
    public sealed class MlModel
    {
        public const uint Magic = 0x4C4D4253; // "SBML"
        public const int FormatVersion = 1;

        public uint Spec = MlObs.SpecHash;
        public float[] Mean, Std;
        public MlMlp Policy;
        public MlMlp Value;
        /// <summary>Free text: how and when it was trained (shown in tools, ignored by the game).</summary>
        public string Note = "";

        public static MlModel Fresh(int[] hidden, int seed)
        {
            var rng = new MlRng((ulong)seed);
            var pol = new int[hidden.Length + 2];
            var val = new int[hidden.Length + 2];
            pol[0] = val[0] = MlObs.Size;
            for (int i = 0; i < hidden.Length; i++) pol[i + 1] = val[i + 1] = hidden[i];
            pol[pol.Length - 1] = MlActions.TotalLogits;
            val[val.Length - 1] = 1;
            var m = new MlModel { Mean = new float[MlObs.Size], Std = new float[MlObs.Size], Policy = new MlMlp(pol), Value = new MlMlp(val) };
            for (int k = 0; k < MlObs.Size; k++) m.Std[k] = 1;
            Init(m.Policy, rng, 0.01);
            Init(m.Value, rng, 1.0);
            return m;
        }

        /// <summary>Scaled (orthogonal-like) random start; the last layer small so a fresh policy is nearly uniform.</summary>
        private static void Init(MlMlp n, MlRng rng, double lastGain)
        {
            for (int l = 0; l < n.W.Length; l++)
            {
                int nin = n.Sizes[l];
                double gain = l == n.W.Length - 1 ? lastGain : 1.0;
                double sd = gain / Math.Sqrt(nin);
                for (int k = 0; k < n.W[l].Length; k++) n.W[l][k] = (float)(rng.Gauss() * sd);
            }
        }

        /// <summary>Scales an observation in place the way the model was trained (clipped to +-5).</summary>
        public void Normalize(float[] obs, float[] into)
        {
            for (int k = 0; k < MlObs.Size; k++)
            {
                float v = (obs[k] - Mean[k]) / Std[k];
                into[k] = v < -5 ? -5 : v > 5 ? 5 : v;
            }
        }

        public void Save(string path)
        {
            using (var f = File.Create(path)) Save(f);
        }

        public void Save(Stream s)
        {
            using (var w = new BinaryWriter(s, System.Text.Encoding.UTF8, true))
            {
                w.Write(Magic); w.Write(FormatVersion); w.Write(Spec);
                w.Write(Mean.Length);
                foreach (float v in Mean) w.Write(v);
                foreach (float v in Std) w.Write(v);
                Policy.Write(w);
                w.Write(Value != null);
                Value?.Write(w);
                w.Write(Note ?? "");
            }
        }

        public static MlModel Load(string path)
        {
            using (var f = File.OpenRead(path)) return Load(f);
        }

        /// <summary>Reads a model; refuses one made for another observation layout or action set.</summary>
        public static MlModel Load(Stream s)
        {
            using (var r = new BinaryReader(s, System.Text.Encoding.UTF8, true))
            {
                if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a SCULPT bot model");
                int ver = r.ReadInt32();
                if (ver != FormatVersion) throw new InvalidDataException($"model format {ver}, this build reads {FormatVersion}");
                var m = new MlModel { Spec = r.ReadUInt32() };
                if (m.Spec != MlObs.SpecHash) throw new InvalidDataException("model was trained for another observation layout (retrain it)");
                int n = r.ReadInt32();
                if (n != MlObs.Size) throw new InvalidDataException("observation size differs");
                m.Mean = new float[n]; m.Std = new float[n];
                for (int k = 0; k < n; k++) m.Mean[k] = r.ReadSingle();
                for (int k = 0; k < n; k++) m.Std[k] = Math.Max(1e-4f, r.ReadSingle());
                m.Policy = MlMlp.Read(r);
                if (m.Policy.Inputs != n || m.Policy.Outputs != MlActions.TotalLogits) throw new InvalidDataException("policy shape differs");
                if (r.ReadBoolean()) m.Value = MlMlp.Read(r);
                m.Note = s.Position < s.Length ? r.ReadString() : "";
                return m;
            }
        }

        public MlModel Clone() => new MlModel { Spec = Spec, Mean = (float[])Mean.Clone(), Std = (float[])Std.Clone(), Policy = Policy.Clone(), Value = Value?.Clone(), Note = Note };
    }

    /// <summary>Runs a model's policy for one bot: scales the observation, runs the network, samples each head.</summary>
    public sealed class MlPolicyRunner
    {
        private readonly MlModel _m;
        private readonly float[] _x, _a, _b, _logits;

        public MlPolicyRunner(MlModel m)
        {
            _m = m;
            _x = new float[MlObs.Size];
            int w = m.Policy.Widest;
            _a = new float[w]; _b = new float[w];
            _logits = new float[MlActions.TotalLogits];
        }

        public float[] Logits => _logits;

        /// <summary>Picks an action. Temperature 0 = always the likeliest choice per head.</summary>
        public void Act(float[] obs, double temperature, MlRng rng, int[] action)
        {
            _m.Normalize(obs, _x);
            _m.Policy.Forward(_x, _logits, _a, _b);
            Sample(_logits, temperature, rng, action);
        }

        public static void Sample(float[] logits, double temperature, MlRng rng, int[] action)
        {
            int off = 0;
            for (int h = 0; h < MlActions.Heads; h++)
            {
                int n = MlActions.HeadSizes[h];
                if (temperature <= 1e-6)
                {
                    int best = 0;
                    for (int k = 1; k < n; k++) if (logits[off + k] > logits[off + best]) best = k;
                    action[h] = best;
                }
                else
                {
                    double max = double.MinValue;
                    for (int k = 0; k < n; k++) max = Math.Max(max, logits[off + k]);
                    double sum = 0;
                    for (int k = 0; k < n; k++) sum += Math.Exp((logits[off + k] - max) / temperature);
                    double u = rng.NextDouble() * sum, acc = 0;
                    int pick = n - 1;
                    for (int k = 0; k < n; k++) { acc += Math.Exp((logits[off + k] - max) / temperature); if (u < acc) { pick = k; break; } }
                    action[h] = pick;
                }
                off += n;
            }
        }
    }
}
