using System;

namespace Sculpt.Core
{
    /// <summary>The six world axis directions. No diagonals exist anywhere in the game.</summary>
    public enum Dir : byte
    {
        East = 0,  // +X
        West = 1,  // -X
        Up = 2,    // +Y
        Down = 3,  // -Y
        North = 4, // +Z
        South = 5, // -Z
    }

    public static class DirUtil
    {
        public static readonly Dir[] Horizontal = { Dir.East, Dir.West, Dir.North, Dir.South };

        public static Int3 ToInt3(this Dir d)
        {
            switch (d)
            {
                case Dir.East: return new Int3(1, 0, 0);
                case Dir.West: return new Int3(-1, 0, 0);
                case Dir.Up: return new Int3(0, 1, 0);
                case Dir.Down: return new Int3(0, -1, 0);
                case Dir.North: return new Int3(0, 0, 1);
                case Dir.South: return new Int3(0, 0, -1);
                default: throw new ArgumentOutOfRangeException(nameof(d));
            }
        }

        public static Vec3d ToVec(this Dir d) => Vec3d.FromInt3(d.ToInt3());

        public static Dir Opposite(this Dir d)
        {
            switch (d)
            {
                case Dir.East: return Dir.West;
                case Dir.West: return Dir.East;
                case Dir.Up: return Dir.Down;
                case Dir.Down: return Dir.Up;
                case Dir.North: return Dir.South;
                case Dir.South: return Dir.North;
                default: throw new ArgumentOutOfRangeException(nameof(d));
            }
        }

        public static bool IsHorizontal(this Dir d) => d != Dir.Up && d != Dir.Down;

        /// <summary>Maps a unit axis vector to a direction. Throws if it isn't one.</summary>
        public static Dir FromInt3(Int3 v)
        {
            if (v == new Int3(1, 0, 0)) return Dir.East;
            if (v == new Int3(-1, 0, 0)) return Dir.West;
            if (v == new Int3(0, 1, 0)) return Dir.Up;
            if (v == new Int3(0, -1, 0)) return Dir.Down;
            if (v == new Int3(0, 0, 1)) return Dir.North;
            if (v == new Int3(0, 0, -1)) return Dir.South;
            throw new ArgumentException("Not an axis direction: " + v);
        }
    }
}
