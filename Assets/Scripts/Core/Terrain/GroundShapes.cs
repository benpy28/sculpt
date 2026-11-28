using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Building blocks for smooth ground (<see cref="HeightField"/>): hills, bowls, ridges, rock-faced mesas and level
    /// footings. Each is a plain height function of a horizontal position; maps add or max them together. Slopes are kept
    /// to two bands (walkable hills under about 35 degrees, rock faces over 55), so nothing reads half way.
    /// </summary>
    public static class GroundShapes
    {
        public static double Smoothstep(double t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        /// <summary>A round hill: <paramref name="height"/> at the middle, 0 at <paramref name="radius"/> (cosine profile).
        /// Steepest slope = height x pi / (2 radius).</summary>
        public static double Hill(double x, double z, double cx, double cz, double radius, double height)
        {
            double r = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            return r >= radius ? 0 : height * 0.5 * (1 + Math.Cos(Math.PI * r / radius));
        }

        /// <summary>A long ridge between two points (rounded ends): a hill stretched along the segment.</summary>
        public static double Ridge(double x, double z, double ax, double az, double bx, double bz, double halfWidth, double height)
        {
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / l2)) : 0;
            return Hill(x, z, ax + dx * t, az + dz * t, halfWidth, height);
        }

        /// <summary>Distance from (x, z) to the rectangle (0 inside).</summary>
        public static double ToRect(double x, double z, double x0, double z0, double x1, double z1)
        {
            double dx = Math.Max(0, Math.Max(x0 - x, x - x1)), dz = Math.Max(0, Math.Max(z0 - z, z - z1));
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// A mesa: flat at <paramref name="height"/> over the rectangle, falling away at <paramref name="faceDegrees"/>
        /// (a rock face when steep) to nothing.
        /// </summary>
        public static double Mesa(double x, double z, double x0, double z0, double x1, double z1, double height, double faceDegrees)
        {
            double d = ToRect(x, z, x0, z0, x1, z1);
            return Math.Max(0, height - d * Math.Tan(faceDegrees * Math.PI / 180));
        }

        /// <summary>
        /// A level footing for a building (Ben's plan: buildings stand on level ground cut into the hill, walking up to a
        /// door never needs a jump): exactly <paramref name="level"/> over the rectangle grown by <paramref name="apron"/>,
        /// then cut and filled like real earthworks: the ground is held within a batter of <paramref name="batterDegrees"/>
        /// of the footing (a walkable bank), and is the natural ground wherever that is already inside the batter. So
        /// the footing never adds a slope steeper than the batter.
        /// </summary>
        public static double Footing(double natural, double x, double z, double x0, double z0, double x1, double z1, double level, double apron, double batterDegrees = 28)
        {
            double d = Math.Max(0, ToRect(x, z, x0, z0, x1, z1) - apron);
            double reach = d * Math.Tan(batterDegrees * Math.PI / 180);
            return Math.Max(level - reach, Math.Min(level + reach, natural));
        }
    }
}
