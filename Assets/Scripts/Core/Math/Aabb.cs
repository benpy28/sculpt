namespace Sculpt.Core
{
    /// <summary>Axis-aligned box in metres.</summary>
    public readonly struct Aabb
    {
        /// <summary>Boxes closer than this are treated as touching, not overlapping.</summary>
        public const double Epsilon = 1e-6;

        public readonly Vec3d Min;
        public readonly Vec3d Max;

        public Aabb(Vec3d min, Vec3d max)
        {
            Min = min;
            Max = max;
        }

        public Vec3d Center => (Min + Max) * 0.5;

        public Aabb Translate(Vec3d d) => new Aabb(Min + d, Max + d);

        public Aabb Expand(double m) => new Aabb(Min - new Vec3d(m, m, m), Max + new Vec3d(m, m, m));

        /// <summary>True if the interiors overlap by more than Epsilon on every axis.</summary>
        public bool Overlaps(Aabb o) =>
            Min.X < o.Max.X - Epsilon && Max.X > o.Min.X + Epsilon &&
            Min.Y < o.Max.Y - Epsilon && Max.Y > o.Min.Y + Epsilon &&
            Min.Z < o.Max.Z - Epsilon && Max.Z > o.Min.Z + Epsilon;

        public bool Contains(Vec3d p, double tolerance) =>
            p.X >= Min.X - tolerance && p.X <= Max.X + tolerance &&
            p.Y >= Min.Y - tolerance && p.Y <= Max.Y + tolerance &&
            p.Z >= Min.Z - tolerance && p.Z <= Max.Z + tolerance;

        public Vec3d ClosestPoint(Vec3d p) => new Vec3d(
            Clamp(p.X, Min.X, Max.X),
            Clamp(p.Y, Min.Y, Max.Y),
            Clamp(p.Z, Min.Z, Max.Z));

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        public override string ToString() => $"[{Min} .. {Max}]";
    }
}
