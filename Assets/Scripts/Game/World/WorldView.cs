using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>Owns one GameObject + mesh per chunk and rebuilds only chunks the grid marks dirty.</summary>
    public sealed class WorldView : MonoBehaviour
    {
        private VoxelGrid _grid;
        private ChunkMesher _mesher;
        private Material _material;
        private Mesh[] _meshes;
        private readonly List<int> _dirty = new List<int>();

        public void Init(VoxelGrid grid, SculptConfig sculpt)
        {
            _grid = grid;
            _mesher = new ChunkMesher(sculpt);

            Shader shader = Shader.Find("SCULPT/Voxel");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Voxel not found; falling back to URP Unlit.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            _material = new Material(shader) { name = "Voxel (runtime)" };

            _meshes = new Mesh[grid.ChunkCount];
            for (int i = 0; i < grid.ChunkCount; i++)
            {
                Int3 cc = grid.ChunkCoordFromIndex(i);
                var go = new GameObject($"Chunk {cc.X},{cc.Y},{cc.Z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = grid.ChunkOrigin(cc).ToUnity() * (float)VoxelGrid.BlockSize;
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _material;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _meshes[i] = new Mesh { name = go.name };
                mf.sharedMesh = _meshes[i];
            }
            grid.MarkAllDirty();
            RebuildDirty();
        }

        /// <summary>Rebuild every chunk the grid changed since the last call.</summary>
        public int RebuildDirty()
        {
            if (_grid == null || !_grid.HasDirtyChunks) return 0;
            _dirty.Clear();
            _grid.DrainDirtyChunks(_dirty);
            foreach (int i in _dirty) _mesher.Build(_grid, _grid.ChunkCoordFromIndex(i), _meshes[i]);
            return _dirty.Count;
        }

        private void OnDestroy()
        {
            if (_meshes != null) foreach (var m in _meshes) if (m != null) Destroy(m);
            if (_material != null) Destroy(_material);
        }
    }
}
