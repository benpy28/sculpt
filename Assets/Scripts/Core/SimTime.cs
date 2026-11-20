using System;

namespace Sculpt.Core
{
    /// <summary>Fixed-tick simulation clock. All Core timers are integer tick counts.</summary>
    public static class SimTime
    {
        public const int TicksPerSecond = 60;

        /// <summary>Converts seconds to the nearest whole tick count. Use only for config conversion, never inside the simulation.</summary>
        public static int SecondsToTicks(double seconds)
        {
            if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Durations cannot be negative.");
            return (int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
        }
    }
}
