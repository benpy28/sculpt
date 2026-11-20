using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Builds one mesh per 16^3 sub-cell chunk straight from the Core surface kernel (<see cref="TerrainShape"/>), so
    /// what is drawn is exactly what players collide with and bullets hit. Each dual cell contributes the faces of its
    /// convex shape; faces on a dual cell's boundary are skipped when the neighbour's cross-section matches.
    /// Shading is per normal with a fixed sun (tops brightest, north/south darker, east/west darker again, chamfers
    /// in between). Colour per id: natural rock, anchors, bedrock, and pieces (a slight per-piece variation, plus a
    /// team tint that fades). Seams between different pieces are darkened through uv.x; the shader adds rock detail.
    /// No grid lines: it must never read as a cube game.
    /// </summary>
    public sealed class ChunkMesher
    {
        // Per-axis shade (fixed sun). Purple is reserved for "sculptable" and never appears here.
        public const float ShadeTop = 1.00f;
        public const float ShadeNorthSouth = 0.80f;
        public const float ShadeEastWest = 0.68f;
        public const float ShadeBottom = 0.52f;

        public static readonly Color Rock = new Color(0.80f, 0.75f, 0.66f);
        public static readonly Color RockHigh = new Color(0.86f, 0.82f, 0.74f);
        public static readonly Color Bedrock = new Color(0.46f, 0.44f, 0.41f);
        public static readonly Color AnchorColor = new Color(0.47f, 0.52f, 0.56f);
        public static readonly Color[] TeamTint = { new Color(0.30f, 0.55f, 1.00f), new Color(1.00f, 0.55f, 0.20f) }; // blue, orange: never purple

        /// <summary>Optional: returns (team, 0..1 strength) for a piece's fading tint.</summary>
        public System.Func<ushort, (int team, float strength)> PieceTint;

        private readonly List<Vector3> _verts = new List<Vector3>(16384);
        private readonly List<Color> _colors = new List<Color>(16384);
        private readonly List<Vector2> _uvs = new List<Vector2>(16384);
        private readonly List<Vector3> _normals = new List<Vector3>(16384);
        private readonly List<int> _tris = new List<int>(24576);
        private readonly bool _linear;

        public ChunkMesher()
        {
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        }

        public void Build(SubGrid g, Int3 chunkCoord, Mesh mesh)
        {
            _verts.Clear();
            _colors.Clear();
            _uvs.Clear();
            _normals.Clear();
            _tris.Clear();

            Int3 o = g.ChunkOrigin(chunkCoord);
            const int n = SubGrid.ChunkSize;
            const float h = (float)TerrainShape.H;
            const float s = (float)SubGrid.SubSize;
            for (int y = 0; y < n; y++)
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        int vx = o.X + x, vy = o.Y + y, vz = o.Z + z;
                        int cfg = TerrainShape.ConfigAt(g, vx, vy, vz);
                        if (cfg == 0) continue;
                        TerrainShape.Poly poly = TerrainShape.Get(cfg);
                        // Neighbour signatures only needed for boundary faces.
                        Vector3 cellMin = new Vector3(x * s - h, y * s - h, z * s - h);
                        foreach (var f in poly.Faces)
                        {
                            if (!TerrainShape.IsFaceVisible(g, vx, vy, vz, poly, f)) continue;
                            EmitFace(g, f, cellMin, vx, vy, vz, cfg);
                        }
                    }

            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0, false);
            mesh.RecalculateBounds();
        }

        private void EmitFace(SubGrid g, TerrainShape.Face f, Vector3 cellMin, int vx, int vy, int vz, int cfg)
        {
            const float h = (float)TerrainShape.H;
            Vector3 normal = f.Normal.ToUnity();

            // Face centroid (world) picks the colour owner: the nearest occupied corner sub-cell.
            Vec3d cw = Vec3d.Zero;
            foreach (var v in f.Verts) cw += v;
            cw = cw / f.Verts.Length;
            Vec3d worldMin = TerrainShape.CellMin(vx, vy, vz);
            Vec3d centroid = worldMin + cw * TerrainShape.H;
            Int3 owner = TerrainGeometry.NearestOccupied(cfg, vx, vy, vz, centroid);
            ushort ownerId = g.Get(owner);
            Color albedo = Lin(Albedo(ownerId, owner)) * Shade(f.Normal);
            albedo.a = 1;

            int start = _verts.Count;
            for (int i = 0; i < f.Verts.Length; i++)
            {
                Vec3d lv = f.Verts[i];
                _verts.Add(cellMin + new Vector3((float)lv.X, (float)lv.Y, (float)lv.Z) * h);
                _normals.Add(normal);
                _colors.Add(albedo);
                // Seam weight: the vertex sits next to a different solid id than the face's owner.
                Vec3d wv = worldMin + lv * TerrainShape.H;
                Int3 near = TerrainGeometry.NearestOccupied(cfg, vx, vy, vz, wv);
                ushort nid = g.Get(near);
                float seam = nid != ownerId && (SubId.IsPiece(nid) || SubId.IsPiece(ownerId)) ? 1f : 0f;
                _uvs.Add(new Vector2(seam, 0));
            }
            // Core orders vertices so Cross(v1 - v0, v2 - v0) points outward, which is what Unity treats as front facing
            // (same axes, same cross product; the old mesher enforced the same rule).
            for (int i = 1; i + 1 < f.Verts.Length; i++)
            {
                _tris.Add(start);
                _tris.Add(start + i);
                _tris.Add(start + i + 1);
            }
        }

        private Color Albedo(ushort id, Int3 sub)
        {
            Color c;
            switch (id)
            {
                case SubId.Anchor: c = AnchorColor; break;
                case SubId.Bedrock: c = Bedrock; break;
                case SubId.Natural: c = sub.Y >= 0 ? RockHigh : Rock; break;
                default:
                {
                    // Pieces: slight per-piece variation so pieces read as separate stones.
                    uint hsh = (uint)id * 2654435761u;
                    float v = 0.93f + ((hsh >> 24) & 0xFF) / 255f * 0.10f;
                    c = RockHigh * v;
                    if (PieceTint != null)
                    {
                        var (team, strength) = PieceTint(id);
                        if (team >= 1 && team <= 2 && strength > 0) c = Color.Lerp(c, TeamTint[team - 1], 0.18f * strength);
                    }
                    break;
                }
            }
            c.a = 1;
            return c;
        }

        /// <summary>Fixed-sun shade from the face normal: blends top, sides and bottom so chamfers sit in between.</summary>
        private static float Shade(Vec3d n)
        {
            double ax = System.Math.Abs(n.X), az = System.Math.Abs(n.Z);
            double side = ax + az > 1e-9 ? (ax * ShadeEastWest + az * ShadeNorthSouth) / (ax + az) : ShadeTop;
            if (n.Y >= 0) return (float)(side + (ShadeTop - side) * n.Y);
            return (float)(side + (ShadeBottom - side) * -n.Y);
        }

        private Color Lin(Color c) => _linear ? c.linear : c;
    }
}
