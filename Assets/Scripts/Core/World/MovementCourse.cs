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
    /// The movement course on the side of the breach demo map: a raised route that only flows if you keep your speed.
    /// Tower top, a long stair flight to slide down (stairs collide as a ramp), a gap to slide-hop, a downhill landing
    /// ramp, a corner over a drop that needs a wall kick to turn, a corridor with a gap that needs a kick off the side
    /// wall, a 3 m ledge to mantle (and mantle-cancel), a 1.5 m block to vault, and a finish pad to brake on.
    /// Every top is on the 0.3 m lattice. Falling off just drops you to the ground; the tower stairs lead back up.
    /// </summary>
    public static class MovementCourse
    {
        public const double LaneWidth = 8.0;

        /// <summary>Builds the course with its lane starting at (x0, 0, z0) and running north (+Z), then east (+X).</summary>
        public static CourseInfo Build(BrushWorld w, double x0, double z0)
        {
            var info = new CourseInfo();
            double xa = x0, xb = x0 + LaneWidth, cx = x0 + LaneWidth / 2;
            Vec3d V(double x, double y, double z) => new Vec3d(x, y, z + z0);
            void Box(double ax, double ay, double az, double bx, double by, double bz, BrushKind k) =>
                w.AddBox(V(ax, ay, az), V(bx, by, bz), k);
            void Ramp(double ax, double ay, double az, double bx, double by, double bz, Dir rise, BrushKind k) =>
                w.AddRamp(V(ax, ay, az), V(bx, by, bz), rise, k);

            // A. Start tower, 18 m (four storeys), with a stair flight up its east side for walking back.
            Box(xa, 0, 0, xb, 18, 12, BrushKind.Frame);
            Ramp(xb, 0, 0, xb + 3, 18, 36, Dir.South, BrushKind.Stairs);
            info.Start = V(cx, 18.0, 2.0);
            info.StartYaw = 0;
            info.Marks.Add(new KeyValuePair<string, Vec3d>("start", info.Start));

            // B. The long flight down: 18 m -> 9 m over 18 m (27 degrees). Slide it to build speed.
            Ramp(xa, 9, 12, xb, 18, 30, Dir.South, BrushKind.Stairs);
            info.Marks.Add(new KeyValuePair<string, Vec3d>("stairs-top", V(cx, 18, 12)));
            Box(xa, 0, 12, xb, 9, 42, BrushKind.Frame);
            info.Marks.Add(new KeyValuePair<string, Vec3d>("stairs-bottom", V(cx, 9, 30)));

            // C. Deck at 9 m, then a 6 m gap to slide-hop.
            info.Marks.Add(new KeyValuePair<string, Vec3d>("gap-1", V(cx, 9, 42)));

            // D. Downhill landing ramp 9 m -> 4.5 m (20 degrees), then a short deck E at 4.5 m.
            const double e = 4.5;
            Ramp(xa, e, 48, xb, 9, 60, Dir.South, BrushKind.Ramp);
            Box(xa, 0, 48, xb, e, 68, BrushKind.Frame);
            info.Marks.Add(new KeyValuePair<string, Vec3d>("corner-edge", V(cx, e, 68)));

            // E. The corner: deck E ends over a drop; the far wall lets you kick to the east, onto deck F.
            double wallZ = 72;
            Box(xa - 4, 0, wallZ, xa + 62, 15, wallZ + 1, BrushKind.Wall);          // north wall of the corner and corridor
            info.Marks.Add(new KeyValuePair<string, Vec3d>("corner-wall", V(cx + 2, e, wallZ)));

            // F. Corridor east at 4.5 m with a 6 m gap (too far to hop at corridor speed); the north wall carries you over it with a kick.
            double fz0 = wallZ - 8, fz1 = wallZ;
            double f0 = xb + 1;                     // corner drop is x in [xb, f0]
            double gap0 = f0 + 16, gap1 = gap0 + 6;
            Box(f0, 0, fz0, gap0, e, fz1, BrushKind.Frame);
            Box(gap1, 0, fz0, gap1 + 12, e, fz1, BrushKind.Frame);
            Box(gap0 - 1, 0, fz0 - 1, gap1 + 1, 15, fz0, BrushKind.Wall);             // south wall over the gap
            info.Marks.Add(new KeyValuePair<string, Vec3d>("f-start", V(f0 + 2, e, (fz0 + fz1) / 2)));
            info.Marks.Add(new KeyValuePair<string, Vec3d>("gap-2", V(gap0, e, (fz0 + fz1) / 2)));
            info.Marks.Add(new KeyValuePair<string, Vec3d>("gap-2-end", V(gap1, e, (fz0 + fz1) / 2)));

            // G. A 3 m ledge up to deck G at 7.5 m: mantle it (jump again near the top to keep your speed).
            const double gy = e + 3;
            double g0 = gap1 + 12;
            Box(g0, 0, fz0, g0 + 30, gy, fz1, BrushKind.Frame);
            info.Marks.Add(new KeyValuePair<string, Vec3d>("ledge", V(g0, gy, (fz0 + fz1) / 2)));

            // H. A 1.5 m block across deck G to vault at speed.
            double v0 = g0 + 9;
            Box(v0, gy, fz0, v0 + 1, gy + 1.5, fz1, BrushKind.Wall);
            info.Marks.Add(new KeyValuePair<string, Vec3d>("vault", V(v0, gy, (fz0 + fz1) / 2)));

            // I. Finish pad: brake to a stop on it (counter-strafe).
            double p0 = v0 + 9;
            info.Finish = new Aabb(V(p0, gy, fz0), V(p0 + 5, gy + 2, fz1));
            w.Add(new Brush { Bounds = new Aabb(V(p0, gy, fz0), V(p0 + 5, gy + 0.02, fz1)), Kind = BrushKind.Marker, Solid = false });
            info.Marks.Add(new KeyValuePair<string, Vec3d>("finish", V(p0 + 2.5, gy, (fz0 + fz1) / 2)));
            return info;
        }
    }
}
