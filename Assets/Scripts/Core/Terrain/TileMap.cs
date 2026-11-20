namespace Sculpt.Core
{
    /// <summary>A whole 1.2 m tile for hand-built maps: empty, rock, anchor, or a rock ramp.</summary>
    public readonly struct Tile
    {
        public readonly ushort Id;
        public readonly bool IsRamp;
        /// <summary>Ramp: the side where it is low. It rises toward the opposite side in four 0.3 m steps.</summary>
        public readonly Dir RampLow;

        private Tile(ushort id, bool ramp, Dir low)
        {
            Id = id;
            IsRamp = ramp;
            RampLow = low;
        }

        public static Tile Empty => new Tile(SubId.Empty, false, Dir.East);
        public static Tile Solid => new Tile(SubId.Natural, false, Dir.East);
        public static Tile Anchor => new Tile(SubId.Anchor, false, Dir.East);
        public static Tile Ramp(Dir low) => new Tile(SubId.Natural, true, low);
        public static Tile AnchorRamp(Dir low) => new Tile(SubId.Anchor, true, low);
    }

    /// <summary>
    /// Builds sub-cell terrain from whole tiles, so tile-designed maps (and the M2 movement tests) carry over.
    /// A ramp tile is a staircase of four sub-cell steps, which the surface kernel turns into a 45 degree slope
    /// running from 0.15 m outside the low edge up to 0.15 m short of the high edge.
    /// Tile y = 0 is the first level above base ground (sub-cells 0..3).
    /// </summary>
    public sealed class TileMap
    {
        public const double B = SubGrid.TileSize;
        public readonly SubGrid Grid;

        public TileMap(int chunksX, int chunksY, int chunksZ, int minY = -16) => Grid = new SubGrid(chunksX, chunksY, chunksZ, minY);

        public TileMap(SubGrid grid) => Grid = grid;

        /// <summary>Size in tiles.</summary>
        public int SizeX => Grid.SizeX / SubGrid.TileSubs;
        public int SizeZ => Grid.SizeZ / SubGrid.TileSubs;

        public void FillBaseGround() => Grid.FillBaseGround();

        public void Set(int x, int y, int z, Tile t) => Set(new Int3(x, y, z), t);

        public void Set(Int3 tile, Tile t)
        {
            Int3 o = SubGrid.TileOrigin(tile);
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    int height = 4;
                    if (t.IsRamp)
                    {
                        // k = steps from the low side (0..3); the column is k + 1 sub-cells tall.
                        int k;
                        switch (t.RampLow)
                        {
                            case Dir.East: k = 3 - i; break;
                            case Dir.West: k = i; break;
                            case Dir.North: k = 3 - j; break;
                            default: k = j; break;
                        }
                        height = k + 1;
                    }
                    for (int dy = 0; dy < 4; dy++)
                        Grid.Set(o.X + i, o.Y + dy, o.Z + j, dy < height ? t.Id : SubId.Empty);
                }
        }

        /// <summary>Fills a box of tiles (inclusive bounds).</summary>
        public void Fill(Int3 min, Int3 max, Tile t)
        {
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    for (int x = min.X; x <= max.X; x++)
                        Set(new Int3(x, y, z), t);
        }

        /// <summary>Feet position at the centre of a tile's floor.</summary>
        public static Vec3d FeetIn(int x, int y, int z) => new Vec3d((x + 0.5) * B, y * B, (z + 0.5) * B);
    }
}
