using System;

namespace Sculpt.Core
{
    /// <summary>Per-player sculpt bar. Integer basis points with exact fractional regen (no drift).</summary>
    public sealed class SculptBar
    {
        public int Value { get; private set; } = SculptConfig.BarMax;

        /// <summary>Tick of the last spend. Regen waits RegenDelayTicks after it.</summary>
        public int LastSpendTick { get; private set; } = int.MinValue / 2;

        private int _remainder;

        public bool CanAfford(int cost) => cost <= Value;

        public void Spend(int cost, int tick)
        {
            if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
            if (cost > Value) throw new InvalidOperationException("Not enough bar.");
            Value -= cost;
            LastSpendTick = tick;
            _remainder = 0;
        }

        /// <summary>Gives back a cancelled action's cost. Does not reset the regen delay.</summary>
        public void Refund(int amount)
        {
            Value = Math.Min(SculptConfig.BarMax, Value + amount);
            if (Value == SculptConfig.BarMax) _remainder = 0;
        }

        private int _damageRemainder;

        /// <summary>
        /// Bar earned by dealing <paramref name="damage"/> to an enemy: <paramref name="per10"/> basis points per 10
        /// damage, exact (fractions carry over to the next hit). Does not reset the refill delay. Returns the gain.
        /// </summary>
        public int GainForDamage(int damage, int per10)
        {
            if (damage <= 0 || per10 <= 0) return 0;
            long total = (long)damage * per10 + _damageRemainder;
            _damageRemainder = (int)(total % 10);
            int gain = (int)Math.Min(SculptConfig.BarMax, total / 10);
            int before = Value;
            Value = Math.Min(SculptConfig.BarMax, Value + gain);
            if (Value == SculptConfig.BarMax) _remainder = 0;
            return Value - before;
        }

        public void SetValue(int value)
        {
            Value = Math.Max(0, Math.Min(SculptConfig.BarMax, value));
            _remainder = 0;
        }

        /// <summary>Advance one tick. Regen runs on ticks strictly after LastSpendTick + RegenDelayTicks.</summary>
        public void Tick(int tick, SculptConfig cfg)
        {
            if (Value >= SculptConfig.BarMax) return;
            if (tick - LastSpendTick <= cfg.RegenDelayTicks) return;
            _remainder += SculptConfig.BarMax;
            int gain = _remainder / cfg.RegenFullTicks;
            _remainder -= gain * cfg.RegenFullTicks;
            Value = Math.Min(SculptConfig.BarMax, Value + gain);
            if (Value == SculptConfig.BarMax) _remainder = 0;
        }
    }
}
