namespace Sculpt.Core
{
    public enum SculptActionType : byte
    {
        /// <summary>Push or pull: the target block moves one cell along a horizontal direction.</summary>
        Move = 0,
        /// <summary>Add a block on top of the targeted column.</summary>
        Raise = 1,
        /// <summary>Remove the top block of the targeted column.</summary>
        Sink = 2,
        /// <summary>Toggle the target block's top between flat and a ramp low on Direction's side.</summary>
        Shape = 3,
    }

    /// <summary>A sculpt command as sent by a client. The server re-validates all of it.</summary>
    public struct SculptAction
    {
        public SculptActionType Type;
        /// <summary>The block the player aimed at.</summary>
        public Int3 Target;
        /// <summary>Move: direction the block travels. Shape: the side that becomes the ramp's low edge.</summary>
        public Dir Direction;
        public bool Reinforce;
        /// <summary>Point on the target block's surface under the crosshair (for line-of-sight checks).</summary>
        public Vec3d AimPoint;

        public static SculptAction Move(Int3 target, Dir dir, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Move, Target = target, Direction = dir, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Raise(Int3 target, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Raise, Target = target, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Sink(Int3 target, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Sink, Target = target, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Shape(Int3 target, Dir low, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Shape, Target = target, Direction = low, AimPoint = aim, Reinforce = reinforce };
    }

    public enum SculptResult : byte
    {
        Valid = 0,
        NoTarget,            // target cell is empty or out of the grid
        NotSculptable,       // anchor, bedrock, or outside the sculpt height range
        BadDirection,        // vertical move, or a non-horizontal ramp side
        DestinationBlocked,  // terrain in the destination cell
        OutOfHeightRange,    // raise above the max / sink below the min
        NotColumnTop,        // shape needs the top block of a column
        OutOfRange,
        NoLineOfSight,
        Locked,              // another player's windup holds this cell
        Busy,                // this player already has a windup running
        NotEnoughBar,
        PlayerBlocked,       // a player would have to be shoved into geometry
        Disturbed,           // the cells changed during windup (resolution only)
        NothingToReinforce,  // reinforced sink with no sculptable block left underneath
    }

    /// <summary>Everything the client needs to draw the ghost, and the server needs to start the windup.</summary>
    public struct SculptCheck
    {
        public SculptResult Result;
        public SculptActionType Type;
        /// <summary>The block that changes or moves (move source, column top for raise/sink, shaped block).</summary>
        public Int3 Source;
        /// <summary>Where the ghost goes: move destination, new block for raise, removed block for sink, shaped block.</summary>
        public Int3 Destination;
        public int Cost;
        public int WindupTicks;
        public bool EnemyReinforced;

        public bool IsValid => Result == SculptResult.Valid;
    }
}
