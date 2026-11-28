using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The natural ground of the 1,306 m battle royale map (map v3 step 4, Ben's plan: hills and elevation changes,
    /// valleys, a dry river bed, a raised central plateau, a sunken quarry), before the buildings' footings are cut in.
    /// A plain function of the distance from the map's centre along X and Z, designed in one eighth (u toward an edge, v
    /// toward a corner, 0 &lt;= v &lt;= u) and the same in all eight: every hill stands for itself and its seven twins and
    /// every shape is mirror-even across the eighth's edges, so the ground has no creases where the eighths meet (and the
    /// height map built from it is exactly 8-way symmetric anyway, <see cref="HeightField.Symmetric"/>).
    ///
    /// From the middle out: the plateau (7.5 m, rock faces, a ramp up from each edge midline and each diagonal), rolling
    /// fields, the Hilltop Fort hill on each diagonal, a dry river bed down each edge midline from the plateau's foot to
    /// the rim (a natural road, banks you can walk down), the quarry where it crosses the middle ring (terraced faces,
    /// two ramps along the river), hills and a valley in every eighth, the region approaches (the ground between every
    /// spawn pad and its shared POI rises and falls the same way for all four pads of a region: it depends only on the
    /// distance from the POI there), and the rim (a bank up, then a rock face nobody climbs).
    ///
    /// Slopes keep to two bands: walkable ground under about 35 degrees, rock faces over 55 (tested), so nothing is half
    /// way; in between only at a face's lip and foot.
    /// </summary>
    public static class BrGround
    {
        // ---- The plateau.
        public const double PlateauTop = 7.5, PlateauR = 112, FaceDegrees = 62, RampDegrees = 14, RampHalfWidth = 9;
        // ---- The river bed (down each edge midline).
        public const double RiverStart = 175, RiverEnd = 545, RiverHalfFloor = 7, BankDegrees = 22;
        // ---- The quarry (on the river, in the middle ring).
        public const double QuarryU = 372, QuarryFloor = -16.2, QuarryRampHalf = 6;
        // ---- Hilltop Fort's hill (on each diagonal).
        public const double FortAt = 182, FortHeight = 21, FortRadius = 72;
        // ---- The rim.
        public const double RimStart = 576, RimBank = 30, RimBankTop = 6, CliffTop = 24, CliffDegrees = 72;
        /// <summary>Ground level of the open fields, the POIs and the spawn regions.</summary>
        public const double Base = 1.5;
        // ---- The region approaches (radial round each region's POI).
        public const double PoiU = 297.3, PoiV = 297.3, PoiFlat = 40, ApproachInner = 11, ApproachOuter = 19, ApproachReach = 262, ApproachFade = 28;

        /// <summary>A round hill (or with negative height a hollow) at (U, V) in the design eighth, with its seven twins.</summary>
        private struct Hill
        {
            public double U, V, R, H;
            public Hill(double u, double v, double r, double h) { U = u; V = v; R = r; H = h; }
        }

        private static readonly Hill[] Hills =
        {
            new Hill(FortAt, FortAt, FortRadius, FortHeight),  // Hilltop Fort
            new Hill(168, 62, 30, 6),      // between the plateau and the farmhouse (a chest on top)
            new Hill(430, 225, 40, 9),     // the corner field (a chest on top)
            new Hill(450, 150, 45, 12),
            new Hill(250, 220, 35, 8),     // shoulder of the fort hill
            new Hill(520, 88, 36, 10),
            new Hill(360, 150, 40, 10),
            new Hill(300, 200, 30, -3.6),  // a hollow below the fort's shoulder
            new Hill(560, 300, 22, 4.5),   // behind the spawn region
            new Hill(470, 420, 45, 11),    // the far corner
            new Hill(220, 140, 26, -3),    // a dip on the farm road
        };

        /// <summary>A long low ridge, with its twins.</summary>
        private static readonly (double au, double av, double bu, double bv, double hw, double h)[] Ridges =
        {
            (271, 99, 271, 150, 24, 6),
            (330, 245, 375, 258, 14, 4),
        };

        /// <summary>Waves for the gentle roll of the open ground: angular frequency (1/m), direction, amplitude, phase.</summary>
        private static readonly (double w, double dir, double amp, double ph)[] Roll =
        {
            (0.021, 0.3, 0.55, 0.0), (0.034, 1.1, 0.4, 1.3), (0.057, 2.0, 0.25, 2.1), (0.083, 0.7, 0.14, 0.4),
        };

        private static readonly double FaceK = Math.Tan(FaceDegrees * Math.PI / 180);
        private static readonly double RampK = Math.Tan(RampDegrees * Math.PI / 180);
        private static readonly double BankK = Math.Tan(BankDegrees * Math.PI / 180);
        private static readonly double CliffK = Math.Tan(CliffDegrees * Math.PI / 180);
        private static readonly double QuarryFaceK = Math.Tan(70 * Math.PI / 180);
        private static readonly double QuarryRampK = Math.Tan(15 * Math.PI / 180);

        /// <summary>Natural ground at (u, v) metres from the map's centre (any quadrant: it folds into the design eighth).</summary>
        public static double At(double u, double v)
        {
            u = Math.Abs(u); v = Math.Abs(v);
            if (v > u) (u, v) = (v, u);
            return Eighth(u, v);
        }

        /// <summary>Natural ground in the design eighth (0 &lt;= v &lt;= u).</summary>
        public static double Eighth(double u, double v)
        {
            double r = Math.Sqrt(u * u + v * v);
            // Open ground: the base level, a gentle roll, hills and hollows.
            double h = Base + RollAt(u, v);
            for (int k = 0; k < Hills.Length; k++) h += Images(u, v, k);
            foreach (var g in Ridges) h += RidgeImages(u, v, g.au, g.av, g.bu, g.bv, g.hw, g.h);
            // The region approaches: radial round the POI near the line to the region, and a level plaza round the POI.
            double pu = u - PoiU, pv = v - PoiV, d = Math.Sqrt(pu * pu + pv * pv);
            double ang = Math.Abs(Math.Atan2(pv, pu)) * 180 / Math.PI;   // 0 = straight toward the region (+u)
            double wSector = (1 - GroundShapes.Smoothstep((ang - ApproachInner) / (ApproachOuter - ApproachInner)))
                             * (1 - GroundShapes.Smoothstep((d - ApproachReach) / ApproachFade));
            double wPlaza = 1 - GroundShapes.Smoothstep((d - PoiFlat) / 30);
            double w = Math.Max(wSector, wPlaza);
            if (w > 0) h += (Approach(d) - h) * w;
            // The plateau, with its ramps.
            double plateau = PlateauTop - Math.Max(0, r - PlateauR) * FaceK;
            double ramp = PlateauTop - Math.Max(0, r - PlateauR) * RampK;
            double across = Math.Min(Math.Abs(u - v) / Math.Sqrt(2), v);   // to the nearest diagonal or edge midline
            if (across > RampHalfWidth) ramp -= (across - RampHalfWidth) * FaceK;
            h = Math.Max(h, Math.Max(plateau, ramp));
            // The river bed down the edge midline (v = 0): a floor that falls gently outward, walkable banks.
            if (u > RiverStart - 40 && u < RiverEnd + 100)
            {
                double floor = -1.8 - 2.4 * Math.Max(0, Math.Min(1, (u - RiverStart) / (RiverEnd - RiverStart)));
                // It starts and ends gently: the floor rises to the fields at both ends (under 10 degrees).
                floor += Math.Max(0, RiverStart - u) * 0.12 + Math.Max(0, u - RiverEnd) * 0.1;
                double bed = floor + Math.Max(0, v - RiverHalfFloor) * BankK;
                h = Math.Min(h, bed);
            }
            // The quarry: terraced rock faces round a deep floor, and two ramps down along the river bed.
            double q = Math.Sqrt((u - QuarryU) * (u - QuarryU) + v * v);
            if (q < 110)
            {
                double pit = q < 24 ? QuarryFloor
                    : q < 26 ? QuarryFloor + (q - 24) * 2.7
                    : q < 34 ? QuarryFloor + 5.4
                    : q < 36 ? QuarryFloor + 5.4 + (q - 34) * 2.7
                    : q < 44 ? QuarryFloor + 10.8
                    : QuarryFloor + 10.8 + (q - 44) * QuarryFaceK;
                // The ramps cut down through the terraces along the river bed, rock faces either side.
                double along = Math.Abs(u - QuarryU);
                double rampTop = QuarryFloor + Math.Max(0, along - 24) * QuarryRampK + Math.Max(0, v - QuarryRampHalf) * QuarryFaceK;
                h = Math.Min(h, Math.Min(pit, rampTop));
            }
            // The rim: the ground banks up, then a rock face, flat to the edge.
            if (u > RimStart)
            {
                h += GroundShapes.Smoothstep((u - RimStart) / RimBank) * RimBankTop;
                if (u > RimStart + RimBank) h = Math.Max(h, Math.Min(CliffTop, Base + RimBankTop + (u - RimStart - RimBank) * CliffK));
            }
            return h;
        }

        /// <summary>The ground between a POI and its regions, by distance from the POI: level round the POI, a shallow
        /// dip half way, level again at the region.</summary>
        public static double Approach(double d)
        {
            if (d <= PoiFlat) return Base;
            if (d >= 230) return Base;
            double s = Math.Sin(Math.PI * (d - PoiFlat) / (230 - PoiFlat));
            return Base - 2.1 * s * s;
        }

        private static double RollAt(double u, double v)
        {
            double sum = 0;
            foreach (var w in Roll)
            {
                double cx = Math.Cos(w.dir), cz = Math.Sin(w.dir), acc = 0;
                // The eight images of the point, so the roll is the same seen from every eighth (and smooth across
                // their edges).
                acc += Math.Cos(w.w * (u * cx + v * cz) + w.ph) + Math.Cos(w.w * (-u * cx + v * cz) + w.ph);
                acc += Math.Cos(w.w * (u * cx - v * cz) + w.ph) + Math.Cos(w.w * (-u * cx - v * cz) + w.ph);
                acc += Math.Cos(w.w * (v * cx + u * cz) + w.ph) + Math.Cos(w.w * (-v * cx + u * cz) + w.ph);
                acc += Math.Cos(w.w * (v * cx - u * cz) + w.ph) + Math.Cos(w.w * (-v * cx - u * cz) + w.ph);
                sum += w.amp * acc / 8 * 2; // the mean of eight unit waves has a smaller spread: scaled back up
            }
            return sum;
        }

        private static double Images(double u, double v, int hill)
        {
            Hill k = Hills[hill];
            double h = 0;
            double[] us = HillU[hill], vs = HillV[hill];
            for (int i = 0; i < us.Length; i++)
            {
                double du = u - us[i], dv = v - vs[i];
                if (Math.Abs(du) >= k.R || Math.Abs(dv) >= k.R) continue;
                double r = Math.Sqrt(du * du + dv * dv);
                if (r < k.R) h += k.H * 0.5 * (1 + Math.Cos(Math.PI * r / k.R));
            }
            return h;
        }

        private static double RidgeImages(double u, double v, double au, double av, double bu, double bv, double hw, double height)
        {
            double h = 0;
            foreach (int[] m in BrMap.Sym)
            {
                double ax = m[0] * au + m[1] * av, az = m[2] * au + m[3] * av, bx = m[0] * bu + m[1] * bv, bz = m[2] * bu + m[3] * bv;
                if (u < Math.Min(ax, bx) - hw || u > Math.Max(ax, bx) + hw || v < Math.Min(az, bz) - hw || v > Math.Max(az, bz) + hw) continue;
                h += GroundShapes.Ridge(u, v, ax, az, bx, bz, hw, height);
            }
            return h;
        }

        // The twins of each hill's centre: (±U, ±V) and (±V, ±U); the same spot counts once (a hill on an eighth's edge).
        private static readonly double[][] HillU = new double[Hills.Length][], HillV = new double[Hills.Length][];

        static BrGround()
        {
            for (int k = 0; k < Hills.Length; k++)
            {
                double a = Hills[k].U, b = Hills[k].V;
                var us = new System.Collections.Generic.List<double>();
                var vs = new System.Collections.Generic.List<double>();
                foreach (var p in new[] { (a, b), (-a, b), (a, -b), (-a, -b), (b, a), (-b, a), (b, -a), (-b, -a) })
                {
                    bool dup = false;
                    for (int i = 0; i < us.Count && !dup; i++) dup = Math.Abs(us[i] - p.Item1) < 1e-9 && Math.Abs(vs[i] - p.Item2) < 1e-9;
                    if (dup) continue;
                    us.Add(p.Item1); vs.Add(p.Item2);
                }
                HillU[k] = us.ToArray();
                HillV[k] = vs.ToArray();
            }
        }
    }
}
