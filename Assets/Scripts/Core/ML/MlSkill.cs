using System;

namespace Sculpt.Core.ML
{
    /// <summary>
    /// The human limits a learned bot plays under. They sit outside the network (the senses and the hands apply them), so
    /// the same trained model plays at any level, and the network is told its own limits as inputs so it can learn to
    /// play well inside them (a slow reactor holds angles; a fast one takes duels).
    /// </summary>
    public sealed class MlSkill
    {
        /// <summary>It acts on what it saw or heard this long ago (ticks; 24 = 200 ms at 120 Hz).</summary>
        public int ReactionTicks = 24;
        /// <summary>Fastest turn, degrees per second.</summary>
        public double TurnDegPerSec = 420;
        /// <summary>Size of the slow hand wobble added to its aim, degrees.</summary>
        public double AimNoiseDeg = 0.8;
        /// <summary>Horizontal view cone it sees in (a screen plus a little side vision), degrees.</summary>
        public double ViewConeDeg = 110;
        /// <summary>Sampling temperature: 1 plays the policy as trained; higher is sloppier, lower is greedier.</summary>
        public double Temperature = 1.0;

        public static MlSkill Easy => new MlSkill { ReactionTicks = 36, TurnDegPerSec = 260, AimNoiseDeg = 2.0, ViewConeDeg = 95, Temperature = 1.25 };
        public static MlSkill Normal => new MlSkill();
        public static MlSkill Hard => new MlSkill { ReactionTicks = 20, TurnDegPerSec = 560, AimNoiseDeg = 0.5, ViewConeDeg = 115, Temperature = 0.9 };
        /// <summary>The top of the human range: about a pro's reaction and hand. Never faster than this.</summary>
        public static MlSkill Pro => new MlSkill { ReactionTicks = 18, TurnDegPerSec = 720, AimNoiseDeg = 0.3, ViewConeDeg = 120, Temperature = 0.75 };

        /// <summary>A random skill in the human range (training samples one per life so the model learns every level).</summary>
        public static MlSkill Random(MlRng rng)
        {
            double t = rng.NextDouble();
            return new MlSkill
            {
                ReactionTicks = (int)Math.Round(18 + (40 - 18) * Lerp(t, rng.NextDouble())),
                TurnDegPerSec = 240 + (720 - 240) * Lerp(1 - t, rng.NextDouble()),
                AimNoiseDeg = 0.3 + 1.9 * Lerp(t, rng.NextDouble()),
                ViewConeDeg = 95 + 25 * rng.NextDouble(),
                Temperature = 1.0,
            };
        }

        // Skills are correlated (a slow reactor usually has a slow hand) but not locked together.
        private static double Lerp(double main, double noise) => 0.7 * main + 0.3 * noise;

        public MlSkill Clone() => (MlSkill)MemberwiseClone();
    }

    /// <summary>A tiny deterministic random source (xorshift64*), the same on every runtime (System.Random isn't promised to be).</summary>
    public sealed class MlRng
    {
        private ulong _s;
        public MlRng(ulong seed) { _s = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL; if (_s == 0) _s = 1; Next(); }
        public ulong Next()
        {
            _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
            return _s * 0x2545F4914F6CDD1DUL;
        }
        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
        public int Next(int n) => (int)((Next() >> 33) % (ulong)n);
        /// <summary>Standard normal (Box-Muller).</summary>
        public double Gauss() { double u = 1 - NextDouble(), v = NextDouble(); return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); }
    }
}
