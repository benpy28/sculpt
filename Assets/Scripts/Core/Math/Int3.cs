using System;

namespace Sculpt.Core
{
    /// <summary>Integer cell coordinate. X = east, Y = up, Z = north.</summary>
    public readonly struct Int3 : IEquatable<Int3>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public Int3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Int3 Zero = new Int3(0, 0, 0);
        public static readonly Int3 Up = new Int3(0, 1, 0);
        public static readonly Int3 Down = new Int3(0, -1, 0);

        public static Int3 operator +(Int3 a, Int3 b) => new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Int3 operator -(Int3 a, Int3 b) => new Int3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Int3 operator -(Int3 a) => new Int3(-a.X, -a.Y, -a.Z);
        public static Int3 operator *(Int3 a, int s) => new Int3(a.X * s, a.Y * s, a.Z * s);
        public static bool operator ==(Int3 a, Int3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(Int3 a, Int3 b) => !(a == b);

        public bool Equals(Int3 other) => this == other;
        public override bool Equals(object obj) => obj is Int3 o && this == o;
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));
        public override string ToString() => $"({X}, {Y}, {Z})";

        /// <summary>Deterministic ordering: Y, then Z, then X.</summary>
        public static int CompareYZX(Int3 a, Int3 b)
        {
            if (a.Y != b.Y) return a.Y.CompareTo(b.Y);
            if (a.Z != b.Z) return a.Z.CompareTo(b.Z);
            return a.X.CompareTo(b.X);
        }
    }
}
