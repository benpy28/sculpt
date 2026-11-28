using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Draws the smooth height-map ground (<see cref="HeightField"/>) as exactly the triangles Core collides with and
    /// shoots against: each 1.5 m cell's four triangles meeting at its centre, in 48 m patches. Only the shading is smooth
    /// (normals averaged at the vertices); the shape is the simulation's to the millimetre. Grass on walkable slopes,
    /// soil then rock as a slope steepens, so a rock face you can't stand on also looks like one.
    /// </summary>
    public sealed class HeightFieldView : MonoBehaviour
    {
        public const int PatchCells = 32;
        public int Triangles { get; private set; }
        public int Patches { get; private set; }

        public void Build(HeightField f, Material material)
        {
            for (int pj = 0; pj < f.NZ; pj += PatchCells)
                for (int pi = 0; pi < f.NX; pi += PatchCells)
                {
                    int ni = Mathf.Min(PatchCells, f.NX - pi), nj = Mathf.Min(PatchCells, f.NZ - pj);
                    Mesh mesh = Patch(f, pi, pj, ni, nj);
                    var go = new GameObject($"Ground {pi},{pj}");
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    Triangles += ni * nj * 4;
                    Patches++;
                }
        }

        private static Mesh Patch(HeightField f, int pi, int pj, int ni, int nj)
        {
            int corners = (ni + 1) * (nj + 1);
            var verts = new Vector3[corners + ni * nj];
            var normals = new Vector3[verts.Length];
            var colors = new Color[verts.Length];
            var uv0 = new List<Vector3>(verts.Length);
            var uv1 = new List<Vector2>(verts.Length);
            for (int k = 0; k < verts.Length; k++) { uv0.Add(Vector3.zero); uv1.Add(Vector2.zero); }
            double c = f.Cell;
            void Put(int k, double x, double y, double z, Vec3d n)
            {
                verts[k] = new Vector3((float)x, (float)y, (float)z);
                normals[k] = n.ToUnity();
                float shade = ChunkMesher.Shade(n);
                colors[k] = new Color(shade, shade, shade, 1);
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
            var tris = new int[ni * nj * 12];
            int t = 0;
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
                    t = Tri(tris, t, v00, ci, v10);
                    t = Tri(tris, t, v10, ci, v11);
                    t = Tri(tris, t, v11, ci, v01);
                    t = Tri(tris, t, v01, ci, v00);
                }
            var mesh = new Mesh { name = "Height field", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static int Tri(int[] tris, int t, int a, int b, int c)
        {
            tris[t] = a; tris[t + 1] = b; tris[t + 2] = c;
            return t + 3;
        }

        /// <summary>Smooth shading normal at a grid vertex (central differences, one-sided at the edges).</summary>
        private static Vec3d CornerNormal(HeightField f, int i, int j)
        {
            int i0 = Mathf.Max(0, i - 1), i1 = Mathf.Min(f.NX, i + 1), j0 = Mathf.Max(0, j - 1), j1 = Mathf.Min(f.NZ, j + 1);
            double dx = (f[i1, j] - f[i0, j]) / ((i1 - i0) * f.Cell), dz = (f[i, j1] - f[i, j0]) / ((j1 - j0) * f.Cell);
            return new Vec3d(-dx, 1, -dz).Normalized;
        }
    }
}
