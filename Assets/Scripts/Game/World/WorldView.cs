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
        public const int ReinforceFadeTicks = 180;

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
        private LocalPlayer _localPlayer;
        private NetSession _session;
        private readonly List<Vector3> _actors = new List<Vector3>();
        private readonly List<int> _remoteIds = new List<int>();

        /// <summary>map: optional, for trees (anchors drawn as bark and leaves).</summary>
        public void Init(SubGrid grid, SculptConfig sculpt, MapInfo map = null, GroundCover cover = null)
        {
            _mesher = new ChunkMesher { Cover = cover };
            if (map != null && map.Trees.Count > 0)
            {
                _mesher.Decor = new Dictionary<Int3, byte>();
                foreach (TreeInfo t in map.Trees)
                {
                    foreach (Int3 c in t.Wood) _mesher.Decor[c] = (byte)ChunkMesher.KindWood;
                    foreach (Int3 c in t.Leaves) _mesher.Decor[c] = (byte)ChunkMesher.KindLeaves;
                }
            }
            _grass = new GrassField();
            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Voxel not found; falling back to URP Unlit.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            _material = new Material(shader) { name = "Terrain (runtime)" };
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
                _meshes = new Mesh[grid.ChunkCount];
                _chunkObjects = new GameObject[grid.ChunkCount];
                for (int i = 0; i < grid.ChunkCount; i++)
                {
                    Int3 cc = grid.ChunkCoordFromIndex(i);
                    var go = new GameObject($"Chunk {cc.X},{cc.Y},{cc.Z}");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = grid.ChunkOrigin(cc).ToUnity() * (float)SubGrid.SubSize;
                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = _material;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    _meshes[i] = new Mesh { name = go.name };
                    mf.sharedMesh = _meshes[i];
                    _chunkObjects[i] = go;
                }
            }
            _grid = grid;
            _grass.Bind(grid);
            grid.MarkAllDirty();
            RebuildDirty();
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

        /// <summary>Rebuild every chunk the grid changed since the last call.</summary>
        public int RebuildDirty()
        {
            if (_grid == null) return 0;
            RefreshTints();
            if (!_grid.HasDirtyChunks) return 0;
            _dirty.Clear();
            _grid.DrainDirtyChunks(_dirty);
            foreach (int i in _dirty)
            {
                _mesher.Build(_grid, _grid.ChunkCoordFromIndex(i), _meshes[i]);
                _grass.Rebuild(i);
            }
            return _dirty.Count;
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
                bool tintFading = p.Team != 0 && age >= 0 && age <= _sculpt.Config.TintFadeTicks + 40;
                // Reinforcement: re-mesh while the veins ease out, and once more just after it decays.
                int toEnd = p.ReinforceUntilTick - tick;
                bool veinsFading = p.ReinforceTeam != 0 && toEnd > -40 && toEnd < ReinforceFadeTicks + 30;
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
