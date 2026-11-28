using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The North West segment of the battle royale map (id 6, x 0..435.2, z 870.4..1305.6): Farmstead country and
    /// Radio Mast Hill. Its spawn region stands west of the home anchor (389.76, 915.84) along the south border.
    ///
    /// Ground: the shared roll, plus Radio Mast Hill (a 29 m dome with a flat summit, about 30 degrees on its flanks)
    /// filling the north-west quarter, a rock-faced crag shelf on the hill's north-west flank, a gentle swell in the
    /// fields with a burnt-out farm on top, a dew pond, and the Holloway: a sunken lane 3.2 m deep from the east road
    /// port west to the hill's foot (cover across open ground).
    ///
    /// The buildings, loot and props are laid out in <see cref="NwBuild"/>, the building kit is <see cref="NwKit"/>.
    /// </summary>
    public sealed class NorthWestSegment : BrSegment
    {
        public override int Id => 6;

        public override string HomePoiName => "Greyfield Farm";

        // ---- Ground features (map coordinates).
        public const double HillX = 190, HillZ = 1118, HillFlat = 24, HillFall = 78, HillHeight = 29;
        /// <summary>The crag: a flat rock shelf on the hill's north-west flank, rock faces on its downhill sides.</summary>
        public const double CragX0 = 116, CragZ0 = 1146, CragX1 = 148, CragZ1 = 1176, CragTop = 20.1, CragFace = 66;
        public const double SwellX = 315, SwellZ = 1106, SwellR = 42, SwellH = 4;
        public const double PondX = 104, PondZ = 1052, PondR = 22, PondDepth = 3;
        /// <summary>Tors: rock outcrops on the hill's flanks (centre, half sizes, height above the hill at the centre):
        /// blocks of rock standing out of the slope with faces all round, cover for anyone climbing.</summary>
        public static readonly (double x, double z, double hx, double hz, double h)[] Tors =
        {
            (150, 1080, 4, 5, 6.0), (238, 1148, 5, 4, 5.5), (214, 1068, 4, 3.5, 5.5), (162, 1172, 3.5, 4, 6.0), (128, 1118, 4, 4, 5.5),
        };
        /// <summary>The Holloway's centre line, east to west.</summary>
        public static readonly (double x, double z)[] Holloway = { (440, 1049.6), (395, 1049.6), (345, 1050.5), (305, 1054), (280, 1063), (262, 1078) };
        /// <summary>The Holloway's depth, floor half width, bank width, and how far it fades out at its west end.</summary>
        public const double LaneDepth = 3.2, LaneFloor = 4, LaneBank = 10, LaneFade = 30;

        public override double Ground(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            h += PlateauHill(x, z, HillX, HillZ, HillFlat, HillFall, HillHeight);
            h += GroundShapes.Hill(x, z, SwellX, SwellZ, SwellR, SwellH);
            h -= GroundShapes.Hill(x, z, PondX, PondZ, PondR, PondDepth);
            foreach (var t in Tors)
            {
                h += GroundShapes.Mesa(x, z, t.x - t.hx, t.z - t.hz, t.x + t.hx, t.z + t.hz, t.h, 68);
            }
            double crag = CragTop - GroundShapes.ToRect(x, z, CragX0, CragZ0, CragX1, CragZ1) * Math.Tan(CragFace * Math.PI / 180);
            if (crag > h) h = crag;
            h -= LaneCut(x, z);
            return h;
        }

        /// <summary>A hill with a flat top: <paramref name="height"/> within <paramref name="flat"/>, a cosine fall to nothing over <paramref name="fall"/> more.</summary>
        public static double PlateauHill(double x, double z, double cx, double cz, double flat, double fall, double height)
        {
            double r = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            if (r <= flat) return height;
            if (r >= flat + fall) return 0;
            return height * 0.5 * (1 + Math.Cos(Math.PI * (r - flat) / fall));
        }

        /// <summary>How deep the Holloway cuts at a point (0 away from it): full depth on its floor, easing up its banks,
        /// fading out over the last <see cref="LaneFade"/> metres at its west end.</summary>
        public static double LaneCut(double x, double z)
        {
            double best = double.PositiveInfinity, bestAlong = 0, along = 0;
            for (int k = 0; k + 1 < Holloway.Length; k++)
            {
                var (ax, az) = Holloway[k];
                var (bx, bz) = Holloway[k + 1];
                double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz, l = Math.Sqrt(l2);
                double t = Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / l2));
                double px = ax + dx * t - x, pz = az + dz * t - z, d = Math.Sqrt(px * px + pz * pz);
                if (d < best) { best = d; bestAlong = along + t * l; }
                along += l;
            }
            if (best >= LaneFloor + LaneBank) return 0;
            double fade = GroundShapes.Smoothstep((along - bestAlong) / LaneFade);
            return LaneDepth * fade * (1 - GroundShapes.Smoothstep((best - LaneFloor) / LaneBank));
        }

        public override void Build(BrBuild b) => NwBuild.Build(b);
    }
}
