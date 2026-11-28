using System;

namespace Sculpt.Core
{
    /// <summary>
    /// The spawn region every outer segment holds (framework-owned: segment threads don't change it). A two-storey
    /// building, 4 x 9 modules, its front facing the segment's home anchor: four pad rooms along the front (open
    /// doorways), the entrance hall with the stairwell in the middle lane, back rooms, a shut back door, first-floor
    /// wings. Very little loot: one chest outside each pad room's doorway. Every pad is <see cref="PadToAnchor"/> from
    /// the anchor and has the same straight walk out of its doorway to its chest (the inner pads sit deeper in their
    /// rooms by exactly what evens the distance).
    ///
    /// Built twice as a template (an edge one east of the map's centre, a corner one in the north-east) and stamped into
    /// each outer segment by <see cref="BrLayout.KitSymmetry"/>, so all eight are the same building, turned or mirrored.
    /// </summary>
    public static class BrSpawnKit
    {
        /// <summary>The building's front is this far from the anchor (toward the kit).</summary>
        public const double FrontOffset = 154;
        /// <summary>Building size in modules: depth (toward the rim) and length (across).</summary>
        public const int Depth = 4, Length = 9;
        /// <summary>The pad rooms' doorway segments along the front.</summary>
        private static readonly int[] PadSeg = { 0, 2, 6, 8 };
        /// <summary>How far in front of the building the footing (and the pads' chests) reaches.</summary>
        public const double FrontApron = 12;

        /// <summary>Every pad's distance from its anchor (outer pads 4 m into their rooms, 25.2 m off the centre line).</summary>
        public static double PadToAnchor => Math.Sqrt((FrontOffset + 4) * (FrontOffset + 4) + 25.2 * 25.2);

        /// <summary>The template's pads, chests and squad spots (map coordinates, before stamping).</summary>
        public sealed class Template
        {
            public Aabb Building;
            /// <summary>The level ground the kit stands on (building plus the front apron with the chests), without the footing's own apron.</summary>
            public Rect2 Plot;
            public readonly Vec3d[] Pads = new Vec3d[4], Loot = new Vec3d[4], Squad = new Vec3d[4];
            /// <summary>Pads face the doorway (toward the anchor: -x in the template).</summary>
            public double PadYaw = -Math.PI / 2;
            public Vec3d Anchor;
        }

        /// <summary>Builds the template kit (edge or corner) into <paramref name="w"/> on level ground at <see cref="BrGround.Base"/>.</summary>
        public static Template Build(BrushWorld w, GreyboxInfo info, bool corner)
        {
            const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column;
            double c = BrLayout.MapCentre;
            BrLayout.TemplateAnchor(corner, out double au, out double av);
            var t = new Template { Anchor = new Vec3d(c + au, BrGround.Base, c + av) };
            double span = Length * M + Col;
            var rb = new GreyboxBlock.Building(t.Anchor.X + FrontOffset, t.Anchor.Z - span / 2, Depth, Length) { Y0 = BrGround.Base };
            GreyboxBlock.Stairwell(w, info, rb, 4, false, -1);
            for (int s = 0; s < 2; s++)
                foreach (int line in new[] { 2, 4, 5, 7 })
                    for (int i = 0; i < 4; i++) rb.AlongX[s, line, i] = WallKind.Wall; // between the rooms, all the way back
            for (int j = 0; j < Length; j++)
            {
                if (j == 4) continue;
                rb.AlongZ[0, 2, j] = j == 1 || j == 3 || j == 5 || j == 7 ? WallKind.Door : WallKind.Wall; // front room <-> back room
                rb.AlongZ[0, 0, j] = Array.IndexOf(PadSeg, j) >= 0 ? WallKind.Door : WallKind.Window;     // the front
            }
            rb.AlongZ[0, 0, 4] = WallKind.WideDoor;  // the main entrance into the hall
            rb.AlongZ[0, 4, 4] = WallKind.ShutDoor;  // back door out of the stair lane
            for (int j = 0; j < Length; j++)
            {
                rb.AlongZ[1, 0, j] = j == 4 ? WallKind.Wall : WallKind.Window;
                rb.AlongZ[1, 4, j] = j % 3 == 1 ? WallKind.Window : WallKind.Wall;
                if (j != 4) rb.AlongZ[1, 2, j] = WallKind.None;
            }
            foreach (int line in new[] { 2, 7 }) for (int i = 0; i < 4; i++) rb.AlongX[1, line, i] = WallKind.None;
            rb.AlongX[1, 4, 1] = WallKind.Door;
            rb.AlongX[1, 4, 3] = WallKind.Door;      // round the stair hole to where the upper flight starts
            rb.AlongX[1, 5, 1] = WallKind.Door;
            rb.AlongX[1, 0, 1] = WallKind.Window;
            rb.AlongX[1, 9, 1] = WallKind.Window;
            GreyboxBlock.Raise(w, info, rb, "Spawn region");
            t.Building = rb.Bounds;
            t.Plot = new Rect2(rb.X0 - FrontApron, rb.Z0, rb.Cx(Depth) + Col, rb.Cz(Length) + Col);

            // Pads: outer two 4 m into their rooms, the inner two deeper by what evens the distance to the anchor.
            double front = rb.X0, y = rb.Y0, r2 = PadToAnchor * PadToAnchor, deepest = 0;
            for (int k = 0; k < 4; k++)
            {
                double z = rb.Cz(PadSeg[k]) + Col + 3.0, dz = z - t.Anchor.Z;
                double x = t.Anchor.X + Math.Sqrt(Math.Max(0, r2 - dz * dz));
                t.Pads[k] = new Vec3d(x, y, z);
                deepest = Math.Max(deepest, x);
            }
            // Loot: straight out of the doorway, the same walk for every pad (the deepest one's chest 6 m outside).
            double walk = deepest - (front - 6.0);
            for (int k = 0; k < 4; k++) t.Loot[k] = t.Pads[k] - new Vec3d(walk, 0, 0);
            // Squads: together in the entrance hall.
            double hx = rb.Cx(0) + Col + 3.0, hz = rb.Cz(4) + Col + 3.0;
            t.Squad[0] = new Vec3d(hx - 1.2, y, hz - 1.2);
            t.Squad[1] = new Vec3d(hx - 1.2, y, hz + 1.2);
            t.Squad[2] = new Vec3d(hx + 1.2, y, hz - 1.2);
            t.Squad[3] = new Vec3d(hx + 1.2, y, hz + 1.2);
            return t;
        }

        /// <summary>
        /// The four pad lanes of the template (loot chest to the home plaza's edge), each <see cref="BrLayout.PadLaneWidth"/>
        /// wide, as segments (start, end). Segment designs keep them clear.
        /// </summary>
        public static void Lane(Template t, int k, out Vec3d from, out Vec3d to)
        {
            from = t.Loot[k];
            Vec3d d = (t.Anchor - from).WithY(0);
            double len = d.HorizontalLength;
            to = from + d * ((len - BrLayout.HomeRadius) / len);
        }
    }
}
