using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// The battle royale's plains (<see cref="BrNature"/>) drawn as one mesh per prop of the design eighth, shown eight
    /// times by renderers turned and mirrored like the map's symmetries. The props are the same sub-cells in Core
    /// (collision, bullets): this only shares their meshes. Meshed into the ground's chunks they were 6 million
    /// triangles and 700 MB, and the garbage from building them stalled the game for half a second at a time.
    /// </summary>
    public sealed class PropsView : MonoBehaviour
    {
        public int Triangles { get; private set; }

        public void Build(Material material)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BrNature.Plan plan = BrNature.GetPlan();
            int n = BrMap.ChunksAcross * SubGrid.ChunkSize;
            float c = (float)(BrMap.Size / 2);
            const int cs = SubGrid.ChunkSize;
            float sub = (float)SubGrid.SubSize;
            var mesher = new ChunkMesher { AnchorsOnly = true, MarkSculptable = false, FlatFastPath = false };
            var chunks = new List<Int3>();
            var offsets = new List<Vector3>();
            int tris = 0;
            for (int k = 0; k < plan.Props.Count; k++)
            {
                BrNature.Prop p = plan.Props[k];
                int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue, y1 = 0;
                for (int i = p.CellStart; i < p.CellStart + p.CellCount; i++)
                {
                    BrNature.Cell q = plan.Cells[i];
                    x0 = Mathf.Min(x0, q.X); x1 = Mathf.Max(x1, q.X);
                    z0 = Mathf.Min(z0, q.Z); z1 = Mathf.Max(z1, q.Z);
                    y1 = Mathf.Max(y1, q.Y);
                }
                // A scratch grid round the prop with the ground under it (so the faces where it meets the ground come out
                // as they would on the map), one sub-cell of air round it.
                int ox = x0 - 2, oz = z0 - 2;
                int cx = (x1 - ox + 3 + cs - 1) / cs, cz = (z1 - oz + 3 + cs - 1) / cs, cy = 1 + (y1 + 2 + cs - 1) / cs;
                var g = new SubGrid(cx, cy, cz, -cs);
                g.FillBaseGround();
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
                mesh.UploadMeshData(true); // never changes: no copy kept in memory
                tris += (int)mesh.GetIndexCount(0) / 3;
                // Eight renderers: the design eighth's copy and its seven twins.
                foreach (int[] m in BrMap.Sym)
                {
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(transform, false);
                    Place(go.transform, m, c);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on
                    r.receiveShadows = true;
                }
            }
            Triangles = tris * 8;
            Debug.Log($"[SCULPT] Plains: {plan.Props.Count} props meshed once each, shown 8 times ({Triangles / 1000} thousand triangles on the map), in {sw.ElapsedMilliseconds} ms.");
        }

        /// <summary>
        /// The symmetry (u, v) -> (a u + b v, c u + d v) about the map's centre as a transform: a turn about the vertical,
        /// after a mirror in x for the four that flip (Unity turns the faces round for a mirrored renderer).
        /// </summary>
        private static void Place(Transform t, int[] m, float c)
        {
            int a = m[0], b = m[1], cc = m[2], d = m[3];
            bool mirror = a * d - b * cc < 0;
            // Unity's turn by phi about y maps (x, z) to (x cos phi + z sin phi, -x sin phi + z cos phi).
            float cos = mirror ? -a : a, sin = b;
            float phi = Mathf.Atan2(sin, cos) * Mathf.Rad2Deg;
            t.localScale = mirror ? new Vector3(-1, 1, 1) : Vector3.one;
            t.localRotation = Quaternion.Euler(0, phi, 0);
            // The centre stays put: position = centre - M centre.
            t.localPosition = new Vector3(c - (a * c + b * c), 0, c - (cc * c + d * c));
        }
    }
}
