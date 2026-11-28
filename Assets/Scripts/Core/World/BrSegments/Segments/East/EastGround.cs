using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The East segment's natural ground (see <see cref="EastPlan"/>): rolling land with three hills in the south, a
    /// plateau at <see cref="EastPlan.Crest"/> in the north east, the reservoir basin west of it and the gorge cut into
    /// it. Put together as the lowest of: the land (rising to the plateau), the basin (a broad bed with gentle banks and
    /// the old river's winding channel), and the cuts (the throat, the spill chute, the dam's slot and the canyon), each
    /// with one-cell rock walls. Pure and thread-safe.
    /// </summary>
    public static class EastGround
    {
        /// <summary>How steeply a cut's wall rises past its edge (metres per metre): a one-cell rock face.</summary>
        private const double Wall = 400;

        /// <summary>Hills on the land: centre, radius, height.</summary>
        public static readonly (double x, double z, double r, double h)[] Hills =
        {
            (1000, 535, 55, 15),   // Surge Hill: the surge tower stands on it
            (1170, 512, 42, 11),   // Pylon Hill
            (935, 548, 58, 5),     // the rise under Damside
            (938, 700, 30, 4.5),   // the knoll by the lake hotel
            (1105, 600, 26, 3),    // a low hump beside the south road
        };

        public static double Ground(double x, double z)
        {
            double land = Land(x, z);
            double h = land;
            h = Math.Min(h, Basin(x, z));
            h = Math.Min(h, Cut(x, z, 1085, EastPlan.GorgeZ0, EastPlan.DamX0, EastPlan.GorgeZ1, EastPlan.Bed));         // the throat
            h = Math.Min(h, EastPlan.Chute(x) + Wall * GroundShapes.ToRect(x, z, EastPlan.ChuteX0, EastPlan.GorgeZ0, EastPlan.DamX1, EastPlan.ChuteZ1));
            h = Math.Min(h, Cut(x, z, EastPlan.DamX0, EastPlan.DamZ0, EastPlan.DamX1, EastPlan.DamZ1, EastPlan.Floor));     // under the dam
            h = Math.Min(h, Cut(x, z, EastPlan.DamX1, EastPlan.GorgeZ0, EastPlan.CanyonX1, EastPlan.GorgeZ1, EastPlan.Floor)); // the canyon
            return h;
        }

        private static double Cut(double x, double z, double x0, double z0, double x1, double z1, double level) =>
            level + Wall * GroundShapes.ToRect(x, z, x0, z0, x1, z1);

        /// <summary>The land: the shared ground with the hills, rising to the plateau in the north east.</summary>
        public static double Land(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            foreach (var k in Hills) h += GroundShapes.Hill(x, z, k.x, k.z, k.r, k.h);
            // Ridges along both banks of the basin, so it reads as a valley: the north one behind it, the south one
            // between it and the home square.
            h += GroundShapes.Ridge(x, z, 905, 846, 1060, 842, 34, 6.0);
            h += GroundShapes.Ridge(x, z, 925, 731, 1050, 727, 22, 5.0);
            double w = PlateauWeight(x, z);
            if (w >= 1) return EastPlan.Crest;
            return h + (EastPlan.Crest - h) * w;
        }

        /// <summary>1 on the plateau (exactly level there), easing to 0 over 30 m west and south of it.</summary>
        public static double PlateauWeight(double x, double z) =>
            GroundShapes.Smoothstep((x - (EastPlan.PlateauX0 - 30)) / 30) * GroundShapes.Smoothstep((z - (EastPlan.PlateauZ0 - 30)) / 30);

        // ---------------------------------------------------------------- the basin

        private static readonly (double x, double v)[] HalfWidth = { (900, 22), (925, 32), (955, 44), (1000, 50), (1050, 44), (1075, 36), (1092.3, 33) };
        private static readonly (double x, double v)[] BedLevel = { (905, 0.9), (960, -1.2), (1020, -3.3), (1078, EastPlan.Bed) };
        private static readonly (double x, double v)[] Middle = { (900, 784), (930, 782), (1000, 776), (1075, EastPlan.GorgeMid) };

        /// <summary>The reservoir's bed and banks (walled off east of the plateau's edge).</summary>
        public static double Basin(double x, double z)
        {
            double bed = Lerp(BedLevel, x), half = Lerp(HalfWidth, x), mid = Lerp(Middle, x);
            double bank = Math.Max(0, Math.Abs(z - mid) - half) * 0.42;
            return bed - Channel(x, z) + bank + Wall * Math.Max(0, x - (EastPlan.PlateauX0 - 1.5));
        }

        /// <summary>The old river's channel winding down the bed (2.1 m deep, 6 m wide at the bottom, 27 degree banks), gone
        /// before the throat.</summary>
        public static double Channel(double x, double z)
        {
            double fade = GroundShapes.Smoothstep((1072 - x) / 22) * GroundShapes.Smoothstep((x - 905) / 20);
            if (fade <= 0) return 0;
            double c = ChannelMid(x);
            double d = Math.Abs(z - c);
            return 2.1 * fade * (1 - GroundShapes.Smoothstep((d - 3) / 6));
        }

        /// <summary>The channel's middle line.</summary>
        public static double ChannelMid(double x) => Lerp(Middle, x) + 13 * Math.Sin((x - 900) / 34.0) * GroundShapes.Smoothstep((1080 - x) / 40);

        private static double Lerp((double x, double v)[] t, double x)
        {
            if (x <= t[0].x) return t[0].v;
            for (int i = 1; i < t.Length; i++)
                if (x <= t[i].x)
                {
                    double s = GroundShapes.Smoothstep((x - t[i - 1].x) / (t[i].x - t[i - 1].x));
                    return t[i - 1].v + (t[i].v - t[i - 1].v) * s;
                }
            return t[t.Length - 1].v;
        }
    }
}
