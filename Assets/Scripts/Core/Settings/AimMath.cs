using System;

namespace Sculpt.Core.Settings
{
    /// <summary>
    /// Mouse sensitivity maths for the settings menu. The game turns <c>Sensitivity * 0.022</c> degrees per mouse count
    /// (the Source-style scale), so a sensitivity copied from another game on that scale feels the same. cm/360 is how
    /// far the mouse travels on the desk for one full turn: the only number that means the same on every mouse and in
    /// every game, which is why the menu shows it.
    /// </summary>
    public static class AimMath
    {
        /// <summary>Degrees of turn per mouse count at sensitivity 1.</summary>
        public const double DegreesPerCountAtOne = 0.022;
        public const double CmPerInch = 2.54;
        public const int MinDpi = 100, MaxDpi = 32000, DefaultDpi = 800;

        public static double DegreesPerCount(double sensitivity) => sensitivity * DegreesPerCountAtOne;

        /// <summary>Desk distance in cm for one 360 degree turn. 0 if the inputs make no sense.</summary>
        public static double CmPer360(double sensitivity, int dpi, double multiplier = 1.0)
        {
            double deg = DegreesPerCount(sensitivity) * multiplier;
            if (deg <= 0 || dpi <= 0 || double.IsNaN(deg)) return 0;
            double counts = 360.0 / deg;
            return counts / dpi * CmPerInch;
        }

        /// <summary>The sensitivity that gives <paramref name="cm"/> per 360 at this DPI (inverse of <see cref="CmPer360"/>).</summary>
        public static double SensitivityFor(double cm, int dpi, double multiplier = 1.0)
        {
            if (cm <= 0 || dpi <= 0 || multiplier <= 0) return 0;
            double counts = cm / CmPerInch * dpi;
            return 360.0 / counts / DegreesPerCountAtOne / multiplier;
        }

        /// <summary>eDPI: DPI times sensitivity, the number players compare across setups on the same scale.</summary>
        public static double Edpi(double sensitivity, int dpi) => sensitivity * dpi;

        public static int ClampDpi(int dpi) => dpi < MinDpi ? MinDpi : dpi > MaxDpi ? MaxDpi : dpi;

        /// <summary>
        /// Degrees per count while aiming down sights. The zoom scales the tangent of the view by the gun's
        /// <c>AimFovScale</c>, so multiplying by that scale too ("zoom matched") keeps a flick to a point on screen the same
        /// hand movement with and without zoom; the ADS multiplier then applies on top.
        /// </summary>
        public static double AdsDegreesPerCount(double sensitivity, double adsMultiplier, bool zoomMatched, double zoomFovScale)
        {
            double k = DegreesPerCount(sensitivity) * adsMultiplier;
            if (zoomMatched && zoomFovScale > 0 && zoomFovScale < 1) k *= zoomFovScale;
            return k;
        }
    }
}
