namespace Sculpt.Core
{
    public enum SculptEventType : byte
    {
        /// <summary>A windup started. WarnedPlayers lists everyone standing on or touching the block.</summary>
        Began,
        Completed,
        /// <summary>A windup was cancelled at resolution and its cost refunded. Result says why.</summary>
        Cancelled,
        /// <summary>A stack of blocks dropped one cell. Cell = new bottom of the stack.</summary>
        BlockFell,
        /// <summary>A player was shoved or lifted by a sculpt. Cell = the block that pushed them.</summary>
        PlayerShoved,
    }

    public struct SculptEvent
    {
        public SculptEventType Type;
        public int Tick;
        /// <summary>Sculptor (Began/Completed/Cancelled) or the shoved player (PlayerShoved).</summary>
        public int PlayerId;
        public SculptAction Action;
        public SculptCheck Check;
        public SculptResult Result;
        public Int3 Cell;
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
        /// <summary>Set if anything other than this action wrote to a locked cell during windup.</summary>
        public bool Disturbed;
    }
}
