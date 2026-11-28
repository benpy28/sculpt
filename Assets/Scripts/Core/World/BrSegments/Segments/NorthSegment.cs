using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The North segment of the battle royale map (id 7, see <see cref="BrLayout"/>): the Train Yard.
    ///
    /// South to north: an embankment carrying the main line across the segment (cover from the centre's long sight
    /// lines; the south road passes under it on a breakable bridge), the yard (six tracks of parked trains with two
    /// raised island platforms, an engine shed straddling the west tracks, a footbridge from the embankment over every
    /// track to the station), the tracks running east into a rock cutting through Signal Ridge (a signal box on the
    /// ridge, a footbridge over the cut), and Junction Station at the anchor (the home POI: a two-storey station with
    /// a double-height concourse, a sealed ticket office and strongroom, a platform canopy). North of the station the
    /// spawn region's approach; west of it the goods yard (a long goods shed you breach along its blank side, a loading
    /// dock and siding, freight sheds, a water tower on its hill, railway cottages); east of it the signal tower and
    /// the container terminal (stacks of containers in rows, a gantry crane).
    ///
    /// Owned by its own design thread; the building code is in Segments/North/.
    /// </summary>
    public sealed class NorthSegment : BrSegment
    {
        public override int Id => 7;

        public override string HomePoiName => "Junction Station";

        private const double Base = BrGround.Base;

        /// <summary>The yard: flat at <see cref="Base"/>.</summary>
        public const double YardX0 = 470, YardX1 = 790, YardZ0 = 948, YardZ1 = 1018, YardEase = 20;
        /// <summary>The embankment: a 6 m bank along z <see cref="EmbZ"/>, an 11 m flat top, 14 m banks.</summary>
        public const double EmbZ = 930, EmbX0 = 492, EmbX1 = 808, EmbH = 6, EmbTopHalf = 5.5, EmbBank = 14;
        /// <summary>The road under the embankment (the south road port's line), between two rock abutments.</summary>
        public const double RoadX = 652.8, RoadHalf = 10;
        /// <summary>Signal Ridge, along z through the yard's east end, and the rock cutting the tracks run through.</summary>
        public const double RidgeX = 800, RidgeZ0 = 950, RidgeZ1 = 1020, RidgeHalf = 42, RidgeH = 14;
        public const double CutZ0 = 969.5, CutZ1 = 982.5, CutX0 = 742, CutX1 = 836, CutFaceDeg = 66;
        /// <summary>The container terminal's flat apron.</summary>
        public const double TermX0 = 744, TermX1 = 824, TermZ0 = 1070, TermZ1 = 1218, TermEase = 12;
        /// <summary>The water tower's hill.</summary>
        public const double WaterX = 490, WaterZ = 1186, WaterR = 34, WaterH = 6;

        public override double Ground(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            // The yard and the terminal are flat.
            h = Flatten(h, x, z, YardX0, YardZ0, YardX1, YardZ1, YardEase);
            h = Flatten(h, x, z, TermX0, TermZ0, TermX1, TermZ1, TermEase);
            // The water tower's hill.
            h += GroundShapes.Hill(x, z, WaterX, WaterZ, WaterR, WaterH);
            // The embankment, with rock abutments either side of the road under it.
            double e = Embankment(x, z);
            if (e > 0) h = Math.Max(h, Base + e);
            // Signal Ridge.
            double r = GroundShapes.Ridge(x, z, RidgeX, RidgeZ0, RidgeX, RidgeZ1, RidgeHalf, RidgeH);
            if (r > 0) h = Math.Max(h, Base + r);
            // The cutting: a level floor at the yard's level with rock faces either side.
            if (x > CutX0 && x < CutX1)
            {
                double dz = Math.Max(0, Math.Max(CutZ0 - z, z - CutZ1));
                h = Math.Min(h, Base + dz * Math.Tan(CutFaceDeg * Math.PI / 180));
            }
            return h;
        }

        private static double Flatten(double h, double x, double z, double x0, double z0, double x1, double z1, double ease)
        {
            double d = GroundShapes.ToRect(x, z, x0, z0, x1, z1);
            if (d >= ease) return h;
            return h + (Base - h) * (1 - GroundShapes.Smoothstep(d / ease));
        }

        /// <summary>The embankment's height over the base at (x, z): 0 off it.</summary>
        public static double Embankment(double x, double z)
        {
            double t = Math.Max(EmbX0, Math.Min(EmbX1, x));
            double d = Math.Sqrt((x - t) * (x - t) + (z - EmbZ) * (z - EmbZ));
            double e = EmbH * GroundShapes.Smoothstep((EmbTopHalf + EmbBank - d) / EmbBank);
            // Rock abutments (70 degrees) either side of the road gap.
            double gap = Math.Abs(x - RoadX) - RoadHalf;
            return Math.Max(0, Math.Min(e, gap * Math.Tan(70 * Math.PI / 180)));
        }

        public override void Build(BrBuild b) => NorthBuild.Build(b);
    }
}
