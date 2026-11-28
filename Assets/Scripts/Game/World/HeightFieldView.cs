using System;
using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Draws the smooth height-map ground (<see cref="HeightField"/>) in 48 m patches. Near the camera a patch is exactly
    /// the triangles Core collides with and shoots against (each 1.5 m cell's four triangles meeting at its centre); only
    /// the shading is smooth. Further out it is a coarser copy (one vertex every 2, 4 or 8 cells) that never rises above
    /// the true surface (<see cref="HeightFieldLod"/>, map v3 step 4): a far hill can look a little lower than it is,
    /// never higher, so the simpler ground never hides anyone. Every patch hangs a skirt down from its edges, so where a
    /// finer patch meets a lower, coarser one there is no crack (the skirt is under the true surface: never in the way).
    /// Grass on walkable slopes, soil then rock as a slope steepens; building footings' level ground in pale stone.
    /// </summary>
    public sealed class HeightFieldView : MonoBehaviour
    {
        public const int PatchCells = 32;
        /// <summary>A patch is drawn at level k (exact, then 2, 4, 8 cells a vertex) from this distance to its nearest point.</summary>
        public static readonly float[] LevelFrom = { 0f, 140f, 300f, 520f };
        /// <summary>Exact patches are built near the camera within this budget a frame (milliseconds).</summary>
        public const double BuildBudgetMs = 1.5;

        /// <summary>Triangles of the whole ground at full detail.</summary>
        public int Triangles { get; private set; }
        public int Patches => _patches.Count;
        /// <summary>Triangles of the patches as drawn right now (skirts not counted).</summary>
        public int DrawnTriangles { get; private set; }

        private sealed class Patch
        {
            public int Pi, Pj, Ni, Nj;
            public Rect Area;
            public float Skirt;
            public readonly Mesh[] Meshes = new Mesh[4];
            public MeshFilter Filter;
            public int Shown = -1;
            public int Wanted;
        }

        private HeightField _f;
        private readonly HeightField[] _levels = new HeightField[4];
        private readonly List<Patch> _patches = new List<Patch>();
        private Func<double, double, float> _paved;
        private readonly List<Patch> _queue = new List<Patch>();

        /// <param name="paved">Ground drawn as stone (a building footing's level ground), or null.</param>
        public void Build(HeightField f, Material material, Func<double, double, float> paved = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _f = f;
            _paved = paved;
            _levels[0] = f;
            for (int k = 1; k < 4; k++)
            {
                int step = 1 << k;
                _levels[k] = f.NX % step == 0 && f.NZ % step == 0 ? HeightFieldLod.Coarse(f, step) : null;
            }
            for (int pj = 0; pj < f.NZ; pj += PatchCells)
                for (int pi = 0; pi < f.NX; pi += PatchCells)
                {
                    var p = new Patch { Pi = pi, Pj = pj, Ni = Mathf.Min(PatchCells, f.NX - pi), Nj = Mathf.Min(PatchCells, f.NZ - pj) };
                    p.Area = new Rect((float)f.VertexX(pi), (float)f.VertexZ(pj), (float)(p.Ni * f.Cell), (float)(p.Nj * f.Cell));
                    p.Skirt = SkirtDepth(p);
                    var go = new GameObject($"Ground {pi},{pj}");
                    go.transform.SetParent(transform, false);
                    p.Filter = go.AddComponent<MeshFilter>();
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    for (int k = 1; k < 4; k++)
                        if (Usable(p, k)) p.Meshes[k] = Mesh(p, k);
                    _patches.Add(p);
                    Triangles += p.Ni * p.Nj * 4;
                }
            Pick(true);
            Debug.Log($"[SCULPT] Smooth ground: {_patches.Count} patches ({Triangles / 1000} thousand triangles at full detail), far levels built in {sw.ElapsedMilliseconds} ms.");
        }

        /// <summary>A level can draw this patch if it exists and its step divides the patch's cells.</summary>
        private bool Usable(Patch p, int k)
        {
            if (_levels[k] == null) return false;
            int step = 1 << k;
            return p.Pi % step == 0 && p.Pj % step == 0 && p.Ni % step == 0 && p.Nj % step == 0;
        }

        /// <summary>How far down a patch's skirt must hang: past the lowest any coarser level puts its edges, plus a margin.</summary>
        private float SkirtDepth(Patch p)
        {
            double most = 0;
            for (int k = 1; k < 4; k++)
            {
                HeightField c = _levels[k];
                if (c == null) continue;
                for (int e = 0; e <= p.Ni; e++)
                    foreach (int j in new[] { p.Pj, p.Pj + p.Nj })
                        most = Math.Max(most, _f[p.Pi + e, j] - c.HeightAt(_f.VertexX(p.Pi + e), _f.VertexZ(j)));
                for (int e = 0; e <= p.Nj; e++)
                    foreach (int i in new[] { p.Pi, p.Pi + p.Ni })
                        most = Math.Max(most, _f[i, p.Pj + e] - c.HeightAt(_f.VertexX(i), _f.VertexZ(p.Pj + e)));
            }
            return (float)most + 0.5f;
        }

        private void Update() => Pick(false);

        private void Pick(bool all)
        {
            Camera cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            _queue.Clear();
            int drawn = 0;
            foreach (Patch p in _patches)
            {
                float d = Mathf.Sqrt(Dist(p, eye));
                // Hysteresis: a patch goes coarser a few metres past the line it came finer at.
                int want = 0;
                for (int k = 3; k >= 1; k--)
                    if (d >= LevelFrom[k] + (p.Shown >= 0 && p.Shown < k ? 12f : 0f)) { want = k; break; }
                while (want > 0 && !Usable(p, want)) want--;
                p.Wanted = want;
                if (want == 0 && p.Meshes[0] == null) _queue.Add(p);
                int show = want;
                while (show < 3 && p.Meshes[show] == null) show++; // the exact mesh isn't built yet: a coarser one meanwhile
                if (show != p.Shown && p.Meshes[show] != null)
                {
                    p.Filter.sharedMesh = p.Meshes[show];
                    p.Shown = show;
                }
                drawn += (p.Ni * p.Nj * 4) >> (2 * Mathf.Max(0, p.Shown));
            }
            DrawnTriangles = drawn;
            if (_queue.Count > 0)
            {
                // Exact patches, nearest first, within the frame's budget (all of them at load).
                _queue.Sort((a, b) => Dist(a, eye).CompareTo(Dist(b, eye)));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (Patch p in _queue)
                {
                    if (!all && sw.Elapsed.TotalMilliseconds > BuildBudgetMs) break;
                    p.Meshes[0] = Mesh(p, 0);
                    p.Filter.sharedMesh = p.Meshes[0];
                    p.Shown = 0;
                }
            }
            // Exact meshes well behind are dropped (rebuilt in a few frames if needed again).
            foreach (Patch p in _patches)
                if (p.Meshes[0] != null && p.Wanted >= 2 && p.Shown != 0)
                {
                    Destroy(p.Meshes[0]);
                    p.Meshes[0] = null;
                }
        }

        private static float Dist(Patch p, Vector3 eye)
        {
            float dx = Mathf.Max(0f, Mathf.Max(p.Area.xMin - eye.x, eye.x - p.Area.xMax));
            float dz = Mathf.Max(0f, Mathf.Max(p.Area.yMin - eye.z, eye.z - p.Area.yMax));
            return dx * dx + dz * dz;
        }

        private Mesh Mesh(Patch p, int level)
        {
            HeightField f = _levels[level];
            int step = 1 << level;
            int pi = p.Pi / step, pj = p.Pj / step, ni = p.Ni / step, nj = p.Nj / step;
            int corners = (ni + 1) * (nj + 1);
            int skirtVerts = 2 * (ni + nj) * 2;
            int count = corners + ni * nj + skirtVerts;
            var verts = new Vector3[count];
            var normals = new Vector3[count];
            var colors = new Color[count];
            var uv0 = new List<Vector3>(count);
            var uv1 = new List<Vector2>(count);
            var uv2 = new List<Vector2>(count);
            for (int k = 0; k < count; k++) { uv0.Add(Vector3.zero); uv1.Add(Vector2.zero); uv2.Add(Vector2.zero); }
            double c = f.Cell;
            void Put(int k, double x, double y, double z, Vec3d n)
            {
                verts[k] = new Vector3((float)x, (float)y, (float)z);
                normals[k] = n.ToUnity();
                float shade = ChunkMesher.Shade(n);
                colors[k] = new Color(shade, shade, shade, 1);
                // Paving weight round buildings for callers that pass one (the battle royale's comes from MeadowView's
                // texture instead, so it can't depend on which detail level of the mesh is showing).
                uv2[k] = new Vector2(_paved != null ? _paved(x, z) : 0f, 0f);
                // Depth below the "top" drives grass -> soil -> rock in the ground palette: steeper is deeper.
                float depth = Mathf.Clamp((float)((0.8 - n.Y) * 6.0), 0f, 2.4f);
                uv1[k] = new Vector2(ChunkMesher.KindGround, depth);
            }
            for (int j = 0; j <= nj; j++)
                for (int i = 0; i <= ni; i++)
                {
                    int gi = pi + i, gj = pj + j;
                    Put(j * (ni + 1) + i, f.VertexX(gi), f[gi, gj], f.VertexZ(gj), CornerNormal(f, gi, gj));
                }
            var tris = new List<int>(ni * nj * 12 + skirtVerts * 6);
            for (int j = 0; j < nj; j++)
                for (int i = 0; i < ni; i++)
                {
                    int gi = pi + i, gj = pj + j;
                    double h00 = f[gi, gj], h10 = f[gi + 1, gj], h11 = f[gi + 1, gj + 1], h01 = f[gi, gj + 1];
                    double hc = (h00 + h10 + h11 + h01) * 0.25;
                    var n = new Vec3d(-((h10 + h11) - (h00 + h01)) / (2 * c), 1, -((h01 + h11) - (h00 + h10)) / (2 * c)).Normalized;
                    int ci = corners + j * ni + i;
                    Put(ci, f.VertexX(gi) + c * 0.5, hc, f.VertexZ(gj) + c * 0.5, n);
                    int v00 = j * (ni + 1) + i, v10 = v00 + 1, v01 = v00 + ni + 1, v11 = v01 + 1;
                    // South, east, north, west; wound so they face up (Unity: clockwise seen from above).
                    Tri(tris, v00, ci, v10);
                    Tri(tris, v10, ci, v11);
                    Tri(tris, v11, ci, v01);
                    Tri(tris, v01, ci, v00);
                }
            // Skirts: each edge segment's two vertices again, hung straight down; both faces drawn.
            int s0 = corners + ni * nj;
            void Skirt(int top0, int top1)
            {
                int a = s0, b = s0 + 1;
                foreach (int t in new[] { top0, top1 })
                {
                    verts[s0] = verts[t] - new Vector3(0, p.Skirt, 0);
                    normals[s0] = normals[t];
                    colors[s0] = colors[t];
                    uv1[s0] = new Vector2(ChunkMesher.KindGround, 2.4f);
                    s0++;
                }
                Tri(tris, top0, top1, b); Tri(tris, top0, b, a);
                Tri(tris, top0, a, b); Tri(tris, top0, b, top1);
            }
            for (int i = 0; i < ni; i++)
            {
                Skirt(i, i + 1);                                  // south edge
                Skirt(nj * (ni + 1) + i, nj * (ni + 1) + i + 1);  // north edge
            }
            for (int j = 0; j < nj; j++)
            {
                Skirt(j * (ni + 1), (j + 1) * (ni + 1));            // west edge
                Skirt(j * (ni + 1) + ni, (j + 1) * (ni + 1) + ni);  // east edge
            }
            var mesh = new Mesh { name = $"Height field L{level}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetUVs(2, uv2);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static void Tri(List<int> tris, int a, int b, int c)
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        /// <summary>Smooth shading normal at a grid vertex (central differences, one-sided at the edges).</summary>
        private static Vec3d CornerNormal(HeightField f, int i, int j)
        {
            int i0 = Mathf.Max(0, i - 1), i1 = Mathf.Min(f.NX, i + 1), j0 = Mathf.Max(0, j - 1), j1 = Mathf.Min(f.NZ, j + 1);
            double dx = (f[i1, j] - f[i0, j]) / ((i1 - i0) * f.Cell), dz = (f[i, j1] - f[i, j0]) / ((j1 - j0) * f.Cell);
            return new Vec3d(-dx, 1, -dz).Normalized;
        }

        private void OnDestroy()
        {
            foreach (Patch p in _patches)
                foreach (Mesh m in p.Meshes)
                    if (m != null) Destroy(m);
        }
    }
}
