using System;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    // Whole-tile test maps that keep the M2-era test syntax (VoxelGrid / Block / VoxelGeometry), so the movement,
    // movement-fuzz and netcode tests port unchanged as regression checks. Everything is built on the terrain v2
    // sub-cell grid: a Block is a 1.2 m tile of 4x4x4 sub-cells and a ramp tile is a four-step staircase that the
    // surface kernel smooths into a 45 degree slope.

    public enum BlockKind : byte
    {
        Empty = 0,
        Solid = 1,
        Anchor = 2,
    }

    public struct Block
    {
        public BlockKind Kind;
        public bool IsRamp;
        public Dir RampLow;

        public bool IsEmpty => Kind == BlockKind.Empty;

        public static Block Empty => default;
        public static Block Solid => new Block { Kind = BlockKind.Solid };
        public static Block Anchor => new Block { Kind = BlockKind.Anchor };
        public static Block Ramp(Dir low) => new Block { Kind = BlockKind.Solid, IsRamp = true, RampLow = low };

        public Tile ToTile()
        {
            if (Kind == BlockKind.Empty) return Tile.Empty;
            if (IsRamp) return Kind == BlockKind.Anchor ? Tile.AnchorRamp(RampLow) : Tile.Ramp(RampLow);
            return Kind == BlockKind.Anchor ? Tile.Anchor : Tile.Solid;
        }
    }

    public sealed class VoxelGrid : ITerrain
    {
        public const double BlockSize = SubGrid.TileSize;

        public readonly SubGrid Sub;
        private readonly TileMap _tiles;

        /// <summary>Chunk counts are in old 16-tile chunks horizontally; the height is fixed at tiles -4..11.</summary>
        public VoxelGrid(int chunksX, int chunksY, int chunksZ, int minY = -16)
        {
            Sub = new SubGrid(chunksX * 4, 4, chunksZ * 4, -16);
            _tiles = new TileMap(Sub);
        }

        public VoxelGrid(SubGrid sub)
        {
            Sub = sub;
            _tiles = new TileMap(sub);
        }

        public int SizeX => Sub.SizeX / SubGrid.TileSubs;
        public int SizeZ => Sub.SizeZ / SubGrid.TileSubs;

        public void FillBaseGround() => Sub.FillBaseGround();
        public void ClearChanges() => Sub.ClearChanges();
        public void Set(int x, int y, int z, Block b) => _tiles.Set(x, y, z, b.ToTile());
        public void Set(Int3 c, Block b) => _tiles.Set(c, b.ToTile());
        public void Fill(Int3 min, Int3 max, Block b) => _tiles.Fill(min, max, b.ToTile());

        /// <summary>Rough read-back of a tile: empty, anchor if it holds anchor, otherwise solid.</summary>
        public Block Get(int x, int y, int z)
        {
            Int3 o = SubGrid.TileOrigin(new Int3(x, y, z));
            int solid = 0;
            bool anchor = false;
            for (int dy = 0; dy < 4; dy++)
                for (int dz = 0; dz < 4; dz++)
                    for (int dx = 0; dx < 4; dx++)
                    {
                        ushort id = Sub.Get(o.X + dx, o.Y + dy, o.Z + dz);
                        if (id == SubId.Empty) continue;
                        solid++;
                        if (id == SubId.Anchor || id == SubId.Bedrock) anchor = true;
                    }
            if (solid == 0) return Block.Empty;
            return anchor ? Block.Anchor : Block.Solid;
        }

        public Block Get(Int3 c) => Get(c.X, c.Y, c.Z);

        public static Aabb CellBounds(Int3 c)
        {
            var min = new Vec3d(c.X * BlockSize, c.Y * BlockSize, c.Z * BlockSize);
            return new Aabb(min, min + new Vec3d(BlockSize, BlockSize, BlockSize));
        }

        public static Int3 CellAt(Vec3d p) => new Int3(
            (int)Math.Floor(p.X / BlockSize), (int)Math.Floor(p.Y / BlockSize), (int)Math.Floor(p.Z / BlockSize));

        public bool BoxOverlapsSolid(Aabb box) => Sub.BoxOverlapsSolid(box);
        public bool GroundContact(Aabb box, double probe, out Vec3d normal) => Sub.GroundContact(box, probe, out normal);
        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance) => Sub.Raycast(origin, direction, maxDistance);
    }

    public static class VoxelGeometry
    {
        public static bool BoxOverlapsSolid(VoxelGrid g, Aabb box) => g.BoxOverlapsSolid(box);
        public static bool BoxOverlapsSolid(ITerrain g, Aabb box) => g.BoxOverlapsSolid(box);
    }
}
