using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Read access to sub-cell ids. The real grid and "what if" overlays both implement it.</summary>
    public interface ISubCells
    {
        /// <summary>Id of a sub-cell: 0 empty, 1 bedrock, 2 anchor, 3 natural rock, 4+ a carved piece.</summary>
        ushort Get(int x, int y, int z);
    }

    /// <summary>Well-known sub-cell ids.</summary>
    public static class SubId
    {
        public const ushort Empty = 0;
        /// <summary>Unsculptable floor of the world. Never moves.</summary>
        public const ushort Bedrock = 1;
        /// <summary>Spawns, landmarks, objectives, map bounds. Never sculpted, never falls.</summary>
        public const ushort Anchor = 2;
        /// <summary>Uncarved, sculptable rock. A sculpt carves a piece out of it.</summary>
        public const ushort Natural = 3;
        /// <summary>First id used for carved pieces.</summary>
        public const ushort FirstPiece = 4;

        public static bool IsPiece(ushort id) => id >= FirstPiece;
        /// <summary>Rock a sculpt can grab: natural rock or a carved piece.</summary>
        public static bool IsSculptable(ushort id) => id >= Natural;
    }

    /// <summary>
    /// Terrain v2 storage: 0.3 m sub-cells (4x4x4 per 1.2 m tile), each holding an id, in flat 16^3 chunks.
    /// Sub-cell (x, y, z) spans [x, x+1) * SubSize metres. Base ground top is world height 0 (sub-cell y = -1 is the
    /// top ground layer). Out of bounds reads as bedrock below and at the sides, empty above.
    /// The shape everyone sees, walks on and shoots is derived from occupancy by <see cref="TerrainShape"/>.
    /// </summary>
    public sealed class SubGrid : ISubCells, ITerrain
    {
        public const int ChunkSize = 16;
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        /// <summary>Sub-cell edge in metres.</summary>
        public const double SubSize = 0.3;
        /// <summary>Sub-cells per tile edge.</summary>
        public const int TileSubs = 4;
        /// <summary>Tile (and level) edge in metres.</summary>
        public const double TileSize = SubSize * TileSubs;

        public readonly int ChunksX, ChunksY, ChunksZ;
        /// <summary>Lowest sub-cell Y stored. Multiple of ChunkSize.</summary>
        public readonly int MinY;

        public int SizeX => ChunksX * ChunkSize;
        public int SizeY => ChunksY * ChunkSize;
        public int SizeZ => ChunksZ * ChunkSize;
        public int MaxYExclusive => MinY + SizeY;

        private readonly ushort[][] _chunks;
        private readonly HashSet<int> _dirtyChunks = new HashSet<int>();
        private readonly List<Int3> _changes = new List<Int3>();

        public SubGrid(int chunksX, int chunksY, int chunksZ, int minY = -16)
        {
            if (chunksX <= 0 || chunksY <= 0 || chunksZ <= 0) throw new ArgumentOutOfRangeException(nameof(chunksX), "Chunk counts must be positive.");
            if (minY % ChunkSize != 0) throw new ArgumentException("MinY must be a multiple of ChunkSize.", nameof(minY));
            ChunksX = chunksX;
            ChunksY = chunksY;
            ChunksZ = chunksZ;
            MinY = minY;
            _chunks = new ushort[chunksX * chunksY * chunksZ][];
            for (int i = 0; i < _chunks.Length; i++) _chunks[i] = new ushort[ChunkVolume];
        }

        public int ChunkCount => _chunks.Length;

        public bool InBounds(int x, int y, int z) =>
            x >= 0 && x < SizeX && z >= 0 && z < SizeZ && y >= MinY && y < MaxYExclusive;

        public bool InBounds(Int3 c) => InBounds(c.X, c.Y, c.Z);

        public ushort Get(int x, int y, int z)
        {
            if (!InBounds(x, y, z))
                return y >= MaxYExclusive && x >= 0 && x < SizeX && z >= 0 && z < SizeZ ? SubId.Empty : SubId.Bedrock;
            int ly = y - MinY;
            int cx = x >> 4, cy = ly >> 4, cz = z >> 4;
            return _chunks[(cy * ChunksZ + cz) * ChunksX + cx][(((ly & 15) << 4) + (z & 15) << 4) + (x & 15)];
        }

        public ushort Get(Int3 c) => Get(c.X, c.Y, c.Z);

        public bool IsSolid(int x, int y, int z) => Get(x, y, z) != SubId.Empty;

        /// <summary>Writes a sub-cell, logs the change and marks every chunk whose surface it touches dirty.</summary>
        public void Set(int x, int y, int z, ushort id)
        {
            if (!InBounds(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x), $"Sub-cell out of bounds: ({x}, {y}, {z})");
            int ly = y - MinY;
            int cx = x >> 4, cy = ly >> 4, cz = z >> 4;
            ref ushort slot = ref _chunks[(cy * ChunksZ + cz) * ChunksX + cx][(((ly & 15) << 4) + (z & 15) << 4) + (x & 15)];
            if (slot == id) return;
            slot = id;
            _changes.Add(new Int3(x, y, z));
            // The surface around a sub-cell lives in the 8 dual cells at its corners: vertices (x..x+1, y..y+1, z..z+1).
            for (int dy = 0; dy <= 1; dy++)
                for (int dz = 0; dz <= 1; dz++)
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int vx = x + dx, vy = y + dy, vz = z + dz;
                        if (vx >= SizeX || vz >= SizeZ || vy >= MaxYExclusive) continue;
                        _dirtyChunks.Add(ChunkIndex(new Int3(vx >> 4, (vy - MinY) >> 4, vz >> 4)));
                    }
        }

        public void Set(Int3 c, ushort id) => Set(c.X, c.Y, c.Z, id);

        /// <summary>Fills a box of sub-cells (inclusive bounds).</summary>
        public void Fill(Int3 min, Int3 max, ushort id)
        {
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    for (int x = min.X; x <= max.X; x++)
                        Set(x, y, z, id);
        }

        /// <summary>Lowest sculptable sub-cell layer: a one-level slab here has its top at level -2 (-2.4 m), the range floor.</summary>
        public const int SculptMinY = -3 * TileSubs;

        /// <summary>Bedrock below the sculpt range, natural rock from there up to the base ground top.</summary>
        public void FillBaseGround()
        {
            Fill(new Int3(0, MinY, 0), new Int3(SizeX - 1, SculptMinY - 1, SizeZ - 1), SubId.Bedrock);
            Fill(new Int3(0, SculptMinY, 0), new Int3(SizeX - 1, -1, SizeZ - 1), SubId.Natural);
        }

        // ---- Coordinates ----

        public static Aabb SubBounds(Int3 c)
        {
            var min = new Vec3d(c.X * SubSize, c.Y * SubSize, c.Z * SubSize);
            return new Aabb(min, min + new Vec3d(SubSize, SubSize, SubSize));
        }

        public static Int3 SubAt(Vec3d p) => new Int3(
            (int)Math.Floor(p.X / SubSize), (int)Math.Floor(p.Y / SubSize), (int)Math.Floor(p.Z / SubSize));

        /// <summary>Tile containing a sub-cell.</summary>
        public static Int3 TileOf(Int3 sub) => new Int3(sub.X >> 2, sub.Y >> 2, sub.Z >> 2);

        public static Int3 TileOrigin(Int3 tile) => new Int3(tile.X * TileSubs, tile.Y * TileSubs, tile.Z * TileSubs);

        // ---- Chunks, dirty tracking, change log ----

        public int ChunkIndex(Int3 chunkCoord) => (chunkCoord.Y * ChunksZ + chunkCoord.Z) * ChunksX + chunkCoord.X;

        public Int3 ChunkCoordFromIndex(int i) => new Int3(i % ChunksX, i / (ChunksX * ChunksZ), (i / ChunksX) % ChunksZ);

        /// <summary>Sub-cell coordinate of a chunk's minimum corner. Its mesh covers dual cells (lattice vertices) from here.</summary>
        public Int3 ChunkOrigin(Int3 chunkCoord) => new Int3(chunkCoord.X * ChunkSize, chunkCoord.Y * ChunkSize + MinY, chunkCoord.Z * ChunkSize);

        public void MarkAllDirty()
        {
            for (int i = 0; i < _chunks.Length; i++) _dirtyChunks.Add(i);
        }

        public bool HasDirtyChunks => _dirtyChunks.Count > 0;

        /// <summary>Moves the dirty chunk indices into <paramref name="into"/> in ascending order and clears them.</summary>
        public void DrainDirtyChunks(List<int> into)
        {
            int start = into.Count;
            into.AddRange(_dirtyChunks);
            into.Sort(start, into.Count - start, Comparer<int>.Default);
            _dirtyChunks.Clear();
        }

        /// <summary>Moves the changed sub-cells (write order) into <paramref name="into"/> and clears the log.</summary>
        public void DrainChanges(List<Int3> into)
        {
            into.AddRange(_changes);
            _changes.Clear();
        }

        public void ClearChanges() => _changes.Clear();

        /// <summary>Copies every sub-cell (used by tests and for late-join snapshots).</summary>
        public SubGrid Clone()
        {
            var g = new SubGrid(ChunksX, ChunksY, ChunksZ, MinY);
            for (int i = 0; i < _chunks.Length; i++) Array.Copy(_chunks[i], g._chunks[i], ChunkVolume);
            return g;
        }

        /// <summary>True if both grids hold exactly the same ids.</summary>
        public bool SameCells(SubGrid o)
        {
            if (o.ChunksX != ChunksX || o.ChunksY != ChunksY || o.ChunksZ != ChunksZ || o.MinY != MinY) return false;
            for (int i = 0; i < _chunks.Length; i++)
            {
                ushort[] a = _chunks[i], b = o._chunks[i];
                for (int k = 0; k < ChunkVolume; k++) if (a[k] != b[k]) return false;
            }
            return true;
        }

        /// <summary>Order-dependent hash of every sub-cell, for determinism checks.</summary>
        public ulong Hash()
        {
            ulong h = 1469598103934665603UL;
            foreach (var chunk in _chunks)
                for (int k = 0; k < ChunkVolume; k++)
                {
                    h ^= chunk[k];
                    h *= 1099511628211UL;
                }
            return h;
        }

        // ---- ITerrain ----

        public bool BoxOverlapsSolid(Aabb box) => TerrainGeometry.BoxOverlapsSolid(this, box);

        public bool GroundContact(Aabb box, double probe, out Vec3d normal) => TerrainGeometry.GroundContact(this, box, probe, out normal);

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance) => TerrainGeometry.Raycast(this, origin, direction, maxDistance);
    }
}
