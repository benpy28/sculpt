namespace Sculpt.Core
{
    /// <summary>
    /// The physical part of a player that the world simulation cares about.
    /// Position is the centre of the feet (bottom of the collision box).
    /// </summary>
    public sealed class PlayerBody
    {
        public int Id;
        /// <summary>Team id, 1 or higher. Teammates share reinforcement rights.</summary>
        public int Team;
        public Vec3d Position;
        public Vec3d Velocity;

        /// <summary>Half the box width on X and Z, metres.</summary>
        public double HalfWidth = 0.35;
        /// <summary>Current box height, metres (changes when crouching or sliding).</summary>
        public double Height = 1.8;
        /// <summary>Current eye height above the feet, metres.</summary>
        public double EyeHeight = 1.62;

        public PlayerBody(int id, int team, Vec3d position)
        {
            Id = id;
            Team = team;
            Position = position;
        }

        public Vec3d Eye => new Vec3d(Position.X, Position.Y + EyeHeight, Position.Z);

        public Aabb Bounds => BoundsAt(Position);

        public Aabb BoundsAt(Vec3d feet) => new Aabb(
            new Vec3d(feet.X - HalfWidth, feet.Y, feet.Z - HalfWidth),
            new Vec3d(feet.X + HalfWidth, feet.Y + Height, feet.Z + HalfWidth));
    }
}
