using System;

namespace Sculpt.Core
{
    /// <summary>Double-precision vector used by Core maths. X = east, Y = up, Z = north. Metres.</summary>
    public readonly struct Vec3d : IEquatable<Vec3d>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3d Zero = new Vec3d(0, 0, 0);

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator -(Vec3d a) => new Vec3d(-a.X, -a.Y, -a.Z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);
        public static Vec3d operator *(double s, Vec3d a) => a * s;
        public static Vec3d operator /(Vec3d a, double s) => new Vec3d(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3d b) => X * b.X + Y * b.Y + Z * b.Z;
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);
        public double HorizontalLength => Math.Sqrt(X * X + Z * Z);

        public Vec3d Normalized
        {
            get
            {
                double l = Length;
                return l > 1e-12 ? this / l : Zero;
            }
        }

        public Vec3d WithY(double y) => new Vec3d(X, y, Z);

        public static Vec3d FromInt3(Int3 v) => new Vec3d(v.X, v.Y, v.Z);

        public bool Equals(Vec3d o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec3d o && Equals(o);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }
}
