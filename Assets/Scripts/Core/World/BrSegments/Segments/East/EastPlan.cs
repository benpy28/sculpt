using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The East segment's plan (Dam and reservoir valley): the levels and rectangles its ground and buildings share.
    ///
    /// The big idea: a plateau at <see cref="Crest"/> with a gorge cut 24 m into it. The dam stands wedged across the gorge
    /// between real rock walls, its roof level with the plateau, so its crest is the bridge from the north plateau to the
    /// south one. Upstream (west) the gorge's throat opens into the dry reservoir basin, its silt bed at <see cref="Bed"/>
    /// (the dam's third storey: you walk in from the bed). Downstream (east) the canyon floor at <see cref="Floor"/> holds
    /// the power house and the tailrace yard and runs out into the rim's cliff. A spill chute runs down beside the dam
    /// from the bed to the canyon floor (a slide).
    ///
    /// Every edge between levels that must be exact lies on the ground field's vertex lines (<see cref="V"/>): the field
    /// is exactly level between them, and a level change between two vertex lines is a one-cell rock face.
    /// </summary>
    public static class EastPlan
    {
        /// <summary>The ground field's vertex lines are at 4.8 + 1.5 k on both axes.</summary>
        public static double V(int k) => 4.8 + 1.5 * k;
        /// <summary>The vertex index of a vertex line (rounded).</summary>
        public static int K(double x) => (int)Math.Round((x - 4.8) / 1.5);

        // ---- Levels (on the 0.3 m lattice).
        /// <summary>The canyon floor and the dam's ground floor.</summary>
        public const double Floor = -14.1;
        /// <summary>The reservoir's silt bed at the dam (the dam's third storey).</summary>
        public const double Bed = -4.5;
        /// <summary>The plateau, the dam's crest (its roof), the towers' roofs.</summary>
        public const double Crest = 9.9;
        public const double Storey = GreyboxBlock.StoreyHeight;

        // ---- The dam (5 storeys, Floor to Crest): x 1110.3 .. 1135.8, z 754.8 .. 811.8.
        public static readonly double DamX0 = V(737), DamX1 = V(754), DamZ0 = V(500), DamZ1 = V(538);
        public const int DamMx = 4, DamMz = 9, DamStoreys = 5;

        /// <summary>The gorge (between the plateau's rock walls): z 745.8 .. 811.8.</summary>
        public static readonly double GorgeZ0 = V(494), GorgeZ1 = V(538);
        /// <summary>The throat (the bed inside the gorge, upstream of the dam) starts here (its west edge is the plateau's).</summary>
        public static readonly double PlateauX0 = V(725);
        /// <summary>The spill chute: z 745.8 .. 753.3, falling from the bed at x 1095.3 to the canyon floor at the dam's downstream face.</summary>
        public static readonly double ChuteX0 = V(727), ChuteZ1 = V(499);
        /// <summary>The canyon runs from the dam out under the rim.</summary>
        public const double CanyonX1 = 1262;

        /// <summary>The plateau is exactly at <see cref="Crest"/> from here north (to the seam) and east (to the rim).</summary>
        public const double PlateauZ0 = 715, PlateauZ1 = 830.4;

        // ---- The power house: attached to the dam's downstream face, two storeys (Floor to Bed), its roof a terrace at Bed.
        public static double PowerX0 => DamX1;
        public static readonly double PowerZ0 = DamZ0 + 2 * GreyboxBlock.Module;
        public const int PowerMx = 6, PowerMz = 5;

        /// <summary>The middle of the basin's bed (and the gorge).</summary>
        public static double GorgeMid => (GorgeZ0 + GorgeZ1) / 2;

        /// <summary>The spill chute's floor at x (the bed at its mouth, the canyon floor at the dam's downstream face).</summary>
        public static double Chute(double x)
        {
            double t = (x - ChuteX0) / (DamX1 - ChuteX0);
            return Bed + (Floor - Bed) * Math.Max(0, Math.Min(1, t));
        }
    }
}
