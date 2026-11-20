namespace Sculpt.Core
{
    public enum SculptActionType : byte
    {
        /// <summary>Push or pull: the piece moves one tile (1.2 m) along a horizontal direction.</summary>
        Move = 0,
        /// <summary>The piece rises one level; the level it leaves is filled with rock (the column grows).</summary>
        Raise = 1,
        /// <summary>The piece sinks one level into the natural rock under it (the column shrinks).</summary>
        Sink = 2,
        /// <summary>The piece is torn out and thrown along the look direction.</summary>
        Throw = 3,
    }

    /// <summary>A sculpt command as sent by a client. The server re-validates all of it.</summary>
    public struct SculptAction
    {
        public SculptActionType Type;
        /// <summary>The sub-cell the player aimed at (the solid sub-cell under the crosshair).</summary>
        public Int3 Target;
        /// <summary>Move: direction the piece travels (horizontal only).</summary>
        public Dir Direction;
        public bool Reinforce;
        /// <summary>Point on the surface under the crosshair (for range and line-of-sight checks).</summary>
        public Vec3d AimPoint;
        /// <summary>Throw: the look direction at confirm. Locked for the whole windup.</summary>
        public Vec3d LookDir;

        public static SculptAction Move(Int3 target, Dir dir, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Move, Target = target, Direction = dir, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Raise(Int3 target, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Raise, Target = target, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Sink(Int3 target, Vec3d aim, bool reinforce = false) =>
            new SculptAction { Type = SculptActionType.Sink, Target = target, AimPoint = aim, Reinforce = reinforce };

        public static SculptAction Throw(Int3 target, Vec3d aim, Vec3d look) =>
            new SculptAction { Type = SculptActionType.Throw, Target = target, AimPoint = aim, LookDir = look };
    }

    public enum SculptResult : byte
    {
        Valid = 0,
        NoTarget,            // aimed sub-cell is empty or out of the grid
        NotSculptable,       // anchor, bedrock, or outside the sculpt height range
        BadDirection,        // vertical move
        DestinationBlocked,  // terrain where the piece would go
        OutOfHeightRange,    // raise above +4 / sink below -2
        OutOfRange,
        NoLineOfSight,
        Locked,              // another player's windup holds these sub-cells (free; buffered)
        Busy,                // this player already has a windup or a throw in flight (free; buffered)
        NotEnoughBar,
        PlayerBlocked,       // a player would have to be shoved into geometry
        Disturbed,           // the sub-cells changed during windup (resolution only)
        CannotThrowReinforced,
        /// <summary>Network layer: the confirm arrived while the player was not (yet) in sculpt mode, or was disarmed.</summary>
        NotInSculptMode,
        /// <summary>Network layer: the confirm reached the server too late to apply (or never did, as the client decided).</summary>
        TooLate,
        /// <summary>A queued confirm was replaced by a newer one (or dropped because you left sculpt mode). Silent: no red.</summary>
        Superseded,
    }

    /// <summary>Everything the client needs to draw the ghost, and the server needs to start the windup.</summary>
    public struct SculptCheck
    {
        public SculptResult Result;
        public SculptActionType Type;
        /// <summary>Existing piece id, or 0 if the piece is natural rock that will be carved on confirm.</summary>
        public ushort PieceId;
        /// <summary>The piece's sub-cells (sorted Y, Z, X). The ghost outline.</summary>
        public Int3[] Cells;
        /// <summary>Sub-cell translation of the piece (zero for a throw). Ghost = Cells + Delta.</summary>
        public Int3 Delta;
        /// <summary>The sub-cell the action really works on (raise and sink move the aim to the column top).</summary>
        public Int3 Target;
        public int Cost;
        public int WindupTicks;
        public bool EnemyReinforced;
        /// <summary>Weapon layer: the gun stays locked during this windup (attacking an enemy piece, or a throw).</summary>
        public bool LocksGun;

        public int Size => Cells?.Length ?? 0;
        public bool IsValid => Result == SculptResult.Valid;
    }
}
