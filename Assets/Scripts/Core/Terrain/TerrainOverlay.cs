using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>"What if" view of a grid: a few sub-cells replaced, everything else read through. Same shape rules.</summary>
    public sealed class TerrainOverlay : ISubCells, ITerrain
    {
        private readonly ISubCells _base;
        private readonly Dictionary<Int3, ushort> _over = new Dictionary<Int3, ushort>();

        public TerrainOverlay(ISubCells baseCells) => _base = baseCells;

        public void Clear() => _over.Clear();

        public void Set(Int3 c, ushort id) => _over[c] = id;

        public ushort Get(int x, int y, int z) => _over.Count != 0 && _over.TryGetValue(new Int3(x, y, z), out ushort id) ? id : _base.Get(x, y, z);

        public bool BoxOverlapsSolid(Aabb box) => TerrainGeometry.BoxOverlapsSolid(this, box);

        public bool GroundContact(Aabb box, double probe, out Vec3d normal) => TerrainGeometry.GroundContact(this, box, probe, out normal);

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance) => TerrainGeometry.Raycast(this, origin, direction, maxDistance);
    }
}
