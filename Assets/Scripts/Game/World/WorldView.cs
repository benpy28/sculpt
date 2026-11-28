using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Owns one GameObject + mesh per sub-cell chunk and rebuilds only chunks the grid marks dirty. Also drives the
    /// local sculpt overlay on the terrain material (purple edge glow, ripple, range dimming) and the fading team tint.
    /// Also owns the meadow (<see cref="GrassField"/>, re-planted with each rebuilt chunk) and the shared wind.
    /// </summary>
    public sealed class WorldView : MonoBehaviour
    {
        private SubGrid _grid;
        private ChunkMesher _mesher;
        private Material _material;
        private Mesh[] _meshes;
        private GameObject[] _chunkObjects;
        // Chunks are drawn in groups of G x G columns (all heights): flat maps have tens of thousands of nearly empty
        // ground chunks, and a renderer each cost the render thread more than drawing them. A change re-meshes its
        // group. Maps with sculptable detail keep one chunk per group (an edit re-meshes only its own chunk).
        private int _group = 1, _groupsX, _groupsZ;
        private readonly HashSet<int> _dirtyGroups = new HashSet<int>();
        private readonly List<Int3> _groupChunks = new List<Int3>();
        private readonly List<Vector3> _groupOffsets = new List<Vector3>();
        private readonly List<int> _dirty = new List<int>();
        private SculptClient _sculpt;
        private float _tintTimer;
        private float _mode;
        private float _rippleStart = -10f;
        private bool _wasInMode;

        private static readonly int IdMode = Shader.PropertyToID("_SculptMode");
        private static readonly int IdEye = Shader.PropertyToID("_SculptEye");
        private static readonly int IdRange = Shader.PropertyToID("_SculptRange");
        private static readonly int IdRipple = Shader.PropertyToID("_SculptRipple");

        public Material Material => _material;
        public SubGrid Grid => _grid;

        /// <summary>Reinforced veins ease out over the last 3 s before the reinforcement decays.</summary>
        public const int ReinforceFadeTicks = 180 * SimTime.Per60;

        private readonly HashSet<Int3> _hidden = new HashSet<Int3>();
        private static readonly int IdRippleGlow = Shader.PropertyToID("_SculptRippleGlow");

        /// <summary>Stop drawing these sub-cells (a landing being animated separately). Rebuilds their chunks now.</summary>
        public void Hide(IList<Int3> cells)
        {
            if (_grid == null || cells == null) return;
            foreach (var c in cells) { _hidden.Add(c); _grid.MarkDirty(c); }
            RebuildDirty();
        }

        /// <summary>Draw these sub-cells again.</summary>
        public void Unhide(IList<Int3> cells)
        {
            if (_grid == null || cells == null) return;
            foreach (var c in cells) if (_hidden.Remove(c)) _grid.MarkDirty(c);
            RebuildDirty();
        }

        private GrassField _grass;
        private bool _fixedGround;

        /// <summary>The terrain material (props drawn apart from the chunks share it).</summary>
        public Material TerrainMaterial => _material;
        private LocalPlayer _localPlayer;
        private NetSession _session;
        private readonly List<Vector3> _actors = new List<Vector3>();
        private readonly List<int> _remoteIds = new List<int>();

        /// <summary>map: optional, for trees (anchors drawn as bark and leaves).</summary>
        /// <param name="decorOf">Optional: how props on the ground are drawn (the battle royale plains), see
        /// <see cref="ChunkMesher.DecorOf"/>. With it the ground is never edited in play, so chunks are drawn in groups
        /// even though the props give them their own sub-cells.</param>
        public void Init(SubGrid grid, SculptConfig sculpt, MapInfo map = null, GroundCover cover = null, System.Func<Int3, byte> decorOf = null)
        {
            _mesher = new ChunkMesher { Cover = cover, DecorOf = decorOf, SkipAnchors = decorOf != null }; // props: PropsView
            _fixedGround = decorOf != null;
            if (map != null && map.Trees.Count > 0)
            {
                _mesher.Decor = new Dictionary<Int3, byte>();
                foreach (TreeInfo t in map.Trees)
                {
                    foreach (Int3 c in t.Wood) _mesher.Decor[c] = (byte)ChunkMesher.KindWood;
                    foreach (Int3 c in t.Leaves) _mesher.Decor[c] = (byte)ChunkMesher.KindLeaves;
                }
            }
            // Meadow grass belongs to the nature maps (the ones that come with trees and map info). On the greybox maps
            // it would be planted on every bit of bare ground, building interiors included: a quarter of a million
            // tufts handed to the graphics card every frame (0.4 ms of a 1.8 ms frame), and never visible in a built
            // game anyway (see GrassField). Null: no grass.
            _grass = map != null ? new GrassField() : null;
            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Voxel not found; falling back to URP Unlit.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            _material = new Material(shader) { name = "Terrain (runtime)" };
            _material.SetFloat("_Scenery", 1f); // takes the picture settings (shadows, AO, lighting, atmosphere)
            _material.SetFloat(IdRange, (float)sculpt.RangeMeters);
        }

        /// <summary>Switches to another grid (a new client has its own copy of the map).</summary>
        public void Bind(SubGrid grid, SculptClient sculpt)
        {
            _sculpt = sculpt;
            _mesher.PieceTint = sculpt == null ? (System.Func<ushort, (int, float)>)null : id => sculpt.TintOf(id, sculpt.Tick);
            _mesher.PieceReinforce = sculpt == null ? (System.Func<ushort, float>)null : id => sculpt.ReinforceOf(id, sculpt.Tick, ReinforceFadeTicks);
            _mesher.Hidden = _hidden;
            _hidden.Clear();
            if (_grid == null || grid.ChunkCount != _grid.ChunkCount || grid.ChunksX != _grid.ChunksX)
            {
                if (_chunkObjects != null) foreach (var go in _chunkObjects) Destroy(go);
                if (_meshes != null) foreach (var m in _meshes) Destroy(m);
                // Ground that never changes in play (the battle royale's) is drawn in big squares of 16 x 16 chunks (77 m): it
                // is flat merged quads, and 4 x 4 groups were 2,300 renderers on the 921 m map for a plane.
                _group = _fixedGround ? 16 : grid.AllocatedChunks == 0 ? 4 : 1;
                if (DrawsNothing) _group = int.MaxValue / 4;
                _groupsX = (grid.ChunksX + _group - 1) / _group;
                _groupsZ = (grid.ChunksZ + _group - 1) / _group;
                int groups = _groupsX * _groupsZ * (_group == 1 ? grid.ChunksY : 1);
                if (_group == 1) groups = grid.ChunkCount;
                if (DrawsNothing) groups = 0;
                _meshes = new Mesh[groups];
                _chunkObjects = new GameObject[groups];
                for (int i = 0; i < groups; i++)
                {
                    Int3 cc = _group == 1 ? grid.ChunkCoordFromIndex(i) : new Int3((i % _groupsX) * _group, 0, (i / _groupsX) * _group);
                    var go = new GameObject(_group == 1 ? $"Chunk {cc.X},{cc.Y},{cc.Z}" : $"Chunks {cc.X},{cc.Z} +{_group}");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = grid.ChunkOrigin(cc).ToUnity() * (float)SubGrid.SubSize;
                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = _material;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // scenery casts sun shadows when that setting is on (players never do)
                    mr.receiveShadows = true;
                    _meshes[i] = new Mesh { name = go.name };
                    mf.sharedMesh = _meshes[i];
                    _chunkObjects[i] = go;
                }
            }
            // A new client on the same map (going offline at load, say) gets a fresh copy of the same cells: the meshes
            // already show it, so only re-mesh when something differs (saved ~0.5 s of every load).
            bool same = _grid != null && grid.SameCells(_grid);
            _grid = grid;
            _grass?.Bind(grid);
            if (same) return;
            grid.MarkAllDirty();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildDirty();
            long tris = 0, verts = 0;
            if (_meshes != null) foreach (Mesh m in _meshes) if (m != null) { verts += m.vertexCount; tris += m.GetIndexCount(0) / 3; }
            Debug.Log($"[SCULPT] Ground meshed: {grid.ChunkCount} chunks in {sw.ElapsedMilliseconds} ms, {tris / 1000} thousand triangles, {verts / 1000} thousand vertices (about {verts * 64 / (1024 * 1024)} MB).");
            if (Debug.isDebugBuild && System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a == "-meshcheck"))
            {
                int bad = 0, done = 0;
                for (int i = 0; i < grid.ChunkCount && done < 400; i += 7)
                {
                    Int3 cc = grid.ChunkCoordFromIndex(i);
                    string p = _mesher.CheckFlat(grid, cc);
                    done++;
                    if (p != null) { bad++; if (bad <= 5) Debug.LogWarning($"[SCULPT] Mesh check: chunk {cc.X},{cc.Y},{cc.Z}: {p}"); }
                }
                Debug.Log($"[SCULPT] Mesh check: {done} chunks compared, {bad} differ.");
            }
        }

        /// <summary>Players whose feet flatten the grass (the local player and everyone drawn remotely).</summary>
        public void TrackPlayers(LocalPlayer local, NetSession session)
        {
            _localPlayer = local;
            _session = session;
        }

        private void LateUpdate()
        {
            Wind.Update(Time.time);
            if (_grass == null) return;
            _actors.Clear();
            if (_localPlayer != null) _actors.Add(_localPlayer.RenderFeet);
            if (_session != null)
            {
                NetClient net = _session.Client;
                net.GetRemoteIds(_remoteIds);
                foreach (int id in _remoteIds)
                    if (net.TryGetRemote(id, out RemoteView v) && !v.Dead) _actors.Add(v.Position.ToUnity());
            }
            _grass.SetActors(_actors);
            Camera cam = Camera.main;
            if (cam != null) _grass.Draw(cam.transform.position);
        }

        /// <summary>
        /// The ground is drawn elsewhere and the grid holds nothing to draw here (the battle royale since map v3: the
        /// height map is the ground, <see cref="HeightFieldView"/>, and the props are <see cref="PropsView"/>'s). Set
        /// before <see cref="Bind"/>.
        /// </summary>
        public bool DrawsNothing;

        /// <summary>Rebuild every chunk the grid changed since the last call.</summary>
        public int RebuildDirty()
        {
            if (_grid == null) return 0;
            if (DrawsNothing) { _dirty.Clear(); _grid.DrainDirtyChunks(_dirty); return 0; }
            RefreshTints();
            if (!_grid.HasDirtyChunks) return 0;
            _dirty.Clear();
            _grid.DrainDirtyChunks(_dirty);
            if (_group == 1)
            {
                foreach (int i in _dirty)
                {
                    _mesher.Build(_grid, _grid.ChunkCoordFromIndex(i), _meshes[i]);
                    _grass?.Rebuild(i);
                }
                return _dirty.Count;
            }
            _dirtyGroups.Clear();
            foreach (int i in _dirty)
            {
                Int3 cc = _grid.ChunkCoordFromIndex(i);
                _dirtyGroups.Add((cc.Z / _group) * _groupsX + cc.X / _group);
                _grass?.Rebuild(i);
            }
            float sub = (float)SubGrid.SubSize;
            if (_dirtyGroups.Count >= 64)
            {
                // A whole map at load (or a reload): fill the groups on every core, upload here.
                BuildGroupsInParallel(sub);
                return _dirty.Count;
            }
            foreach (int gi in _dirtyGroups)
            {
                Int3 g0 = new Int3((gi % _groupsX) * _group, 0, (gi / _groupsX) * _group);
                Vector3 origin = _grid.ChunkOrigin(g0).ToUnity() * sub;
                _groupChunks.Clear();
                _groupOffsets.Clear();
                for (int cy = 0; cy < _grid.ChunksY; cy++)
                    for (int dz = 0; dz < _group; dz++)
                        for (int dx = 0; dx < _group; dx++)
                        {
                            var cc = new Int3(g0.X + dx, cy, g0.Z + dz);
                            if (cc.X >= _grid.ChunksX || cc.Z >= _grid.ChunksZ) continue;
                            _groupChunks.Add(cc);
                            _groupOffsets.Add(_grid.ChunkOrigin(cc).ToUnity() * sub - origin);
                        }
                _mesher.BuildGroup(_grid, _groupChunks, _groupOffsets, _meshes[gi]);
            }
            return _dirty.Count;
        }

        private void BuildGroupsInParallel(float sub)
        {
            var groups = new List<int>(_dirtyGroups);
            int workers = Mathf.Max(1, System.Environment.ProcessorCount - 1);
            var twins = new ChunkMesher[workers];
            for (int w = 0; w < workers; w++) twins[w] = _mesher.Twin();
            const int Batch = 256;
            var filled = new ChunkMesher.Filled[Batch];
            for (int start = 0; start < groups.Count; start += Batch)
            {
                int count = Mathf.Min(Batch, groups.Count - start);
                int next = -1;
                var tasks = new System.Threading.Tasks.Task[workers];
                for (int w = 0; w < workers; w++)
                {
                    ChunkMesher m = twins[w];
                    tasks[w] = System.Threading.Tasks.Task.Run(() =>
                    {
                        var chunks = new List<Int3>();
                        var offsets = new List<Vector3>();
                        int k;
                        while ((k = System.Threading.Interlocked.Increment(ref next)) < count)
                        {
                            int gi = groups[start + k];
                            Int3 g0 = new Int3((gi % _groupsX) * _group, 0, (gi / _groupsX) * _group);
                            Vector3 origin = _grid.ChunkOrigin(g0).ToUnity() * sub;
                            chunks.Clear();
                            offsets.Clear();
                            for (int cy = 0; cy < _grid.ChunksY; cy++)
                                for (int dz = 0; dz < _group; dz++)
                                    for (int dx = 0; dx < _group; dx++)
                                    {
                                        var cc = new Int3(g0.X + dx, cy, g0.Z + dz);
                                        if (cc.X >= _grid.ChunksX || cc.Z >= _grid.ChunksZ) continue;
                                        chunks.Add(cc);
                                        offsets.Add(_grid.ChunkOrigin(cc).ToUnity() * sub - origin);
                                    }
                            filled[k] = m.FillGroup(_grid, chunks, offsets);
                        }
                    });
                }
                System.Threading.Tasks.Task.WaitAll(tasks);
                for (int k = 0; k < count; k++)
                {
                    ChunkMesher.Upload(filled[k], _meshes[groups[start + k]]);
                    filled[k] = null;
                }
            }
        }

        /// <summary>The team tint is baked into vertex colours; re-mesh tinted pieces a few times a second while it fades.</summary>
        private void RefreshTints()
        {
            if (_sculpt == null) return;
            _tintTimer += Time.unscaledDeltaTime;
            if (_tintTimer < 0.5f) return;
            _tintTimer = 0;
            int tick = _sculpt.Tick;
            foreach (PieceInfo p in _sculpt.Mirror.Pieces.All)
            {
                if (p.Cells.Count == 0) continue;
                int age = tick - p.TintTick;
                bool tintFading = p.Team != 0 && age >= 0 && age <= _sculpt.Config.TintFadeTicks + 40 * SimTime.Per60;
                // Reinforcement: re-mesh while the veins ease out, and once more just after it decays.
                int toEnd = p.ReinforceUntilTick - tick;
                bool veinsFading = p.ReinforceTeam != 0 && toEnd > -40 * SimTime.Per60 && toEnd < ReinforceFadeTicks + 30 * SimTime.Per60;
                if (!tintFading && !veinsFading) continue;
                // One sub-cell per 8 is plenty to touch every chunk the piece is in.
                for (int k = 0; k < p.Cells.Count; k += 8) _grid.MarkDirty(p.Cells[k]);
                _grid.MarkDirty(p.Cells[p.Cells.Count - 1]);
            }
        }

        /// <summary>Per frame: the local player's eye and whether sculpt mode is on (ripple on entering).</summary>
        public void SetSculptView(Vector3 eye, bool inSculptMode, float range)
        {
            if (_material == null) return;
            if (inSculptMode && !_wasInMode) _rippleStart = Time.unscaledTime;
            _wasInMode = inSculptMode;
            _mode = Mathf.MoveTowards(_mode, inSculptMode ? 1f : 0f, Time.unscaledDeltaTime / 0.08f);
            float t = (Time.unscaledTime - _rippleStart) / 0.1f; // spreads to the range edge over ~0.1 s
            float ripple = t < 0 || t > 1.6f ? 100f : t * (range + 0.5f);
            // Afterglow: sculptables the ripple has passed stay lit, fading over ~0.35 s.
            float glow = t < 0 ? 0f : Mathf.Clamp01(1f - (t - 1f) / 3.5f);
            _material.SetFloat(IdRippleGlow, glow * _mode);
            _material.SetFloat(IdMode, _mode);
            _material.SetVector(IdEye, eye);
            _material.SetFloat(IdRange, range);
            _material.SetFloat(IdRipple, ripple);
        }

        private void OnDestroy()
        {
            if (_meshes != null) foreach (var m in _meshes) if (m != null) Destroy(m);
            if (_material != null) Destroy(_material);
        }
    }
}
