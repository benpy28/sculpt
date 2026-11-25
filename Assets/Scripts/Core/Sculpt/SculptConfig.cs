using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Every sculpt number. Game fills this from a ScriptableObject; defaults match CLAUDE.md (terrain v2 and the
    /// balance numbers Ben approved on 2026-11-20). Times are ticks at 60 Hz, bar values are basis points (10000 = 100%).
    /// Piece sizes are in sub-cells; the reference piece is 2x2 tiles, one level thick (8 x 8 x 4 = 256 sub-cells).
    /// </summary>
    public sealed class SculptConfig
    {
        public const int BarMax = 10000;

        /// <summary>
        /// Block moving (push, pull, raise, sink, throw on terrain pieces). Parked by Ben on 2026-11-21 in favour of
        /// destructible walls and healing; the game turns it off from its one switch (Breach Settings). True here so
        /// the parked system's own tests keep running.
        /// </summary>
        public bool BlockMovesEnabled = true;

        public double RangeMeters = 6.0;

        // ---- Windups (at or below the reference size) ----
        public int WindupTicks = 9 * SimTime.Per60;                    // 0.15 s (Ben 2026-11-20, was 0.2 s)
        public int ReinforcedCasterWindupTicks = 21 * SimTime.Per60;   // 0.35 s, a minimum
        public int EnemyReinforcedWindupTicks = 24 * SimTime.Per60;    // 0.4 s, a minimum (balance sim: must stay below a pro counter-break, ~0.5 s)
        public int MaxWindupTicks = 15 * SimTime.Per60;                // size scaling cap for normal actions (0.25 s; was 20 with the 0.2 s base)

        // ---- Distances, in sub-cells ----
        /// <summary>Push/pull travel: 5 sub-cells = 1.5 m (Ben 2026-11-20: "30% further" than one tile; 1.5 m is the nearest exact step).</summary>
        public int MoveSubs = 5;
        /// <summary>Raise/sink travel: one level (4 sub-cells, 1.2 m).</summary>
        public int LiftSubs = SubGrid.TileSubs;

        // ---- Costs (base, at the reference size) ----
        public int CostMove = 500;       // 5%
        public int CostSink = 700;       // 7%
        public int CostRaise = 1000;     // 10%
        public int CostReinforce = 1500; // +15%, flat (not scaled, not doubled)
        public int EnemyReinforcedCostMultiplier = 2;

        // ---- Size scaling ----
        public int ReferenceSubCells = 256;  // 2x2 tiles x one level
        public int MinCostScalePercent = 50;
        public int MaxCostScalePercent = 200;

        // ---- Bar ----
        public int RegenDelayTicks = 90 * SimTime.Per60;      // 1.5 s after the last sculpt
        public int RegenFullTicks = 1200 * SimTime.Per60;     // 0 to 100% in 20 s

        public int ReinforceDurationTicks = 1200 * SimTime.Per60; // 20 s
        public int TintFadeTicks = 600 * SimTime.Per60;           // 10 s (cosmetic)

        /// <summary>A confirm rejected as Locked or Busy is retried for this many ticks (the click buffer).</summary>
        public int ConfirmBufferTicks = 3 * SimTime.Per60;

        // ---- Height range, in sub-cells: tops from level -2 to +4 ----
        public int MinSubY = SubGrid.SculptMinY;         // -12: a slab here tops out at level -2, the range floor
        public int MaxSubY = 4 * SubGrid.TileSubs - 1;   // 15: highest sculptable sub-cell layer

        // ---- Pieces (aim-time carving) ----
        /// <summary>A neighbouring column joins the piece if its rock top is within this many sub-cells of the seed's.</summary>
        public int PieceHeightTolerance = 1;

        // ---- Support and falling ----
        /// <summary>A piece stays up if at least SupportNumerator / SupportDenominator of its underside rests on something.</summary>
        public int SupportNumerator = 1;
        public int SupportDenominator = 3;
        /// <summary>Unsupported pieces drop one sub-cell every this many ticks (one level per 4 ticks).</summary>
        public int FallTicksPerSubCell = 1 * SimTime.Per60;
        /// <summary>Loose natural rock larger than this is treated as attached to the world (never falls).</summary>
        public int NaturalGroundedLimit = 2048;

        // ---- Shove and carry ----
        public double MaxLift = SubGrid.TileSize + 0.01;   // carry onto a raised piece, one level
        public double SmallLift = 0.3;                     // a push only ever lifts this much (clipping a chamfer)
        public double MaxShove = 5 * SubGrid.SubSize + 1e-6;  // the push travel (MoveSubs): someone in the path is shoved at most that far
        /// <summary>Horizontal friction kick given to players standing on a piece that moves sideways (m/s).</summary>
        public double RiderDragSpeed = 7.7;
        /// <summary>Box bottom within this of a piece's top counts as standing on it.</summary>
        public double RiderTolerance = 0.06;

        /// <summary>Extra margin used to decide who is "touching" a piece being sculpted (for warnings).</summary>
        public double TouchMarginMeters = 0.05;
        /// <summary>Slack allowed between the client's aim point and the targeted sub-cell, metres (chamfers sit outside it).</summary>
        public double AimTolerance = 0.2;

        // ---- Throw (balance sim re-base, s = N / ReferenceSubCells) ----
        public int ThrowWindupBaseTicks = 24 * SimTime.Per60;        // + 12 x s
        public int ThrowWindupPerRefTicks = 12 * SimTime.Per60;
        public double ThrowSpeedBase = 20.0;         // - 3 x s m/s
        public double ThrowSpeedPerRef = 3.0;
        public double ThrowMinSpeed = 8.0;
        public double ThrowGravity = 15.0;           // m/s^2
        public double ThrowLiftDegrees = 8.0;
        public int ThrowCostBase = 1200;             // 12% + 13% x s
        public int ThrowCostPerRef = 1300;
        public double ThrowDamageBase = 30.0;        // + 40 x s, times the impact-speed factor
        public double ThrowDamagePerRef = 40.0;
        public double ThrowDamageCap = 120.0;
        public double ThrowImpactFactorMin = 0.6;
        public double ThrowImpactFactorMax = 1.2;
        public double ThrowSplashRadius = 1.0;       // metres around the impact box
        public double ThrowSplashFraction = 0.4;
        public double ThrowRiderKick = 4.0;          // m/s along the throw for anyone standing on the piece
        public int ThrowRiderIgnoreTicks = 12 * SimTime.Per60;       // the piece ignores its riders this long after launch
        public int ThrowMaxFlightTicks = 600 * SimTime.Per60;
        public double ThrowShrink = 0.15;            // flight box = piece bounds shrunk by this on each side
        public int ThrowFollowThroughTicks = 6 * SimTime.Per60;      // weapon layer: after launch
        /// <summary>Disarm ticks = DisarmMaxTicks x clamp((damage - DisarmMinDamage) / DisarmRangeDamage, 0, 1).</summary>
        public int DisarmMaxTicks = 30 * SimTime.Per60;
        public double DisarmMinDamage = 30.0;
        public double DisarmRangeDamage = 60.0;

        /// <summary>Sub-cell translation of an action (zero for a throw).</summary>
        public Int3 DeltaFor(SculptActionType t, Dir d)
        {
            switch (t)
            {
                case SculptActionType.Move: return d.ToInt3() * MoveSubs;
                case SculptActionType.Raise: return new Int3(0, LiftSubs, 0);
                case SculptActionType.Sink: return new Int3(0, -LiftSubs, 0);
                default: return Int3.Zero;
            }
        }

        public int CostFor(SculptActionType t)
        {
            switch (t)
            {
                case SculptActionType.Move: return CostMove;
                case SculptActionType.Raise: return CostRaise;
                case SculptActionType.Sink: return CostSink;
                default: return 0;
            }
        }

        /// <summary>Base cost scaled by piece size: base x N / ref, clamped to 0.5x .. 2x. Integer, rounded.</summary>
        public int ScaledCost(int baseCost, int subCells)
        {
            long n = Math.Max(ReferenceSubCells * (long)MinCostScalePercent / 100, Math.Min(ReferenceSubCells * (long)MaxCostScalePercent / 100, subCells));
            return (int)((baseCost * n + ReferenceSubCells / 2) / ReferenceSubCells);
        }

        /// <summary>12 ticks at or below the reference, +1 per extra eighth of it (rounded up), capped.</summary>
        public int ScaledWindup(int subCells)
        {
            int extra = Math.Max(0, subCells - ReferenceSubCells);
            int eighth = Math.Max(1, ReferenceSubCells / 8);
            // +1/60 s per extra eighth of the reference piece (one tick at 60 Hz, SimTime.Per60 ticks now).
            return Math.Min(MaxWindupTicks, WindupTicks + SimTime.Per60 * ((extra + eighth - 1) / eighth));
        }

        public int ThrowWindup(int subCells) =>
            ThrowWindupBaseTicks + (int)((ThrowWindupPerRefTicks * (long)subCells + ReferenceSubCells / 2) / ReferenceSubCells);

        public int ThrowCost(int subCells) =>
            ThrowCostBase + (int)((ThrowCostPerRef * (long)subCells + ReferenceSubCells / 2) / ReferenceSubCells);

        public double ThrowSpeed(int subCells) =>
            Math.Max(ThrowMinSpeed, ThrowSpeedBase - ThrowSpeedPerRef * subCells / ReferenceSubCells);

        public double ThrowDamage(int subCells, double impactFactor)
        {
            double f = Math.Max(ThrowImpactFactorMin, Math.Min(ThrowImpactFactorMax, impactFactor));
            return Math.Min(ThrowDamageCap, (ThrowDamageBase + ThrowDamagePerRef * subCells / ReferenceSubCells) * f);
        }

        public int DisarmTicks(int damage) =>
            (int)Math.Round(DisarmMaxTicks * Math.Max(0, Math.Min(1, (damage - DisarmMinDamage) / DisarmRangeDamage)), MidpointRounding.AwayFromZero);
    }
}
