using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Block storage: flat arrays split into 16x16x16 chunks. Cell (x, y, z) spans
    /// [x, x+1) * BlockSize metres on each axis. Base ground top is world height 0, so the
    /// top base-ground block is cell y = -1.
    /// Out of bounds: below the grid and outside the sides reads as Anchor (an unbreakable wall);
    /// above the grid reads as Empty.
    /// </summary>
    public sealed class VoxelGrid
    {
        public const int ChunkSize = 16;
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        /// <summary>Block edge length in metres.</summary>
        public const double BlockSize = 1.2;

        public readonly int ChunksX;
        public readonly int ChunksY;
        public readonly int ChunksZ;

        /// <summary>Lowest cell Y stored. Must be a multiple of ChunkSize.</summary>
        public readonly int MinY;

        public int SizeX => ChunksX * ChunkSize;
        public int SizeY => ChunksY * ChunkSize;
        public int SizeZ => ChunksZ * ChunkSize;
        public int MaxYExclusive => MinY + SizeY;

        private readonly Block[][] _chunks;
        private readonly HashSet<int> _dirtyChunks = new HashSet<int>();
        private readonly List<Int3> _changes = new List<Int3>();

        public VoxelGrid(int chunksX, int chunksY, int chunksZ, int minY = -16)
        {
            if (chunksX <= 0 || chunksY <= 0 || chunksZ <= 0) throw new ArgumentOutOfRangeException(nameof(chunksX), "Chunk counts must be positive.");
            if (minY % ChunkSize != 0) throw new ArgumentException("MinY must be a multiple of ChunkSize.", nameof(minY));
            ChunksX = chunksX;
            ChunksY = chunksY;
            ChunksZ = chunksZ;
            MinY = minY;
            _chunks = new Block[chunksX * chunksY * chunksZ][];
            for (int i = 0; i < _chunks.Length; i++) _chunks[i] = new Block[ChunkVolume];
        }

        public int ChunkCount => _chunks.Length;

        public bool InBounds(Int3 c) =>
            c.X >= 0 && c.X < SizeX && c.Z >= 0 && c.Z < SizeZ && c.Y >= MinY && c.Y < MaxYExclusive;

        public Block Get(Int3 c)
        {
            if (!InBounds(c))
                return c.Y >= MaxYExclusive && c.X >= 0 && c.X < SizeX && c.Z >= 0 && c.Z < SizeZ ? Block.Empty : Block.Anchor;
            Locate(c, out int chunk, out int index);
            return _chunks[chunk][index];
        }

        public Block Get(int x, int y, int z) => Get(new Int3(x, y, z));

        public bool IsEmpty(Int3 c) => Get(c).IsEmpty;

        /// <summary>Writes a cell, marks its chunk (and touching neighbour chunks) dirty and logs the change.</summary>
        public void Set(Int3 c, Block b)
        {
            if (!InBounds(c)) throw new ArgumentOutOfRangeException(nameof(c), "Cell out of bounds: " + c);
            Locate(c, out int chunk, out int index);
            _chunks[chunk][index] = b;
            _changes.Add(c);
            MarkDirtyAround(c);
        }

        public void Set(int x, int y, int z, Block b) => Set(new Int3(x, y, z), b);

        /// <summary>Fill a box of cells (inclusive bounds), without needing per-call bounds juggling in map code.</summary>
        public void Fill(Int3 min, Int3 max, Block b)
        {
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    for (int x = min.X; x <= max.X; x++)
                        Set(new Int3(x, y, z), b);
        }

        /// <summary>Solid ground everywhere from the bottom of the grid up to the base ground top (cell y = -1).</summary>
        public void FillBaseGround() => Fill(new Int3(0, MinY, 0), new Int3(SizeX - 1, -1, SizeZ - 1), Block.Solid);

        /// <summary>Highest cell of the contiguous non-empty stack that contains (or starts at) <paramref name="c"/>.</summary>
        public Int3 TopOfStack(Int3 c)
        {
            Int3 t = c;
            while (t.Y + 1 < MaxYExclusive && !Get(t + Int3.Up).IsEmpty) t += Int3.Up;
            return t;
        }

        /// <summary>World-space box of a cell.</summary>
        public static Aabb CellBounds(Int3 c)
        {
            var min = new Vec3d(c.X * BlockSize, c.Y * BlockSize, c.Z * BlockSize);
            return new Aabb(min, min + new Vec3d(BlockSize, BlockSize, BlockSize));
        }

        /// <summary>Cell containing a world point.</summary>
        public static Int3 CellAt(Vec3d p) => new Int3(
            (int)Math.Floor(p.X / BlockSize),
            (int)Math.Floor(p.Y / BlockSize),
            (int)Math.Floor(p.Z / BlockSize));

        // ---- Chunk bookkeeping for meshing and net deltas ----

        public Int3 ChunkCoordOf(Int3 c) => new Int3(
            FloorDiv(c.X, ChunkSize), FloorDiv(c.Y - MinY, ChunkSize), FloorDiv(c.Z, ChunkSize));

        public int ChunkIndex(Int3 chunkCoord) => (chunkCoord.Y * ChunksZ + chunkCoord.Z) * ChunksX + chunkCoord.X;

        public Int3 ChunkCoordFromIndex(int i) => new Int3(i % ChunksX, i / (ChunksX * ChunksZ), (i / ChunksX) % ChunksZ);

        /// <summary>Cell coordinate of a chunk's minimum corner.</summary>
        public Int3 ChunkOrigin(Int3 chunkCoord) => new Int3(chunkCoord.X * ChunkSize, chunkCoord.Y * ChunkSize + MinY, chunkCoord.Z * ChunkSize);

        public void MarkAllDirty()
        {
            for (int i = 0; i < _chunks.Length; i++) _dirtyChunks.Add(i);
        }

        /// <summary>Moves the dirty chunk indices into <paramref name="into"/> in ascending order and clears them.</summary>
        public void DrainDirtyChunks(List<int> into)
        {
            int start = into.Count;
            into.AddRange(_dirtyChunks);
            into.Sort(start, into.Count - start, Comparer<int>.Default);
            _dirtyChunks.Clear();
        }

        public bool HasDirtyChunks => _dirtyChunks.Count > 0;

        /// <summary>Moves the changed cells (in write order, may repeat) into <paramref name="into"/> and clears the log.</summary>
        public void DrainChanges(List<Int3> into)
        {
            into.AddRange(_changes);
            _changes.Clear();
        }

        public void ClearChanges() => _changes.Clear();

        private void MarkDirtyAround(Int3 c)
        {
            // A cell on a chunk border changes the neighbour chunk's faces too.
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > 1) continue;
                        Int3 n = new Int3(c.X + dx, c.Y + dy, c.Z + dz);
                        if (!InBounds(n)) continue;
                        _dirtyChunks.Add(ChunkIndex(ChunkCoordOf(n)));
                    }
        }

        private void Locate(Int3 c, out int chunk, out int index)
        {
            int ly = c.Y - MinY;
            int cx = c.X / ChunkSize, cy = ly / ChunkSize, cz = c.Z / ChunkSize;
            chunk = (cy * ChunksZ + cz) * ChunksX + cx;
            int ix = c.X - cx * ChunkSize, iy = ly - cy * ChunkSize, iz = c.Z - cz * ChunkSize;
            index = (iy * ChunkSize + iz) * ChunkSize + ix;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
