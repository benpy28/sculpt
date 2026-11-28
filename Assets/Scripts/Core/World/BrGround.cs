using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The natural ground of the battle royale map (before the buildings' footings are cut in), put together from the
    /// nine segments (<see cref="BrLayout"/>, Ben 2026-11-28: each segment designed on its own). At a point:
    ///
    /// 1. the owning segment's own ground (<see cref="BrSegment.Ground"/>);
    /// 2. within <see cref="BrLayout.SeamBand"/> of an inner border it eases (smoothstep, so no crease) to the shared
    ///    ground <see cref="Shared"/>, which both sides of every border reach exactly at the border;
    /// 3. in an outer segment, round its home anchor: level at <see cref="Base"/> on the home plaza, and on the approach
    ///    (a narrow wedge from the anchor toward the spawn region, out past the pads) the same profile for every pad, by
    ///    distance from the anchor only (<see cref="Approach"/>): every pad has the same ground between it and its POI;
    /// 4. the rim: the ground banks up from <see cref="RimStart"/>, then a rock face nobody climbs, flat to the edge.
    ///
    /// Segment ground functions must be pure and thread-safe (the field is built on every core) and keep to the map's
    /// two slope bands (walkable hills under about 35 degrees, rock faces over 55; tested over the whole map), between
    /// <see cref="Lowest"/> and <see cref="Highest"/>.
    /// </summary>
    public static class BrGround
    {
        /// <summary>Ground level of the shared ground, the seams, the home plazas and the spawn regions.</summary>
        public const double Base = 1.5;
        // ---- The rim.
        public const double RimStart = 576, RimBank = 30, RimBankTop = 6, CliffTop = 24, CliffDegrees = 72;
        /// <summary>Natural ground anywhere on the map stays within these (props, the walking grid and footings rely on it).</summary>
        public const double Lowest = -26, Highest = 40;
        // ---- The approaches: a wedge round the line from each anchor to its spawn region.
        public const double ApproachInner = 10, ApproachOuter = 14, ApproachFade = 10, ApproachDip = 1.8;
        /// <summary>The approach is level again from here out to the pads (so the spawn plot's footing, a straight edge,
        /// changes nothing on any pad's line).</summary>
        public const double ApproachLevelFrom = 125;
        /// <summary>The home plaza is level out to this, easing back to the segment's own ground by <see cref="BrLayout.HomeRadius"/>.</summary>
        public const double PlazaFlat = 35;

        private static readonly double CliffK = Math.Tan(CliffDegrees * Math.PI / 180);

        /// <summary>Waves for the gentle roll of the shared ground: angular frequency (1/m), direction, amplitude, phase.</summary>
        private static readonly (double w, double dir, double amp, double ph)[] Roll =
        {
            (0.021, 0.3, 0.55, 0.0), (0.034, 1.1, 0.4, 1.3), (0.057, 2.0, 0.25, 2.1), (0.083, 0.7, 0.14, 0.4),
        };

        /// <summary>The shared ground at a map position: base level with a gentle roll (about +-1.5 m). Every seam meets it.</summary>
        public static double Shared(double x, double z)
        {
            double u = x - BrLayout.MapCentre, v = z - BrLayout.MapCentre, sum = 0;
            foreach (var w in Roll)
            {
                double cx = Math.Cos(w.dir), cz = Math.Sin(w.dir);
                sum += w.amp * (Math.Cos(w.w * (u * cx + v * cz) + w.ph) + Math.Cos(w.w * (v * cx - u * cz) + w.ph * 0.7)) * 0.7;
            }
            return Base + sum;
        }

        /// <summary>Natural ground at (u, v) metres from the map's centre.</summary>
        public static double At(double u, double v) => Natural(BrLayout.MapCentre + u, BrLayout.MapCentre + v);

        /// <summary>Natural ground (before footings) at a map position.</summary>
        public static double Natural(double x, double z)
        {
            int id = BrLayout.Owner(x, z);
            double h = BrSegments.All[id].Ground(x, z);
            // The seam: ease to the shared ground near every inner border.
            double d = BrLayout.ToInnerBorder(id, x, z);
            if (d < BrLayout.SeamBand)
            {
                double s = Shared(x, z);
                h = s + (h - s) * GroundShapes.Smoothstep(d / BrLayout.SeamBand);
            }
            // The home plaza and the approach.
            if (id != BrLayout.Centre) h = Home(id, x, z, h);
            return Rim(x, z, h);
        }

        /// <summary>The home plaza and approach of an outer segment on top of its ground.</summary>
        public static double Home(int id, double x, double z, double h)
        {
            Vec3d a = BrLayout.Anchor(id);
            BrLayout.TowardKit(id, out double kx, out double kz);
            double px = x - a.X, pz = z - a.Z, r = Math.Sqrt(px * px + pz * pz);
            double reach = BrSpawnKit.PadToAnchor + 8;
            if (r >= reach + ApproachFade) return h;
            double w = 1 - GroundShapes.Smoothstep((r - PlazaFlat) / (BrLayout.HomeRadius - PlazaFlat));
            double along = px * kx + pz * kz, across = Math.Abs(px * kz - pz * kx);
            if (along > 0)
            {
                double ang = Math.Atan2(across, along) * 180 / Math.PI;
                double sector = (1 - GroundShapes.Smoothstep((ang - ApproachInner) / (ApproachOuter - ApproachInner)))
                                * (1 - GroundShapes.Smoothstep((r - reach) / ApproachFade));
                w = Math.Max(w, sector);
            }
            return w > 0 ? h + (Approach(r) - h) * w : h;
        }

        /// <summary>The ground between a home anchor and its spawn region, by distance from the anchor: level on the plaza,
        /// a shallow dip half way, level again at the pads.</summary>
        public static double Approach(double d)
        {
            double p = ApproachLevelFrom;
            if (d <= BrLayout.HomeRadius || d >= p) return Base;
            double s = Math.Sin(Math.PI * (d - BrLayout.HomeRadius) / (p - BrLayout.HomeRadius));
            return Base - ApproachDip * s * s;
        }

        /// <summary>The rim on top of the ground: a bank up from <see cref="RimStart"/>, then a rock face to <see cref="CliffTop"/>.</summary>
        public static double Rim(double x, double z, double h)
        {
            double m = Math.Max(Math.Abs(x - BrLayout.MapCentre), Math.Abs(z - BrLayout.MapCentre));
            if (m <= RimStart) return h;
            h += GroundShapes.Smoothstep((m - RimStart) / RimBank) * RimBankTop;
            if (m > RimStart + RimBank) h = Math.Max(h, Math.Min(CliffTop, Base + RimBankTop + (m - RimStart - RimBank) * CliffK));
            return h;
        }
    }
}
