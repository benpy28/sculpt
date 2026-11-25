using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Fixed-tick simulation clock. All Core timers are integer tick counts.
    ///
    /// The simulation runs at 120 Hz (Ben 2026-11-25; it was 60). A click waits for the next tick before anything
    /// can happen, and the server buffers each client's commands a couple of ticks, so a shorter tick is less delay
    /// on both. The design is written in sixtieths of a second ("draw 12 ticks = 0.2 s"), and 120 is exactly twice
    /// 60, so every such timing keeps its length: write it as <c>12 * SimTime.Per60</c>.
    /// </summary>
    public static class SimTime
    {
        public const int TicksPerSecond = 120;

        /// <summary>
        /// Simulation ticks in a sixtieth of a second. Multiply a duration given in sixtieths (the unit the design
        /// spec and its approved timings use) by this to get ticks.
        /// </summary>
        public const int Per60 = TicksPerSecond / 60;

        /// <summary>Converts seconds to the nearest whole tick count. Use only for config conversion, never inside the simulation.</summary>
        public static int SecondsToTicks(double seconds)
        {
            if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Durations cannot be negative.");
            return (int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
        }
    }
}
