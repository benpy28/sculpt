using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Builds one mesh per 16^3 chunk. Hidden faces are culled, ramps are real wedges.
    /// Faces are shaded per axis for readability: tops brightest, north/south darker, east/west darker again.
    /// Colours are baked into vertex colours; the shader adds thin block edges.
    /// </summary>
    public sealed class ChunkMesher
    {
        // Per-axis shade (fixed sun). Purple is reserved for "sculptable" and never appears here.
        public const float ShadeTop = 1.00f;
        public const float ShadeSlope = 0.92f;
        public const float ShadeNorthSouth = 0.80f;
        public const float ShadeEastWest = 0.66f;
        public const float ShadeBottom = 0.50f;

        public static readonly Color Ground = new Color(0.80f, 0.76f, 0.68f);
        public static readonly Color Raised = new Color(0.86f, 0.83f, 0.76f);
        public static readonly Color Bedrock = new Color(0.46f, 0.44f, 0.41f);
        public static readonly Color AnchorColor = new Color(0.47f, 0.52f, 0.56f);

        private readonly List<Vector3> _verts = new List<Vector3>(8192);
        private readonly List<Color> _colors = new List<Color>(8192);
        private readonly List<Vector2> _uvs = new List<Vector2>(8192);
        private readonly List<int> _tris = new List<int>(12288);
        private readonly bool _linear;
        private readonly SculptConfig _sculpt;

        public ChunkMesher(SculptConfig sculpt)
        {
            _sculpt = sculpt;
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        }

        public void Build(VoxelGrid g, Int3 chunkCoord, Mesh mesh)
        {
            _verts.Clear();
            _colors.Clear();
            _uvs.Clear();
            _tris.Clear();

            Int3 o = g.ChunkOrigin(chunkCoord);
            const int n = VoxelGrid.ChunkSize;
            for (int y = 0; y < n; y++)
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        var cell = new Int3(o.X + x, o.Y + y, o.Z + z);
                        Block b = g.Get(cell);
                        if (b.IsEmpty) continue;
                        Vector3 local = new Vector3(x, y, z) * (float)VoxelGrid.BlockSize;
                        Color albedo = AlbedoFor(b, cell);
                        if (b.IsRamp) AddRamp(g, cell, b, local, albedo);
                        else AddCube(g, cell, local, albedo);
                    }

            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_verts);
            mesh.SetColors(_colors);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0, false);
            mesh.RecalculateBounds();
        }

        private Color AlbedoFor(Block b, Int3 cell)
        {
            Color c;
            if (b.Kind == BlockKind.Anchor) c = AnchorColor;
            else if (cell.Y < _sculpt.MinCellY) c = Bedrock;
            else if (cell.Y >= 0) c = Raised;
            else c = Ground;
            // Subtle checker so distance and grid position read at a glance.
            if (((cell.X + cell.Y + cell.Z) & 1) == 0) c *= 0.965f;
            c.a = 1;
            return c;
        }

        // ---- Visibility ----

        /// <summary>Does the neighbour in direction d fully cover our face on that side?</summary>
        private static bool Covered(VoxelGrid g, Int3 cell, Dir d)
        {
            Block nb = g.Get(cell + d.ToInt3());
            if (nb.IsEmpty) return false;
            if (!nb.IsRamp) return true;
            Dir faceOfNeighbour = d.Opposite();
            return faceOfNeighbour == Dir.Down || faceOfNeighbour == nb.RampLow.Opposite();
        }

        private static float ShadeFor(Dir d)
        {
            switch (d)
            {
                case Dir.Up: return ShadeTop;
                case Dir.Down: return ShadeBottom;
                case Dir.North:
                case Dir.South: return ShadeNorthSouth;
                default: return ShadeEastWest;
            }
        }

        // ---- Cubes ----

        private void AddCube(VoxelGrid g, Int3 cell, Vector3 p, Color albedo)
        {
            float s = (float)VoxelGrid.BlockSize;
            if (!Covered(g, cell, Dir.Up)) Quad(p + new Vector3(0, s, 0), p + new Vector3(0, s, s), p + new Vector3(s, s, s), p + new Vector3(s, s, 0), Vector3.up, albedo * ShadeTop);
            if (!Covered(g, cell, Dir.Down)) Quad(p, p + new Vector3(s, 0, 0), p + new Vector3(s, 0, s), p + new Vector3(0, 0, s), Vector3.down, albedo * ShadeBottom);
            if (!Covered(g, cell, Dir.North)) Quad(p + new Vector3(0, 0, s), p + new Vector3(s, 0, s), p + new Vector3(s, s, s), p + new Vector3(0, s, s), Vector3.forward, albedo * ShadeNorthSouth);
            if (!Covered(g, cell, Dir.South)) Quad(p, p + new Vector3(0, s, 0), p + new Vector3(s, s, 0), p + new Vector3(s, 0, 0), Vector3.back, albedo * ShadeNorthSouth);
            if (!Covered(g, cell, Dir.East)) Quad(p + new Vector3(s, 0, 0), p + new Vector3(s, s, 0), p + new Vector3(s, s, s), p + new Vector3(s, 0, s), Vector3.right, albedo * ShadeEastWest);
            if (!Covered(g, cell, Dir.West)) Quad(p, p + new Vector3(0, 0, s), p + new Vector3(0, s, s), p + new Vector3(0, s, 0), Vector3.left, albedo * ShadeEastWest);
        }

        // ---- Ramps ----
        // Built in an "east frame" (low edge at +X), then rotated about the cell's vertical axis.

        private static Vector3 Rot(Dir low, Vector3 v, float s, bool isDirection)
        {
            float off = isDirection ? 0 : s;
            switch (low)
            {
                case Dir.East: return v;
                case Dir.West: return new Vector3(off - v.x, v.y, off - v.z);
                case Dir.North: return new Vector3(off - v.z, v.y, v.x);
                default: return new Vector3(v.z, v.y, off - v.x); // South
            }
        }

        private static Dir RotDir(Dir low, Dir d)
        {
            Vector3 v = Rot(low, d.ToInt3().ToUnity(), 0, true);
            return DirUtil.FromInt3(new Int3(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z)));
        }

        private void AddRamp(VoxelGrid g, Int3 cell, Block b, Vector3 p, Color albedo)
        {
            float s = (float)VoxelGrid.BlockSize;
            Dir low = b.RampLow;
            Vector3 R(float x, float y, float z) => p + Rot(low, new Vector3(x, y, z), s, false);
            Vector3 N(Vector3 dir) => Rot(low, dir, s, true);

            // Slope: high edge at x = 0 (top), low edge at x = s (bottom).
            Quad(R(0, s, 0), R(0, s, s), R(s, 0, s), R(s, 0, 0), N(new Vector3(1, 1, 0).normalized), albedo * ShadeSlope);

            // Bottom.
            if (!Covered(g, cell, Dir.Down)) Quad(R(0, 0, 0), R(s, 0, 0), R(s, 0, s), R(0, 0, s), Vector3.down, albedo * ShadeBottom);

            // High side (full face, toward -X in the east frame).
            Dir high = RotDir(low, Dir.West);
            if (!Covered(g, cell, high)) Quad(R(0, 0, 0), R(0, 0, s), R(0, s, s), R(0, s, 0), N(Vector3.left), albedo * ShadeFor(high));

            // Two triangular sides.
            Dir sideA = RotDir(low, Dir.South);
            Dir sideB = RotDir(low, Dir.North);
            if (!CoveredFull(g, cell, sideA)) Tri(R(0, 0, 0), R(0, s, 0), R(s, 0, 0), N(Vector3.back), albedo * ShadeFor(sideA));
            if (!CoveredFull(g, cell, sideB)) Tri(R(0, 0, s), R(s, 0, s), R(0, s, s), N(Vector3.forward), albedo * ShadeFor(sideB));
        }

        private static bool CoveredFull(VoxelGrid g, Int3 cell, Dir d)
        {
            Block nb = g.Get(cell + d.ToInt3());
            return !nb.IsEmpty && !nb.IsRamp;
        }

        // ---- Emit ----

        private Color Lin(Color c) => _linear ? c.linear : c;

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color col)
        {
            int i = _verts.Count;
            // Ensure Unity's clockwise front-face winding for the requested normal.
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
            {
                Vector3 t = b; b = d; d = t;
                _verts.Add(a); _verts.Add(b); _verts.Add(c); _verts.Add(d);
                _uvs.Add(new Vector2(0, 0)); _uvs.Add(new Vector2(1, 0)); _uvs.Add(new Vector2(1, 1)); _uvs.Add(new Vector2(0, 1));
            }
            else
            {
                _verts.Add(a); _verts.Add(b); _verts.Add(c); _verts.Add(d);
                _uvs.Add(new Vector2(0, 0)); _uvs.Add(new Vector2(0, 1)); _uvs.Add(new Vector2(1, 1)); _uvs.Add(new Vector2(1, 0));
            }
            Color lc = Lin(col);
            lc.a = 1;
            for (int k = 0; k < 4; k++) _colors.Add(lc);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 3);
        }

        private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color col)
        {
            int i = _verts.Count;
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
            {
                Vector3 t = b; b = c; c = t;
            }
            _verts.Add(a); _verts.Add(b); _verts.Add(c);
            // Map the triangle into the unit square so edge lines land on its two straight sides.
            _uvs.Add(new Vector2(0, 0)); _uvs.Add(new Vector2(0, 1)); _uvs.Add(new Vector2(1, 0));
            Color lc = Lin(col);
            lc.a = 1;
            for (int k = 0; k < 3; k++) _colors.Add(lc);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
        }
    }
}
