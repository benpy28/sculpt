namespace Sculpt.Core
{
    public enum SculptEventType : byte
    {
        /// <summary>A windup started. WarnedPlayers lists everyone standing on or touching the piece (and, for a throw, enemies in its lane).</summary>
        Began,
        Completed,
        /// <summary>A windup was cancelled at resolution and its cost refunded. Result says why.</summary>
        Cancelled,
        /// <summary>A piece dropped one sub-cell. Piece = its id.</summary>
        PieceFell,
        /// <summary>A falling piece came to rest.</summary>
        PieceLanded,
        /// <summary>A player was shoved or lifted by a sculpt. PlayerId = the shoved player.</summary>
        PlayerShoved,
        /// <summary>A player standing on a piece that moved sideways got a friction kick. PlayerId = that player.</summary>
        PlayerDragged,
        /// <summary>A piece left the ground as a projectile. Value = projectile id.</summary>
        Thrown,
        /// <summary>A thrown piece broke. Point = impact position.</summary>
        ThrowImpact,
        /// <summary>A thrown piece hit a player. PlayerId = victim, Value = damage, Disarm = ticks without weapons or sculpting.</summary>
        ThrowHit,
    }

    public struct SculptEvent
    {
        public SculptEventType Type;
        public int Tick;
        /// <summary>Sculptor (Began/Completed/Cancelled/Thrown/ThrowImpact) or the affected player.</summary>
        public int PlayerId;
        public SculptAction Action;
        public SculptCheck Check;
        public SculptResult Result;
        public ushort Piece;
        public int Value;
        public int Disarm;
        public Vec3d Point;
        public int[] WarnedPlayers;
    }

    /// <summary>A windup in progress.</summary>
    public sealed class PendingSculpt
    {
        public int PlayerId;
        public SculptAction Action;
        public SculptCheck Check;
        public int StartTick;
        public int CompleteTick;
        public Int3[] LockedCells;
        /// <summary>Set if anything other than this action wrote to a locked sub-cell during windup.</summary>
        public bool Disturbed;
    }
}
