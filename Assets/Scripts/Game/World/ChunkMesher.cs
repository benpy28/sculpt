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
        /// <summary>Sculpted pieces: pale amethyst crystal (purple means sculptable; every piece is). The shader adds facets, gloss and glow.</summary>
        public static readonly Color Crystal = new Color(0.50f, 0.30f, 0.88f);
        public static readonly Color[] TeamTint = { new Color(0.30f, 0.55f, 1.00f), new Color(1.00f, 0.55f, 0.20f) }; // blue, orange: never purple

        /// <summary>Optional: returns (team, 0..1 strength) for a piece's fading tint.</summary>
        public System.Func<ushort, (int team, float strength)> PieceTint;

        /// <summary>Optional: 0..1 reinforcement of a piece (crystalline veins in the shader), stored as 1 - vertex alpha.</summary>
        public System.Func<ushort, float> PieceReinforce;

        /// <summary>
        /// Optional: sub-cells not drawn right now (a piece whose landing is being animated by a separate mesh for a few
        /// frames). Visual only; collision and bullets always use the real grid.
        /// </summary>
        public HashSet<Int3> Hidden;

        /// <summary>Flag sculptable faces in uv.y (off for ghosts and flying pieces).</summary>
        public bool MarkSculptable = true;

        /// <summary>
        /// Free-standing meshes (ghosts, flying and snapping pieces): draw only faces of this id and give them the
        /// crystal look. Their scratch grid reads as bedrock outside its bounds, which otherwise meshed a 9.6 m open
        /// box around the piece (the "plates" from the play-test).
        /// </summary>
        public ushort OnlyId;

        /// <summary>Material kinds in uv1.x: the shader paints each its own way. Purely visual.</summary>
        public const float KindStone = 0f, KindGround = 1f, KindWood = 2f, KindLeaves = 3f;

        /// <summary>
        /// Optional: anchor sub-cells drawn as bark (<see cref="KindWood"/>) or foliage (<see cref="KindLeaves"/>), from
        /// the map's tree list. They stay anchors in Core: solid, unsculptable, bullet-stopping.
        /// </summary>
        public Dictionary<Int3, byte> Decor;

        /// <summary>Optional: natural ground drawn as concrete instead of meadow (building ground floors). Visual only.</summary>
        public GroundCover Cover;

        private readonly List<Vector3> _verts = new List<Vector3>(16384);
        private readonly List<Color> _colors = new List<Color>(16384);
        private readonly List<Vector3> _uvs = new List<Vector3>(16384);
        private readonly List<Vector2> _uv1 = new List<Vector2>(16384);
        private readonly List<Vector3> _normals = new List<Vector3>(16384);
        private readonly List<int> _tris = new List<int>(24576);
        // Read lazily: Unity forbids QualitySettings in constructors, and meshers are created from field initializers.
        private int _linearState; // 0 unknown, 1 gamma, 2 linear
        private bool Linear
        {
            get
            {
                if (_linearState == 0) _linearState = QualitySettings.activeColorSpace == ColorSpace.Linear ? 2 : 1;
                return _linearState == 2;
            }
        }

        /// <summary>True if every sub-cell the chunk's own 16 dual layers read (the chunk and its -x, -y, -z neighbours)
        /// is uniformly solid or uniformly empty.</summary>
        private static bool NoSurface(SubGrid g, Int3 o)
        {
            int cx = o.X >> 4, cy = (o.Y - g.MinY) >> 4, cz = o.Z >> 4;
            int want = g.ChunkSolidity(cx, cy, cz);
            if (want < 0) return false;
            for (int y = cy - 1; y <= cy; y++)
                for (int z = cz - 1; z <= cz; z++)
                    for (int x = cx - 1; x <= cx; x++)
                        if (g.ChunkSolidity(x, y, z) != want) return false;
            return true;
        }

        public void Build(SubGrid g, Int3 chunkCoord, Mesh mesh)
        {
            _verts.Clear();
            _colors.Clear();
            _uvs.Clear();
            _uv1.Clear();
            _normals.Clear();
            _tris.Clear();

            Int3 o = g.ChunkOrigin(chunkCoord);
            const int n = SubGrid.ChunkSize;
            const float h = (float)TerrainShape.H;
            const float s = (float)SubGrid.SubSize;
            // Dual cells sit on the lattice vertices below each sub-cell. The top layer of chunks also owns the vertex
            // layer on the grid's ceiling: a ground surface lying exactly on it (flat base ground at y 0 on the breach
            // and duel maps) was never drawn, and the sky showed through where the ground should be.
            int ny = o.Y + n >= g.MaxYExclusive ? n + 1 : n;
            // The chunk's own 16 dual layers read only it and its -x, -y, -z neighbours: if those are all rock or all
            // air there is nothing to draw in them, and only the grid-top layer (if this chunk has it) is left. On the
            // flat duel ground that leaves one layer in sixteen; loading the map got about a second faster.
            int y0 = 0;
            if (NoSurface(g, o))
            {
                if (ny == n) { mesh.Clear(); return; }
                y0 = n;
            }
            for (int y = y0; y < ny; y++)
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
            mesh.SetUVs(1, _uv1);
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
            if (Hidden != null && Hidden.Count > 0 && Hidden.Contains(owner)) return;
            ushort ownerId = g.Get(owner);
            if (OnlyId != 0 && ownerId != OnlyId) return;
            float kind = KindStone;
            if (OnlyId == 0)
            {
                if (ownerId == SubId.Natural) kind = KindGround;
                else if (ownerId == SubId.Anchor && Decor != null && Decor.TryGetValue(owner, out byte dk)) kind = dk;
            }
            bool paved = kind == KindGround && Cover != null && Cover.Paved(owner.X, owner.Z);
            if (paved) kind = KindStone;
            // Ground and trees take their colour from the shader's palette; the vertex colour carries shade and AO only.
            Color albedo = (kind != KindStone ? Color.white : Lin(paved ? GroundCover.Concrete : Albedo(ownerId, owner))) * Shade(f.Normal);
            // Ground: how far below the column's top this face's rock sits (grass lip, then soil, then rock).
            int topY = owner.Y + 1;
            if (kind == KindGround)
                while (topY < owner.Y + 8 && g.Get(owner.X, topY, owner.Z) != SubId.Empty) topY++;
            albedo.a = 1f - (PieceReinforce != null && SubId.IsPiece(ownerId) ? Mathf.Clamp01(PieceReinforce(ownerId)) : 0f);
            // uv.y: 1 on sculptable rock (the shader draws the faint purple edge glow and the sculpt-mode overlay there).
            float sculptable = SubId.IsSculptable(ownerId) && owner.Y >= SubGrid.SculptMinY && owner.Y < 4 * SubGrid.TileSubs ? 1f : 0f;
            if (!MarkSculptable) sculptable = 0f;
            // uv.z: 1 on sculpted pieces (crystal look), also while they snap or fly.
            float crystal = SubId.IsPiece(ownerId) || OnlyId != 0 ? 1f : 0f;

            int start = _verts.Count;
            for (int i = 0; i < f.Verts.Length; i++)
            {
                Vec3d lv = f.Verts[i];
                _verts.Add(cellMin + new Vector3((float)lv.X, (float)lv.Y, (float)lv.Z) * h);
                _normals.Add(normal);
                Color vc = albedo;
                float ao = OnlyId != 0 ? 1f : Occlusion(g, worldMin + lv * TerrainShape.H);
                vc.r *= ao; vc.g *= ao; vc.b *= ao;
                _colors.Add(vc);
                // Seam weight: the vertex sits next to a different solid id than the face's owner.
                Vec3d wv = worldMin + lv * TerrainShape.H;
                Int3 near = TerrainGeometry.NearestOccupied(cfg, vx, vy, vz, wv);
                ushort nid = g.Get(near);
                float seam = nid != ownerId && (SubId.IsPiece(nid) || SubId.IsPiece(ownerId)) ? 1f : 0f;
                _uvs.Add(new Vector3(seam, sculptable, crystal));
                float depth = kind == KindGround ? Mathf.Clamp((float)(topY * SubGrid.SubSize - wv.Y), 0f, 2.4f) : 0f;
                _uv1.Add(new Vector2(kind, depth));
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

        // Vertex ambient occlusion: how much rock surrounds the vertex, at two radii. Concave corners and the feet of
        // steps darken, convex edges lift a little. Visual only; it breaks up the flat per-axis shading so steps read
        // as rock forms instead of a staircase of cubes.
        private const float AoDark = 0.42f, AoLift = 0.10f;

        private static float Occlusion(SubGrid g, Vec3d p)
        {
            float near = Solid(g, p, 0.15), far = Solid(g, p, 0.45);
            float o = near * 0.6f + far * 0.4f; // 0.5 on a flat floor or wall
            if (o >= 0.5f) return 1f - AoDark * (o - 0.5f) * 2f * 0.5f;
            return 1f + AoLift * (0.5f - o) * 2f;
        }

        private static float Solid(SubGrid g, Vec3d p, double r)
        {
            int solid = 0;
            for (int k = 0; k < 8; k++)
            {
                double x = p.X + ((k & 1) != 0 ? r : -r), y = p.Y + ((k & 2) != 0 ? r : -r), z = p.Z + ((k & 4) != 0 ? r : -r);
                var c = new Int3((int)System.Math.Floor(x / SubGrid.SubSize), (int)System.Math.Floor(y / SubGrid.SubSize), (int)System.Math.Floor(z / SubGrid.SubSize));
                if (g.InBounds(c) && g.Get(c) != SubId.Empty) solid++;
            }
            return solid / 8f;
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
                    c = Crystal * v;
                    if (PieceTint != null)
                    {
                        var (team, strength) = PieceTint(id);
                        if (team >= 1 && team <= 2 && strength > 0) c = Color.Lerp(c, TeamTint[team - 1], (SubId.IsPiece(id) ? 0.08f : 0.18f) * strength);
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

        private Color Lin(Color c) => Linear ? c.linear : c;
    }
}
