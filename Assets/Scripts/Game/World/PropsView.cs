using System;
using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// The battle royale's plains (<see cref="BrNature"/>): every prop of the design eighth is meshed once and placed
    /// eight times like the map's symmetries; the copies are merged into one mesh per 128 m cluster of the map (map v3
    /// groundwork: 520 renderers became about 60). The props are the same sub-cells in Core (collision, bullets): this
    /// only draws them. Past <see cref="BrushView.FarDetailFrom"/> a cluster swaps to stand-ins: each tree and bush as
    /// the plain boxes of its own sub-cells with their faces merged (no chamfers), so its outline stays within about
    /// 0.1 m of the real one (under a pixel at that range) and nothing appears or disappears that could hide a player.
    /// Boulders keep their own mesh (their colour comes from where they lie).
    /// </summary>
    public sealed class PropsView : MonoBehaviour
    {
        public int Triangles { get; private set; }
        public int FarTriangles { get; private set; }
        private const float ClusterSize = 128f;

        private sealed class Cluster
        {
            public Rect Area;
            public MeshRenderer Near, Far;
            public bool ShowsFar;
        }

        private readonly List<Cluster> _clusters = new List<Cluster>();

        private struct Part
        {
            public int Prop;
            public int Sym;
        }

        public void Build(Material material)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BrNature.Plan plan = BrNature.GetPlan();
            float c = (float)(BrMap.Size / 2);
            const int cs = SubGrid.ChunkSize;
            float sub = (float)SubGrid.SubSize;
            var mesher = new ChunkMesher { AnchorsOnly = true, MarkSculptable = false, FlatFastPath = false };
            var chunks = new List<Int3>();
            var offsets = new List<Vector3>();
            var nearMeshes = new Mesh[plan.Props.Count];
            var farMeshes = new Mesh[plan.Props.Count];
            for (int k = 0; k < plan.Props.Count; k++)
            {
                BrNature.Prop p = plan.Props[k];
                int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
                bool stone = false;
                for (int i = p.CellStart; i < p.CellStart + p.CellCount; i++)
                {
                    BrNature.Cell q = plan.Cells[i];
                    x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X);
                    z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
                    y0 = Mathf.Min(y0, q.Y); y1 = Mathf.Max(y1, q.Y);
                    if (q.Kind == (byte)ChunkMesher.KindStone) stone = true;
                }
                // A scratch grid round the prop, one sub-cell of air round it. The prop stands on the smooth ground (map
                // v3): its lowest layers are under the ground, so the faces down there are never seen.
                int ox = x0 - 2, oz = z0 - 2;
                int oy = (int)Math.Floor((y0 - 2) / (double)cs) * cs;
                int cx = (x1 - ox + 3 + cs - 1) / cs, cz = (z1 - oz + 3 + cs - 1) / cs, cy = (y1 + 2 - oy + cs) / cs;
                var g = new SubGrid(cx, cy, cz, oy);
                for (int i = p.CellStart; i < p.CellStart + p.CellCount; i++)
                {
                    BrNature.Cell q = plan.Cells[i];
                    g.Set(q.X - ox, q.Y, q.Z - oz, SubId.Anchor);
                }
                mesher.DecorOf = s => BrNature.KindAt(new Int3(s.X + ox, s.Y, s.Z + oz));
                chunks.Clear();
                offsets.Clear();
                Vector3 origin = new Vector3(ox * sub, 0, oz * sub); // the scratch grid's corner on the map (design eighth)
                for (int yy = 0; yy < cy; yy++)
                    for (int zz = 0; zz < cz; zz++)
                        for (int xx = 0; xx < cx; xx++)
                        {
                            var cc = new Int3(xx, yy, zz);
                            chunks.Add(cc);
                            offsets.Add(origin + g.ChunkOrigin(cc).ToUnity() * sub);
                        }
                var mesh = new Mesh { name = $"Prop {k} ({p.Kind})" };
                mesher.BuildGroup(g, chunks, offsets, mesh);
                nearMeshes[k] = mesh;
                farMeshes[k] = stone ? mesh : StandIn(plan, p);
            }

            // Every copy of every prop goes into the cluster its centre lands in.
            var byCluster = new Dictionary<Vector2Int, List<Part>>();
            for (int s = 0; s < BrMap.Sym.Length; s++)
                for (int k = 0; k < plan.Props.Count; k++)
                {
                    BrNature.Prop p = plan.Props[k];
                    Vec3d at = BrMap.Tr(new Vec3d(p.X, 0, p.Z), BrMap.Sym[s], c);
                    var key = new Vector2Int(Mathf.FloorToInt((float)at.X / ClusterSize), Mathf.FloorToInt((float)at.Z / ClusterSize));
                    if (!byCluster.TryGetValue(key, out var list)) byCluster[key] = list = new List<Part>();
                    list.Add(new Part { Prop = k, Sym = s });
                }
            long tris = 0, farTris = 0;
            foreach (var kv in byCluster)
            {
                // A copy can reach out of its cluster a little: the area grows by a prop's reach so the switch is fair.
                var cl = new Cluster { Area = new Rect(kv.Key.x * ClusterSize - 12f, kv.Key.y * ClusterSize - 12f, ClusterSize + 24f, ClusterSize + 24f) };
                cl.Near = Renderer(material, $"Props {kv.Key}", Combine(nearMeshes, kv.Value, c), ref tris);
                cl.Far = Renderer(material, $"Props far {kv.Key}", Combine(farMeshes, kv.Value, c), ref farTris);
                cl.Far.enabled = false;
                _clusters.Add(cl);
            }
            var gone = new HashSet<Mesh>(nearMeshes);
            gone.UnionWith(farMeshes);
            foreach (Mesh m in gone) Destroy(m);
            Triangles = (int)tris;
            FarTriangles = (int)farTris;
            Debug.Log($"[SCULPT] Plains: {plan.Props.Count} props meshed once each, placed 8 times in {_clusters.Count} clusters ({Triangles / 1000} thousand triangles near, {FarTriangles / 1000} thousand as stand-ins), in {sw.ElapsedMilliseconds} ms.");
        }

        private void Update()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            foreach (Cluster cl in _clusters)
            {
                float dx = Mathf.Max(0f, Mathf.Max(cl.Area.xMin - p.x, p.x - cl.Area.xMax));
                float dz = Mathf.Max(0f, Mathf.Max(cl.Area.yMin - p.z, p.z - cl.Area.yMax));
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                bool far = cl.ShowsFar ? d > BrushView.FarDetailFrom : d > BrushView.FarDetailFrom + 24f;
                if (far == cl.ShowsFar) continue;
                cl.ShowsFar = far;
                cl.Near.enabled = !far;
                cl.Far.enabled = far;
            }
        }

        private MeshRenderer Renderer(Material material, string name, Mesh mesh, ref long tris)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on
            r.receiveShadows = true;
            tris += mesh.GetIndexCount(0) / 3;
            return r;
        }

        private static readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>(), U0 = new List<Vector3>();
        private static readonly List<Vector2> U1 = new List<Vector2>();
        private static readonly List<Color> C = new List<Color>();
        private static readonly List<int> T = new List<int>();
        private static readonly List<Vector3> SrcV = new List<Vector3>(), SrcN = new List<Vector3>(), SrcU0 = new List<Vector3>();
        private static readonly List<Vector2> SrcU1 = new List<Vector2>();
        private static readonly List<Color> SrcC = new List<Color>();
        private static readonly List<int> SrcT = new List<int>();

        /// <summary>The copies of props in one cluster as one mesh, each moved by its symmetry (mirrored ones turned round).</summary>
        private static Mesh Combine(Mesh[] meshes, List<Part> parts, float c)
        {
            V.Clear(); N.Clear(); U0.Clear(); U1.Clear(); C.Clear(); T.Clear();
            foreach (Part part in parts)
            {
                Mesh m = meshes[part.Prop];
                int[] s = BrMap.Sym[part.Sym];
                bool mirror = s[0] * s[3] - s[1] * s[2] < 0;
                m.GetVertices(SrcV); m.GetNormals(SrcN); m.GetColors(SrcC); m.GetUVs(0, SrcU0); m.GetUVs(1, SrcU1); m.GetTriangles(SrcT, 0);
                int baseV = V.Count;
                for (int i = 0; i < SrcV.Count; i++)
                {
                    Vector3 p = SrcV[i], n = SrcN.Count == SrcV.Count ? SrcN[i] : Vector3.up;
                    // (u, v) = (x - c, z - c) -> (a u + b v, c u + d v): the map's own symmetry (BrMap.Tr).
                    float u = p.x - c, w = p.z - c;
                    V.Add(new Vector3(c + s[0] * u + s[1] * w, p.y, c + s[2] * u + s[3] * w));
                    N.Add(new Vector3(s[0] * n.x + s[1] * n.z, n.y, s[2] * n.x + s[3] * n.z));
                }
                if (SrcC.Count == SrcV.Count) C.AddRange(SrcC); else for (int i = 0; i < SrcV.Count; i++) C.Add(Color.white);
                if (SrcU0.Count == SrcV.Count) U0.AddRange(SrcU0); else for (int i = 0; i < SrcV.Count; i++) U0.Add(Vector3.zero);
                if (SrcU1.Count == SrcV.Count) U1.AddRange(SrcU1); else for (int i = 0; i < SrcV.Count; i++) U1.Add(Vector2.zero);
                for (int i = 0; i < SrcT.Count; i += 3)
                {
                    T.Add(baseV + SrcT[i]);
                    if (mirror) { T.Add(baseV + SrcT[i + 2]); T.Add(baseV + SrcT[i + 1]); }
                    else { T.Add(baseV + SrcT[i + 1]); T.Add(baseV + SrcT[i + 2]); }
                }
            }
            var mesh = new Mesh { name = "Props", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(V);
            mesh.SetNormals(N);
            mesh.SetColors(C);
            mesh.SetUVs(0, U0);
            mesh.SetUVs(1, U1);
            mesh.SetTriangles(T, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true); // never changes: no copy kept in memory
            return mesh;
        }

        /// <summary>
        /// A tree or bush as the boxes of its own sub-cells, every face that shows merged into rectangles of one kind
        /// (bark or leaves). Same cells, so the same outline as the real prop give or take its 0.15 m chamfers.
        /// </summary>
        private static Mesh StandIn(BrNature.Plan plan, BrNature.Prop p)
        {
            var cells = new Dictionary<(int, int, int), byte>();
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            for (int i = p.CellStart; i < p.CellStart + p.CellCount; i++)
            {
                BrNature.Cell q = plan.Cells[i];
                cells[(q.X, q.Y, q.Z)] = q.Kind;
                x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X);
                y0 = Mathf.Min(y0, q.Y); y1 = Mathf.Max(y1, q.Y);
                z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
            }
            V.Clear(); N.Clear(); U0.Clear(); U1.Clear(); C.Clear(); T.Clear();
            float s = (float)SubGrid.SubSize;
            int[] lo = { x0, y0, z0 }, hi = { x1, y1, z1 };
            var at = new int[3];
            var q3 = new float[3];
            // For each axis and side: slices across it, a mask of the faces that show, greedy rectangles of one kind.
            for (int axis = 0; axis < 3; axis++)
                for (int side = -1; side <= 1; side += 2)
                {
                    int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    int nu = hi[ua] - lo[ua] + 1, nv = hi[va] - lo[va] + 1;
                    var mask = new byte[nu * nv]; // kind + 1, 0 = no face
                    for (int d = lo[axis]; d <= hi[axis]; d++)
                    {
                        System.Array.Clear(mask, 0, mask.Length);
                        for (int v = 0; v < nv; v++)
                            for (int u = 0; u < nu; u++)
                            {
                                at[axis] = d; at[ua] = lo[ua] + u; at[va] = lo[va] + v;
                                if (!cells.TryGetValue((at[0], at[1], at[2]), out byte kind)) continue;
                                at[axis] += side;
                                if (cells.ContainsKey((at[0], at[1], at[2]))) continue;
                                mask[u + nu * v] = (byte)(kind + 1);
                            }
                        for (int v = 0; v < nv; v++)
                            for (int u = 0; u < nu; u++)
                            {
                                byte k = mask[u + nu * v];
                                if (k == 0) continue;
                                int w = 1;
                                while (u + w < nu && mask[u + w + nu * v] == k) w++;
                                int h = 1;
                                for (bool grow = true; grow && v + h < nv; )
                                {
                                    for (int x = 0; x < w; x++) if (mask[u + x + nu * (v + h)] != k) { grow = false; break; }
                                    if (grow) h++;
                                }
                                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) mask[u + x + nu * (v + y)] = 0;
                                float plane = (side > 0 ? d + 1 : d) * s;
                                Vector3 P(int du, int dv)
                                {
                                    q3[axis] = plane; q3[ua] = (lo[ua] + du) * s; q3[va] = (lo[va] + dv) * s;
                                    return new Vector3(q3[0], q3[1], q3[2]);
                                }
                                var normal = new Vector3(axis == 0 ? side : 0, axis == 1 ? side : 0, axis == 2 ? side : 0);
                                AddQuad(P(u, v), P(u + w, v), P(u + w, v + h), P(u, v + h), normal, k - 1);
                            }
                    }
                }
            var mesh = new Mesh { name = "Stand-in", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(V);
            mesh.SetNormals(N);
            mesh.SetColors(C);
            mesh.SetUVs(0, U0);
            mesh.SetUVs(1, U1);
            mesh.SetTriangles(T, 0);
            return mesh;
        }

        private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int kind)
        {
            // Front faces: Cross(b - a, c - a) along the outward normal (as the chunk mesher's faces).
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) < 0) { Vector3 t = b; b = d; d = t; }
            int i = V.Count;
            V.Add(a); V.Add(b); V.Add(c); V.Add(d);
            Color col = Color.white * ChunkMesher.Shade(new Vec3d(n.x, n.y, n.z));
            col.a = 1f;
            for (int k = 0; k < 4; k++) { N.Add(n); C.Add(col); U0.Add(Vector3.zero); U1.Add(new Vector2(kind, 0f)); }
            T.Add(i); T.Add(i + 1); T.Add(i + 2);
            T.Add(i); T.Add(i + 2); T.Add(i + 3);
        }
    }
}
