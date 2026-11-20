namespace Sculpt.Core
{
    /// <summary>
    /// Every sculpt number. Game fills this from a ScriptableObject; defaults match CLAUDE.md.
    /// Times are ticks at 60 Hz, bar values are basis points (10000 = 100%).
    /// </summary>
    public sealed class SculptConfig
    {
        public const int BarMax = 10000;

        public double RangeMeters = 6.0;

        public int WindupTicks = 12;                   // 0.2 s
        public int ReinforcedCasterWindupTicks = 21;   // 0.35 s
        public int EnemyReinforcedWindupTicks = 24;    // 0.4 s (balance sim 2026-11-20: must stay below a pro counter-break, ~0.5 s)

        public int CostMove = 500;      // 5%
        public int CostSink = 700;      // 7%
        public int CostRaise = 1000;    // 10%
        public int CostShape = 500;     // 5%
        public int CostReinforce = 1500; // +15%
        public int EnemyReinforcedCostMultiplier = 2;

        public int RegenDelayTicks = 90;      // 1.5 s after the last sculpt
        public int RegenFullTicks = 1200;     // 0 to 100% in 20 s

        public int ReinforceDurationTicks = 1200; // 20 s
        public int TintFadeTicks = 600;           // 10 s (cosmetic)

        /// <summary>Sculptable cells: y from MinCellY to MaxCellY inclusive. Column tops therefore range -2..+4.</summary>
        public int MinCellY = -2;
        public int MaxCellY = 3;

        /// <summary>Unsupported blocks drop one cell every this many ticks.</summary>
        public int BlockFallIntervalTicks = 4;

        /// <summary>Extra margin used to decide who is "touching" a block being sculpted (for warnings).</summary>
        public double TouchMarginMeters = 0.05;

        /// <summary>Slack allowed between the client's aim point and the targeted block, metres.</summary>
        public double AimTolerance = 0.02;

        public int CostFor(SculptActionType t)
        {
            switch (t)
            {
                case SculptActionType.Move: return CostMove;
                case SculptActionType.Raise: return CostRaise;
                case SculptActionType.Sink: return CostSink;
                case SculptActionType.Shape: return CostShape;
                default: return 0;
            }
        }
    }
}
