using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Distant buildings (map v3 groundwork, Ben's plan: "distant buildings become merged boxes that still show their
    /// holes", "meshes are only built for what's near"). The map is cut into 64 m regions. A region within
    /// <see cref="FarDetailFrom"/> of the camera is drawn by its 8 m tile blocks, as before. Past that it is one mesh:
    /// whole tiles merged into long flat strips along each wall and floor (no hidden sides), and every tile that was
    /// ever hit drawn exactly as up close, so every hole, crack, patch and open door still shows at any distance. Its
    /// outline is the tiles' own boxes: nothing is added or left out that could hide or show a player. A change to a
    /// far region redraws its one mesh within a frame or two (a shared budget, nearest first). Near meshes are built
    /// ahead of you as you move (a ring past the detail distance) and dropped far behind, so memory follows the camera.
    /// </summary>
    public sealed partial class BrushView
    {
        private const float RegionSize = 64f;
        private const int BlocksPerRegion = 8; // RegionSize / TileBlockSize
        /// <summary>Regions nearer than this are drawn in full detail (the picture quality setting will set it: 200-500 m).</summary>
        public static float FarDetailFrom = 200f;
        /// <summary>A near region only turns far again this much further out (no flicker at the line).</summary>
        private const float Hysteresis = 24f;
        /// <summary>Near meshes are built this far past the detail distance before they are needed.</summary>
        private const float PrefetchAhead = 64f;
        /// <summary>Near meshes of regions further than the detail distance plus this are dropped.</summary>
        private const float DropBehind = 160f;
        private const double FarBudgetMs = 1.0;

        private sealed class Region
        {
            public Vector2Int Key;
            public readonly List<int> Tiles = new List<int>();
            public readonly List<Vector2Int> Blocks = new List<Vector2Int>();
            public bool Near;       // drawn by its blocks
            public bool Built;      // its blocks have meshes (may still be dirty)
            public bool FarDirty = true;
            public Mesh FarMesh;
            public GameObject FarGo;
            public MeshRenderer FarRenderer;
        }

        private readonly Dictionary<Vector2Int, Region> _regions = new Dictionary<Vector2Int, Region>();
        private readonly List<Region> _regionList = new List<Region>();
        private readonly Dictionary<Vector2Int, MeshRenderer> _blockRenderers = new Dictionary<Vector2Int, MeshRenderer>();
        private readonly List<Region> _farOrder = new List<Region>();
        private System.Comparison<Region> _regionNearest;
        private readonly MeshBits _farScratch = new MeshBits { Cells = true };

        /// <summary>Renderers drawn now (near blocks and far regions), for the dev HUD and benchmarks.</summary>
        public int TileRenderersOn { get; private set; }
        public int FarRegions { get; private set; }

        private static Vector2Int RegionOfBlock(Vector2Int block) =>
            new Vector2Int(Mathf.FloorToInt(block.x / (float)BlocksPerRegion), Mathf.FloorToInt(block.y / (float)BlocksPerRegion));

        /// <summary>A tile block's look changed: redraw it (when near) and its region's far mesh.</summary>
        private void Dirty(Vector2Int block)
        {
            _dirty.Add(block);
            if (_regions.TryGetValue(RegionOfBlock(block), out Region r)) r.FarDirty = true;
        }

        private void BindRegions()
        {
            foreach (Region r in _regionList)
            {
                if (r.FarGo != null) Destroy(r.FarGo);
                if (r.FarMesh != null) Destroy(r.FarMesh);
            }
            _regions.Clear();
            _regionList.Clear();
            foreach (var kv in _tilesByBlock)
            {
                Vector2Int rk = RegionOfBlock(kv.Key);
                if (!_regions.TryGetValue(rk, out Region r))
                {
                    _regions[rk] = r = new Region { Key = rk };
                    _regionList.Add(r);
                }
                r.Blocks.Add(kv.Key);
                r.Tiles.AddRange(kv.Value);
            }
            foreach (Region r in _regionList) r.Tiles.Sort();
        }

        private float RegionDistance(Region r)
        {
            float x0 = r.Key.x * RegionSize, z0 = r.Key.y * RegionSize;
            float dx = Mathf.Max(0f, Mathf.Max(x0 - _camAt.x, _camAt.x - (x0 + RegionSize)));
            float dz = Mathf.Max(0f, Mathf.Max(z0 - _camAt.z, _camAt.z - (z0 + RegionSize)));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private readonly List<Region> _toBuild = new List<Region>();
        private const double BuildBudgetMs = 2.0;

        /// <summary>
        /// Every frame: which regions are near, near blocks built (nearest first, a shared budget: until a region's
        /// blocks are ready its far mesh stands in, which already shows every hit tile exactly), far meshes redrawn.
        /// </summary>
        private void UpdateLod(bool all)
        {
            Camera cam = Camera.main;
            _camAt = cam != null ? cam.transform.position : _camAt;
            double tick = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            int on = 0, far = 0;
            _toBuild.Clear();
            foreach (Region r in _regionList)
            {
                float d = RegionDistance(r);
                bool want = r.Near ? d < FarDetailFrom + Hysteresis : d < FarDetailFrom;
                if (!r.Built && (want || d < FarDetailFrom + PrefetchAhead)) _toBuild.Add(r);
                else if (r.Built && !want && d > FarDetailFrom + DropBehind) DropBlocks(r);
                Show(r, want && r.Built, all);
                if (r.Near) on += r.Blocks.Count; else far++;
            }
            if (_toBuild.Count > 0)
            {
                _regionNearest ??= (a, b) => RegionDistance(a).CompareTo(RegionDistance(b));
                _toBuild.Sort(_regionNearest);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (Region r in _toBuild)
                {
                    bool urgent = all && RegionDistance(r) < FarDetailFrom; // the first build (loading screen): near ones at once
                    long deadline = start + (long)(BuildBudgetMs * tick);
                    if (!urgent && System.Diagnostics.Stopwatch.GetTimestamp() > deadline) break;
                    BuildBlocks(r, urgent ? 0 : deadline);
                    if (r.Built) Show(r, RegionDistance(r) < FarDetailFrom, false);
                }
            }
            TileRenderersOn = on + far;
            FarRegions = far;

            // Changed near blocks: as before (RebuildDirty drops blocks of regions without near meshes; those are built
            // whole when they come near).
            RebuildDirty(all);

            // Far meshes: every region gets one at the first build, changes after that share a budget, nearest first.
            _farOrder.Clear();
            foreach (Region r in _regionList) if (r.FarDirty && (all || !r.Near)) _farOrder.Add(r);
            if (_farOrder.Count == 0) return;
            _regionNearest ??= (a, b) => RegionDistance(a).CompareTo(RegionDistance(b));
            if (!all) _farOrder.Sort(_regionNearest);
            long farStart = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (Region r in _farOrder)
            {
                if (!all && System.Diagnostics.Stopwatch.GetTimestamp() - farStart > FarBudgetMs * tick) break;
                BuildFar(r);
            }
        }

        /// <summary>Draws a region by its blocks (near) or its far mesh.</summary>
        private void Show(Region r, bool near, bool force)
        {
            if (near == r.Near && !force) return;
            r.Near = near;
            foreach (Vector2Int b in r.Blocks)
                if (_blockRenderers.TryGetValue(b, out MeshRenderer mr) && mr.enabled != near) mr.enabled = near;
            if (r.FarRenderer != null && r.FarRenderer.enabled == near) r.FarRenderer.enabled = !near;
        }

        /// <summary>Builds the region's blocks that have no mesh yet, while the budget lasts (all of them when
        /// <paramref name="deadline"/> is 0). The region counts as built once every block has its mesh.</summary>
        private void BuildBlocks(Region r, long deadline = 0)
        {
            foreach (Vector2Int b in r.Blocks)
            {
                if (_tileMeshes.ContainsKey(b) && !_dirty.Contains(b)) continue;
                if (deadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > deadline) return;
                RebuildTiles(b);
                _dirty.Remove(b);
            }
            r.Built = true;
        }

        private void DropBlocks(Region r)
        {
            foreach (Vector2Int b in r.Blocks)
            {
                if (_tileMeshes.TryGetValue(b, out Mesh mesh))
                {
                    _tileMeshes.Remove(b);
                    Destroy(mesh);
                }
                if (_blockRenderers.TryGetValue(b, out MeshRenderer mr))
                {
                    _blockRenderers.Remove(b);
                    _blocks.Remove(mr.gameObject);
                    Destroy(mr.gameObject);
                }
                _dirty.Remove(b);
            }
            r.Built = false;
        }

        /// <summary>Does this block have a near mesh (its region is drawn by its blocks, or about to be)?</summary>
        private bool BlockBuilt(Vector2Int block) => _tileMeshes.ContainsKey(block);

        // ---- the far mesh ----

        private struct Strip
        {
            public int Tile;
            public float Lo, Hi; // along a1
        }

        private readonly Dictionary<(int, int, int, int, int, int, int), List<Strip>> _strips = new Dictionary<(int, int, int, int, int, int, int), List<Strip>>();
        private readonly List<List<Strip>> _stripLists = new List<List<Strip>>();

        private static int Mm(float v) => Mathf.RoundToInt(v * 1000f);

        private void BuildFar(Region r)
        {
            r.FarDirty = false;
            MeshBits m = _farScratch;
            m.Clear();
            foreach (List<Strip> l in _stripLists) l.Clear();
            _strips.Clear();
            int used = 0;
            foreach (int id in r.Tiles)
            {
                WallTile t = _tiles[id];
                if (_tiles.HasCells(id) && t.State != TileState.Broken && Untouched(id))
                {
                    // A whole tile, and so is everything round it: one strip piece.
                    Brush b = _tiles.ShapeOf(id);
                    Vector3 lo = b.Bounds.Min.ToUnity(), hi = b.Bounds.Max.ToUnity();
                    int a1 = b.CellA1, a2 = b.CellA2, n = 3 - a1 - a2;
                    // Top and bottom (along a2) faces show only where nothing carries on past the tile.
                    int top = _tiles.EdgeNeighbour(id, 2) < 0 && _tiles.CellNeighbour(id, NC * (NC - 1), 2, out _) != -1 ? 1 : 0;
                    int bottom = _tiles.EdgeNeighbour(id, 3) < 0 && _tiles.CellNeighbour(id, 0, 3, out _) != -1 ? 1 : 0;
                    var key = (n, a1, Mm(lo[n]), Mm(hi[n]), Mm(lo[a2]), Mm(hi[a2]), (t.Kind == TileKind.Floor ? 1 : 0) | top << 1 | bottom << 2);
                    if (!_strips.TryGetValue(key, out List<Strip> list))
                    {
                        if (used == _stripLists.Count) _stripLists.Add(new List<Strip>());
                        list = _stripLists[used++];
                        _strips[key] = list;
                    }
                    list.Add(new Strip { Tile = id, Lo = lo[a1], Hi = hi[a1] });
                    continue;
                }
                AddTile(m, id, t); // anything ever hit (and stairs, frame, doors): exactly as up close
            }
            foreach (var kv in _strips) EmitStrips(m, kv.Key, kv.Value);
            _surface = Greybox;

            if (r.FarMesh == null)
            {
                r.FarMesh = new Mesh { name = $"Far tiles {r.Key}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                r.FarMesh.MarkDynamic();
                r.FarGo = new GameObject(r.FarMesh.name);
                r.FarGo.transform.SetParent(transform, false);
                r.FarGo.AddComponent<MeshFilter>().sharedMesh = r.FarMesh;
                r.FarRenderer = r.FarGo.AddComponent<MeshRenderer>();
                r.FarRenderer.sharedMaterial = _mat;
                r.FarRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.FarRenderer.receiveShadows = true;
                r.FarRenderer.enabled = !r.Near;
            }
            Mesh mesh = r.FarMesh;
            mesh.Clear();
            mesh.SetVertices(m.V);
            mesh.SetNormals(m.N);
            mesh.SetColors(m.C);
            mesh.SetUVs(1, m.U1);
            mesh.SetUVs(2, m.U2);
            mesh.SetTriangles(m.T, 0);
            mesh.RecalculateBounds();
        }

        private static Vector3 Unit(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;

        /// <summary>Whole tiles in one row of one wall (or floor) merged where they touch: the two big faces, the ends
        /// where the wall stops, and the top or bottom where nothing carries on.</summary>
        private void EmitStrips(MeshBits m, (int n, int a1, int nLo, int nHi, int a2Lo, int a2Hi, int flags) k, List<Strip> list)
        {
            list.Sort((x, y) => x.Lo.CompareTo(y.Lo));
            bool floor = (k.flags & 1) != 0;
            Color col = floor ? Clay : Brick;
            _surface = floor ? Rough : BrickSurface;
            _uv1 = _uv2 = Vector2.zero;
            int a2 = 3 - k.n - k.a1;
            Vector3 un = Unit(k.n), u1 = Unit(k.a1), u2 = Unit(a2);
            float n0 = k.nLo / 1000f, n1 = k.nHi / 1000f, b0 = k.a2Lo / 1000f, b1 = k.a2Hi / 1000f;
            int i = 0;
            while (i < list.Count)
            {
                int j = i;
                while (j + 1 < list.Count && Mathf.Abs(list[j + 1].Lo - list[j].Hi) < 1e-3f) j++;
                float lo = list[i].Lo, hi = list[j].Hi;
                Vector3 P(float a, float b, float c) => u1 * a + u2 * b + un * c;
                // Big faces (both sides).
                Face4(m, P(lo, b0, n1), P(hi, b0, n1), P(hi, b1, n1), P(lo, b1, n1), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, col, un);
                Face4(m, P(lo, b0, n0), P(hi, b0, n0), P(hi, b1, n0), P(lo, b1, n0), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, col, -un);
                // Ends: where the run stops and no tile of the same grid carries on (an untouched neighbour that is
                // in another strip, or a hit one drawn exactly, covers its own side).
                Color side = col * 0.92f;
                if (_tiles.EdgeNeighbour(list[j].Tile, 0) < 0)
                    Face4(m, P(hi, b0, n0), P(hi, b1, n0), P(hi, b1, n1), P(hi, b0, n1), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, side, u1);
                if (_tiles.EdgeNeighbour(list[i].Tile, 1) < 0)
                    Face4(m, P(lo, b0, n0), P(lo, b1, n0), P(lo, b1, n1), P(lo, b0, n1), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, side, -u1);
                if ((k.flags & 2) != 0)
                    Face4(m, P(lo, b1, n0), P(hi, b1, n0), P(hi, b1, n1), P(lo, b1, n1), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, side, u2);
                if ((k.flags & 4) != 0)
                    Face4(m, P(lo, b0, n0), P(hi, b0, n0), P(hi, b0, n1), P(lo, b0, n1), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, side, -u2);
                i = j + 1;
            }
        }
    }
}
