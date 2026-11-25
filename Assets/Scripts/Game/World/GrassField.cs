using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpt.Game.World
{
    /// <summary>
    /// One shared wind for grass, leaves and the ambient wind sound: a slowly turning direction and soft gusts.
    /// Cosmetic only (nothing in the simulation reads it).
    /// </summary>
    public static class Wind
    {
        /// <summary>Gust strength 0..1 right now.</summary>
        public static float Gust { get; private set; } = 0.4f;
        public static Vector2 Direction { get; private set; } = new Vector2(0.8f, 0.6f);
        private static readonly int IdWind = Shader.PropertyToID("_SculptWind");

        public static void Update(float time)
        {
            float a = 0.6f + Mathf.Sin(time * 0.013f) * 0.5f;
            Direction = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float g = Mathf.PerlinNoise(time * 0.11f, 3.7f) * 0.7f + Mathf.PerlinNoise(time * 0.43f, 9.1f) * 0.3f;
            Gust = Mathf.Clamp01((g - 0.25f) * 1.6f);
            Shader.SetGlobalVector(IdWind, new Vector4(Direction.x, Direction.y, Gust, time));
        }
    }

    /// <summary>
    /// Meadow grass and flowers: GPU-instanced tufts on the exposed tops of natural ground, one list per terrain
    /// chunk, rebuilt when the chunk changes. Visual only: blades are short (under 0.3 m), squash flat within about a
    /// metre of any player, shrink with distance, and never grow on anchors, pieces or ground that has been sculpted
    /// (a column whose contents changed since the map loaded stays bare for the rest of the match), so grass never
    /// hides anyone and never suggests rock that isn't there.
    ///
    /// Known gap (2026-11-25): in a built game nothing is drawn. The materials are made at run time, so Unity sees no
    /// material asset using the instanced variant of SCULPT/Grass and strips it from the build (Project Settings >
    /// Graphics > Instancing Variants = Strip Unused); the Editor compiles it on demand, so grass shows there. The
    /// per-frame cost is also high: every tuft's matrix is copied to the graphics card each frame. Before a nature map
    /// ships, draw from a buffer filled once per chunk and read by instance id (no instancing variant needed).
    /// </summary>
    public sealed class GrassField
    {
        public const int MaxActors = 16;
        private const int MaxBatch = 1023;
        public float DrawDistance = 46f;

        private readonly Mesh _tuft, _flower;
        private readonly Material _grassMat, _flowerMat;
        private SubGrid _grid;
        private List<Matrix4x4>[] _tufts, _flowers;
        private Bounds[] _bounds;
        private uint[] _columnHash;
        private bool[] _scarred;
        private int _sizeX, _sizeZ;
        private readonly Vector4[] _actors = new Vector4[MaxActors];
        private static readonly int IdActors = Shader.PropertyToID("_GrassActors");
        private static readonly int IdActorCount = Shader.PropertyToID("_GrassActorCount");
        private static readonly int IdFade = Shader.PropertyToID("_Fade");

        public GrassField()
        {
            Shader shader = Shader.Find("SCULPT/Grass");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Grass not found; no grass.");
                return;
            }
            _grassMat = new Material(shader) { name = "Grass (runtime)", enableInstancing = true };
            _flowerMat = new Material(shader) { name = "Flowers (runtime)", enableInstancing = true };
            _flowerMat.SetFloat("_Flower", 1f);
            _tuft = BuildTuft(false);
            _flower = BuildTuft(true);
            Shader.SetGlobalVectorArray(IdActors, _actors);
        }

        public void Bind(SubGrid g)
        {
            _grid = g;
            int chunks = g.ChunkCount;
            _tufts = new List<Matrix4x4>[chunks];
            _flowers = new List<Matrix4x4>[chunks];
            _bounds = new Bounds[chunks];
            _sizeX = g.SizeX;
            _sizeZ = g.SizeZ;
            _columnHash = new uint[_sizeX * _sizeZ];
            _scarred = new bool[_sizeX * _sizeZ];
            for (int z = 0; z < _sizeZ; z++)
                for (int x = 0; x < _sizeX; x++)
                    _columnHash[z * _sizeX + x] = ColumnHash(x, z);
        }

        /// <summary>Re-plants one chunk (call after the chunk's terrain mesh was rebuilt).</summary>
        public void Rebuild(int chunkIndex)
        {
            if (_grid == null || _grassMat == null) return;
            var tufts = _tufts[chunkIndex] ?? (_tufts[chunkIndex] = new List<Matrix4x4>());
            var flowers = _flowers[chunkIndex] ?? (_flowers[chunkIndex] = new List<Matrix4x4>());
            tufts.Clear();
            flowers.Clear();
            Int3 o = _grid.ChunkOrigin(_grid.ChunkCoordFromIndex(chunkIndex));
            const int n = SubGrid.ChunkSize;
            float s = (float)SubGrid.SubSize;
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int cx = o.X + x, cz = o.Z + z;
                    if (cx >= _sizeX || cz >= _sizeZ) continue;
                    int col = cz * _sizeX + cx;
                    if (!_scarred[col] && ColumnHash(cx, cz) != _columnHash[col]) _scarred[col] = true; // sculpted: stays bare
                    if (_scarred[col]) continue;
                    for (int y = 0; y < n; y++)
                    {
                        int wy = o.Y + y;
                        if (_grid.Get(cx, wy, cz) != SubId.Natural) continue;
                        if (_grid.Get(cx, wy + 1, cz) != SubId.Empty || _grid.Get(cx, wy + 2, cz) != SubId.Empty) continue;
                        uint h = Hash(cx, wy, cz);
                        if (h % 100 >= 90) continue;
                        // Jitter inside the sub-cell, kept off convex edges (the chamfer starts 0.15 m in).
                        float jx = (((h >> 8) & 255) / 255f - 0.5f) * 0.22f;
                        float jz = (((h >> 16) & 255) / 255f - 0.5f) * 0.22f;
                        if (_grid.Get(cx + 1, wy, cz) == SubId.Empty) jx = Mathf.Min(jx, -0.02f);
                        if (_grid.Get(cx - 1, wy, cz) == SubId.Empty) jx = Mathf.Max(jx, 0.02f);
                        if (_grid.Get(cx, wy, cz + 1) == SubId.Empty) jz = Mathf.Min(jz, -0.02f);
                        if (_grid.Get(cx, wy, cz - 1) == SubId.Empty) jz = Mathf.Max(jz, 0.02f);
                        var pos = new Vector3((cx + 0.5f) * s + jx, (wy + 1) * s, (cz + 0.5f) * s + jz);
                        float yaw = (h >> 22) * (360f / 1024f);
                        uint h2 = Hash(cz, wy, cx);
                        float scale = 0.8f + (h2 & 255) / 255f * 0.5f;
                        var m = Matrix4x4.TRS(pos, Quaternion.Euler(0, yaw, 0), new Vector3(scale, scale, scale));
                        if ((h2 >> 8) % 100 < 3) flowers.Add(m);
                        else tufts.Add(m);
                    }
                }
            Vector3 min = o.ToUnity() * s;
            _bounds[chunkIndex] = new Bounds(min + new Vector3(n, n, n) * (s * 0.5f), new Vector3(n * s + 1f, n * s + 1f, n * s + 1f));
        }

        /// <summary>Feet positions of players (local and remote) that flatten the grass around them.</summary>
        public void SetActors(List<Vector3> feet)
        {
            int count = Mathf.Min(feet.Count, MaxActors);
            for (int i = 0; i < count; i++) _actors[i] = new Vector4(feet[i].x, feet[i].y, feet[i].z, 1f);
            for (int i = count; i < MaxActors; i++) _actors[i] = new Vector4(0, -10000, 0, 0);
            Shader.SetGlobalVectorArray(IdActors, _actors);
            Shader.SetGlobalFloat(IdActorCount, count);
        }

        public void Draw(Vector3 camera)
        {
            if (_grid == null || _grassMat == null) return;
            _grassMat.SetFloat(IdFade, DrawDistance);
            _flowerMat.SetFloat(IdFade, DrawDistance);
            float reach = DrawDistance + 6f;
            for (int i = 0; i < _tufts.Length; i++)
            {
                var t = _tufts[i];
                var f = _flowers[i];
                if ((t == null || t.Count == 0) && (f == null || f.Count == 0)) continue;
                Bounds b = _bounds[i];
                if (b.SqrDistance(camera) > reach * reach) continue;
                Submit(_grassMat, _tuft, t, b);
                Submit(_flowerMat, _flower, f, b);
            }
        }

        private static void Submit(Material mat, Mesh mesh, List<Matrix4x4> list, Bounds b)
        {
            if (list == null || list.Count == 0) return;
            var rp = new RenderParams(mat)
            {
                worldBounds = b,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            for (int start = 0; start < list.Count; start += MaxBatch)
                Graphics.RenderMeshInstanced(rp, mesh, 0, list, Mathf.Min(MaxBatch, list.Count - start), start);
        }

        private uint ColumnHash(int x, int z)
        {
            uint h = 2166136261u;
            for (int y = -12; y < _grid.MaxYExclusive; y++) h = (h ^ _grid.Get(x, y, z)) * 16777619u;
            return h;
        }

        private static uint Hash(int x, int y, int z)
        {
            uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)z * 0xC2B2AE3Du;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }

        /// <summary>
        /// A tuft of tapered blades (uv.x = 0 blade, uv.y = height 0..1). The flower variant adds a stem (uv.x = 1) and a
        /// small five-petal head (uv.x = 2) whose colour the shader picks per instance (never purple).
        /// </summary>
        private static Mesh BuildTuft(bool flower)
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();
            var rnd = new System.Random(flower ? 7 : 3);
            int blades = flower ? 3 : 6;
            for (int b = 0; b < blades; b++)
            {
                float a = (b / (float)blades + (float)rnd.NextDouble() * 0.12f) * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var side = new Vector3(-dir.z, 0, dir.x);
                Vector3 root = dir * (0.02f + (float)rnd.NextDouble() * 0.06f);
                float hgt = flower ? 0.12f + (float)rnd.NextDouble() * 0.06f : 0.15f + (float)rnd.NextDouble() * 0.13f;
                float w = 0.016f + (float)rnd.NextDouble() * 0.008f;
                float lean = 0.03f + (float)rnd.NextDouble() * 0.06f;
                int i0 = v.Count;
                v.Add(root - side * w); uv.Add(new Vector2(0, 0));
                v.Add(root + side * w); uv.Add(new Vector2(0, 0));
                v.Add(root + dir * lean * 0.35f + Vector3.up * hgt * 0.55f - side * w * 0.7f); uv.Add(new Vector2(0, 0.55f));
                v.Add(root + dir * lean * 0.35f + Vector3.up * hgt * 0.55f + side * w * 0.7f); uv.Add(new Vector2(0, 0.55f));
                v.Add(root + dir * lean + Vector3.up * hgt); uv.Add(new Vector2(0, 1));
                tri.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3, i0 + 2, i0 + 4, i0 + 3 });
            }
            if (flower)
            {
                const float stemH = 0.27f, sw = 0.006f;
                for (int k = 0; k < 2; k++)
                {
                    Vector3 side = k == 0 ? Vector3.right * sw : Vector3.forward * sw;
                    int i0 = v.Count;
                    v.Add(-side); uv.Add(new Vector2(1, 0));
                    v.Add(side); uv.Add(new Vector2(1, 0));
                    v.Add(new Vector3(0.01f, stemH, 0) - side); uv.Add(new Vector2(1, 1));
                    v.Add(new Vector3(0.01f, stemH, 0) + side); uv.Add(new Vector2(1, 1));
                    tri.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3 });
                }
                // Head: a centre and five petal tips, slightly cupped.
                var centre = new Vector3(0.01f, stemH + 0.008f, 0);
                int c0 = v.Count;
                v.Add(centre); uv.Add(new Vector2(2, 1));
                const int petals = 10;
                for (int p = 0; p < petals; p++)
                {
                    float a = p / (float)petals * Mathf.PI * 2f;
                    float r = p % 2 == 0 ? 0.042f : 0.02f;
                    v.Add(centre + new Vector3(Mathf.Cos(a) * r, p % 2 == 0 ? 0.01f : 0.003f, Mathf.Sin(a) * r));
                    uv.Add(new Vector2(2, p % 2 == 0 ? 0.6f : 0.9f));
                }
                for (int p = 0; p < petals; p++) tri.AddRange(new[] { c0, c0 + 1 + (p + 1) % petals, c0 + 1 + p });
            }
            var mesh = new Mesh { name = flower ? "Flower" : "Grass tuft" };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            var normals = new Vector3[v.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
