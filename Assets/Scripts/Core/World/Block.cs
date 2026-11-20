namespace Sculpt.Core
{
    public enum BlockKind : byte
    {
        Empty = 0,
        /// <summary>Normal terrain. Sculptable if inside the sculpt height range.</summary>
        Solid = 1,
        /// <summary>Spawns, landmarks, objectives, map bounds. Never sculpted, never falls.</summary>
        Anchor = 2,
    }

    /// <summary>
    /// State of one grid cell. Plain value type so it moves with the block when the block moves.
    /// All timers are absolute tick numbers, so decay needs no per-tick work.
    /// </summary>
    public struct Block
    {
        public BlockKind Kind;

        /// <summary>True if the top of this block is a ramp (a wedge filling the cell).</summary>
        public bool IsRamp;

        /// <summary>The side of the cell where the ramp is low (height 0). It rises toward the opposite side.</summary>
        public Dir RampLow;

        /// <summary>Team that reinforced this block, 0 = none.</summary>
        public byte ReinforceTeam;

        /// <summary>Reinforcement is active while tick &lt; ReinforceUntilTick.</summary>
        public int ReinforceUntilTick;

        /// <summary>Team whose sculpt last touched this block (cosmetic tint), 0 = none.</summary>
        public byte TintTeam;

        /// <summary>Tick the tint was applied. Fades out over SculptConfig.TintFadeTicks.</summary>
        public int TintTick;

        public bool IsEmpty => Kind == BlockKind.Empty;

        public static Block Empty => default;
        public static Block Solid => new Block { Kind = BlockKind.Solid };
        public static Block Anchor => new Block { Kind = BlockKind.Anchor };
        public static Block Ramp(Dir low) => new Block { Kind = BlockKind.Solid, IsRamp = true, RampLow = low };

        public bool IsReinforced(int tick) => ReinforceTeam != 0 && tick < ReinforceUntilTick;

        /// <summary>True if this block is reinforced by a team other than <paramref name="team"/> at <paramref name="tick"/>.</summary>
        public bool IsEnemyReinforced(int team, int tick) => IsReinforced(tick) && ReinforceTeam != team;

        public void ClearReinforcement()
        {
            ReinforceTeam = 0;
            ReinforceUntilTick = 0;
        }

        public void MakeFlat()
        {
            IsRamp = false;
            RampLow = Dir.East;
        }

        public override string ToString() => IsEmpty ? "Empty" : (IsRamp ? $"{Kind}/Ramp(low {RampLow})" : Kind.ToString());
    }
}
