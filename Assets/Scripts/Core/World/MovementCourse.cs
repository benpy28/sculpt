using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Where the movement course is, for the warp key, the showcase run and the look.</summary>
    public sealed class CourseInfo
    {
        /// <summary>Feet position and yaw the showcase run starts from.</summary>
        public Vec3d Start;
        public double StartYaw;
        /// <summary>Stop inside this box to finish.</summary>
        public Aabb Finish;
        /// <summary>Named spots along the route, in order (the showcase steers by them).</summary>
        public readonly List<KeyValuePair<string, Vec3d>> Marks = new List<KeyValuePair<string, Vec3d>>();

        public Vec3d Mark(string name)
        {
            foreach (var kv in Marks) if (kv.Key == name) return kv.Value;
            throw new KeyNotFoundException(name);
        }
    }

    /// <summary>
    /// The movement course on the west side of the breach demo map (Ben 2026-11-25: "expand the TAS movement map so its
    /// closed"): an enclosed two-lane circuit, 12 m walls all round, that shows every core move. Lane A runs north: a
    /// start tower and a long stair flight to slide down, a low beam you can only slide under, a kicker ramp to launch
    /// off onto a downhill landing ramp, a 3 m ledge to climb (and climb-cancel), a 1.5 m block on its deck, a short
    /// flight down into the north room. The north room is a U-turn round the end of the divider, made for carving a
    /// slide. Lane B runs south: five walls with doors on alternate sides (a slalom of carves), a 3 m ledge and a steep downhill, a second low
    /// beam and the finish pad to brake on, beside the start. Nothing drops you out: every miss lands on the floor,
    /// which always leads on. A door in the east wall by the finish lets you walk in. Every top is on the 0.3 m lattice.
    /// </summary>
    public static class MovementCourse
    {
        public const double LaneWidth = 13.0;
        public const double WallHeight = 15.0;
        public const double DoorWidth = 4.5;
        public const int SlalomDoors = 5;

        /// <summary>Builds the course with its south-west corner at (x0, 0, z0).</summary>
        public static CourseInfo Build(BrushWorld w, double x0, double z0)
        {
            var info = new CourseInfo();
            Vec3d V(double x, double y, double z) => new Vec3d(x + x0, y, z + z0);
            void Box(double ax, double ay, double az, double bx, double by, double bz, BrushKind k) =>
                w.AddBox(V(ax, ay, az), V(bx, by, bz), k);
            void Ramp(double ax, double ay, double az, double bx, double by, double bz, Dir rise, BrushKind k) =>
                w.AddRamp(V(ax, ay, az), V(bx, by, bz), rise, k);
            void Mark(string name, double x, double y, double z) => info.Marks.Add(new KeyValuePair<string, Vec3d>(name, V(x, y, z)));
            const double H = WallHeight;
            // Lanes: A is x 1..14 (centre 7.5), B is x 15..27 (centre 21). The divider is x 14..15.
            const double a0 = 1, a1 = 14, b0 = 15, b1 = 27, ca = 7.5, cb = 21;
            const double north = 147;

            // ---- Enclosure ----
            Box(0, 0, 0, 1, H, north + 1, BrushKind.Wall);           // west
            Box(27, 0, 0, 28, H, 12, BrushKind.Wall);                // east, with a 3 m door at z 12..15
            Box(27, 3, 12, 28, H, 15, BrushKind.Wall);
            Box(27, 0, 15, 28, H, north + 1, BrushKind.Wall);
            Box(1, 0, 0, 27, H, 1, BrushKind.Wall);                  // south
            Box(1, 0, north, 27, H, north + 1, BrushKind.Wall);      // north
            Box(a1, 0, 1, b0, H, 128, BrushKind.Wall);               // divider
            Mark("door", 27.5, 0, 13.5);

            // ---- Lane A, northbound ----
            // A1. Start tower, 10.5 m.
            Box(a0, 0, 1, a1, 10.5, 10, BrushKind.Frame);
            info.Start = V(ca, 10.5, 3.0);
            info.StartYaw = 0;
            Mark("start", ca, 10.5, 3);
            Mark("stairs-top", ca, 10.5, 10);
            // A2. The long flight: 6 m down over 12 m (26.6 degrees) to a deck at 4.5 m. Slide it to build speed.
            Ramp(a0, 4.5, 10, a1, 10.5, 22, Dir.South, BrushKind.Stairs);
            Box(a0, 0, 10, a1, 4.5, 44, BrushKind.Frame);
            Mark("stairs-bottom", ca, 4.5, 22);
            // A3. A low beam across the deck: 1.35 m clear under it, so only a slide (1.1 m) gets through.
            Box(a0, 4.5 + 1.35, 31, a1, 9, 32, BrushKind.Frame);
            Mark("beam-1", ca, 4.5, 31);
            // A4. The deck ends at a gap: slide-jump off its lip onto a downhill landing ramp (3 m to the floor over
            // 12 m) 8 m away. Short of it you hit the ramp's 3 m back and climb it.
            Mark("lip", ca, 4.5, 44);
            Ramp(a0, 0, 52, a1, 3, 64, Dir.South, BrushKind.Ramp);
            Mark("landing", ca, 3, 52);
            // A5. A 3 m ledge (two tiles) up to a deck, a 1.5 m block on it (one tile), then a short flight down.
            Box(a0, 0, 82, a1, 3, 100, BrushKind.Frame);
            Mark("ledge", ca, 3, 82);
            Box(a0, 3, 95, a1, 4.5, 95.6, BrushKind.Wall);
            Mark("block", ca, 4.5, 95);
            Ramp(a0, 0, 100, a1, 3, 106, Dir.South, BrushKind.Stairs);
            Mark("flight-2-bottom", ca, 0, 106);

            // ---- North room: a U-turn round a thick pillar at the end of the divider ----
            Box(12, 0, 128, 17, H, 135, BrushKind.Wall);
            Mark("turn-centre", 14.5, 0, 131.5);

            // ---- Lane B, southbound ----
            // B1. Slalom: five walls across the lane, each with a 4.5 m door, on alternate sides.
            double[] wallZ = { 122, 110, 98, 86, 74 };
            for (int i = 0; i < wallZ.Length; i++)
            {
                bool west = i % 2 == 0;
                double d0 = west ? b0 : b1 - DoorWidth, d1 = d0 + DoorWidth;
                if (d0 > b0) Box(b0, 0, wallZ[i], d0, H, wallZ[i] + 0.6, BrushKind.Wall);
                if (d1 < b1) Box(d1, 0, wallZ[i], b1, H, wallZ[i] + 0.6, BrushKind.Wall);
                Mark("door-" + (i + 1), (d0 + d1) / 2, 0, wallZ[i] + 0.3);
            }
            // B2. A 3 m ledge up to a platform (climb it with the speed you hit it with), then a steep downhill: 3 m down
            // over 9 m (18 degrees) to build speed again.
            Box(b0, 0, 59, b1, 3, 66, BrushKind.Frame);
            Ramp(b0, 0, 50, b1, 3, 59, Dir.North, BrushKind.Ramp);
            Mark("hill-foot", cb, 0, 66);
            Mark("hill-bottom", cb, 0, 50);
            // B3. A second low beam, then the finish pad beside the start (brake on it: counter-strafe).
            Box(b0, 1.35, 28, b1, 4.5, 29, BrushKind.Frame);
            Mark("beam-2", cb, 0, 29);
            info.Finish = new Aabb(V(b0, 0, 1), V(b1, 2, 10));
            w.Add(new Brush { Bounds = new Aabb(V(b0, 0, 1), V(b1, 0.02, 10)), Kind = BrushKind.Marker, Solid = false });
            Mark("finish", cb, 0, 6);
            return info;
        }
    }
}
