using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Meshes a free-standing set of sub-cells (a ghost, a flying piece) with the same chamfered surface kernel as the
    /// terrain, by writing them into a small scratch grid. The mesh is in world units relative to <see cref="Origin"/>.
    /// </summary>
    public sealed class PieceMesher
    {
        private const int Chunks = 2;                // 32^3 sub-cells: the biggest piece plus a move
        private const int Span = Chunks * SubGrid.ChunkSize;
        private readonly ChunkMesher _mesher = new ChunkMesher { MarkSculptable = false, OnlyId = SubId.Natural };
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector3> _n = new List<Vector3>();
        private readonly List<Vector3> _uv = new List<Vector3>();
        private readonly List<int> _t = new List<int>();
        private readonly List<Vector3> _uvScratch = new List<Vector3>();
        private Mesh _scratch; // made on first use: Unity objects can't be created from constructors or field initializers
        private SubGrid _grid;

        /// <summary>World position of the scratch grid's sub-cell (0, MinY, 0) for the last built mesh.</summary>
        public Vector3 Origin { get; private set; }

        /// <summary>Builds the surface of cells + offset into <paramref name="mesh"/>. False if the shape does not fit.</summary>
        public bool Build(IList<Int3> cells, Int3 offset, Mesh mesh)
        {
            mesh.Clear();
            if (cells == null || cells.Count == 0) return false;
            if (_scratch == null) _scratch = new Mesh { name = "Piece scratch" };
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c0 in cells)
            {
                Int3 c = c0 + offset;
                x0 = Mathf.Min(x0, c.X); y0 = Mathf.Min(y0, c.Y); z0 = Mathf.Min(z0, c.Z);
                x1 = Mathf.Max(x1, c.X); y1 = Mathf.Max(y1, c.Y); z1 = Mathf.Max(z1, c.Z);
            }
            if (x1 - x0 + 3 > Span || y1 - y0 + 3 > Span || z1 - z0 + 3 > Span) return false;
            // One empty sub-cell of margin all round so the surface closes.
            int ox = x0 - 1, oy = y0 - 1, oz = z0 - 1;
            if (_grid == null) _grid = new SubGrid(Chunks, Chunks, Chunks, 0);
            else _grid.Fill(new Int3(0, 0, 0), new Int3(Span - 1, Span - 1, Span - 1), SubId.Empty);
            foreach (var c0 in cells)
            {
                Int3 c = c0 + offset;
                _grid.Set(c.X - ox, c.Y - oy, c.Z - oz, SubId.Natural);
            }
            _grid.ClearChanges();

            _v.Clear(); _c.Clear(); _n.Clear(); _uv.Clear(); _t.Clear();
            for (int i = 0; i < _grid.ChunkCount; i++)
            {
                Int3 cc = _grid.ChunkCoordFromIndex(i);
                _mesher.Build(_grid, cc, _scratch);
                if (_scratch.vertexCount == 0) continue;
                Vector3 shift = _grid.ChunkOrigin(cc).ToUnity() * (float)SubGrid.SubSize;
                int start = _v.Count;
                var verts = _scratch.vertices;
                foreach (var v in verts) _v.Add(v + shift);
                _c.AddRange(_scratch.colors);
                _n.AddRange(_scratch.normals);
                _scratch.GetUVs(0, _uvScratch); _uv.AddRange(_uvScratch);
                foreach (int tri in _scratch.triangles) _t.Add(tri + start);
            }
            _grid.DrainDirtyChunks(new List<int>());
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(_v);
            mesh.SetColors(_c);
            mesh.SetNormals(_n);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            Origin = new Vector3(ox, oy, oz) * (float)SubGrid.SubSize;
            return true;
        }
    }
}
