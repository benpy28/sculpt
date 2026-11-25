using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Short pooled debris and dust for wall tiles (cosmetic only, never collides). A break throws irregular chunks and
    /// brick-sized pieces out of both faces, mostly from the middle of the tile so the burst reads round, plus soft
    /// billowing dust; a bullet knocks off a few small chips and a little puff. Chunks are stretched, flattened boxes
    /// (no two the same shape), dust is soft camera-facing puffs (SCULPT/Dust), so nothing reads as a cube. Everything
    /// lives in fixed arrays and is drawn as two meshes rebuilt each frame only while anything is alive: no GameObjects
    /// per piece and nothing to garbage collect. Dust is never opaque and fades within ~2 s, so it never hides a player
    /// for long.
    /// </summary>
    public sealed class TileDebris : MonoBehaviour
    {
        private const int Max = 512;
        private const int MaxDust = 192;
        private const float Gravity = 15f;
        private static readonly Color Dust = new Color(0.80f, 0.77f, 0.72f);

        private readonly Vector3[] _pos = new Vector3[Max];
        private readonly Vector3[] _vel = new Vector3[Max];
        private readonly Vector3[] _spin = new Vector3[Max];
        private readonly Vector3[] _shape = new Vector3[Max];
        private readonly Quaternion[] _rot = new Quaternion[Max];
        private readonly float[] _life = new float[Max];
        private readonly float[] _age = new float[Max];
        private readonly float[] _floor = new float[Max];
        private readonly Color[] _col = new Color[Max];
        private int _next;
        private int _alive;

        private readonly Vector3[] _dPos = new Vector3[MaxDust];
        private readonly Vector3[] _dVel = new Vector3[MaxDust];
        private readonly float[] _dSize = new float[MaxDust];
        private readonly float[] _dLife = new float[MaxDust];
        private readonly float[] _dAge = new float[MaxDust];
        private readonly float[] _dAlpha = new float[MaxDust];
        private readonly float[] _dSpin = new float[MaxDust];
        private readonly Color[] _dCol = new Color[MaxDust];
        private int _dNext;
        private int _dAlive;

        private Mesh _mesh, _dustMesh;
        private Vector3[] _v, _dv;
        private Color[] _c, _dc;
        private Vector2[] _duv;
        private int[] _t, _dt;
        private bool _linear;
        private uint _seed = 12345;

        private void Awake()
        {
            Shader shader = Shader.Find("SCULPT/Greybox");
            if (shader == null) { enabled = false; return; }
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            _mesh = new Mesh { name = "Tile debris" };
            _mesh.MarkDynamic();
            _v = new Vector3[Max * 8];
            _c = new Color[Max * 8];
            _t = new int[Max * 36];
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(shader) { name = "Debris (runtime)" };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            Shader dust = Shader.Find("SCULPT/Dust");
            if (dust == null) { Debug.LogWarning("[SCULPT] Shader SCULPT/Dust not found; wall dust is off."); return; }
            _dustMesh = new Mesh { name = "Tile dust" };
            _dustMesh.MarkDynamic();
            _dv = new Vector3[MaxDust * 4];
            _dc = new Color[MaxDust * 4];
            _duv = new Vector2[MaxDust * 4];
            _dt = new int[MaxDust * 6];
            for (int i = 0; i < MaxDust; i++)
            {
                _duv[i * 4] = new Vector2(-1, -1); _duv[i * 4 + 1] = new Vector2(-1, 1);
                _duv[i * 4 + 2] = new Vector2(1, 1); _duv[i * 4 + 3] = new Vector2(1, -1);
            }
            var go = new GameObject("Tile dust");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _dustMesh;
            var dr = go.AddComponent<MeshRenderer>();
            dr.sharedMaterial = new Material(dust) { name = "Dust (runtime)" };
            dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dr.receiveShadows = false;
        }

        private float Rand()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Roughly bell-shaped 0..1 (mean of two), so bursts come mostly from the middle.</summary>
        private float Mid() => (Rand() + Rand()) * 0.5f;

        private static Vector3 NormalOf(Dir facing) =>
            facing == Dir.North ? Vector3.forward : facing == Dir.Up ? Vector3.up : Vector3.right;

        /// <summary>Chunks (and dust if <paramref name="dust"/>) out of both faces of a tile.</summary>
        public void Burst(Aabb tile, Dir facing, Color col, int chunks, bool dust)
        {
            Vector3 lo = tile.Min.ToUnity(), hi = tile.Max.ToUnity();
            Vector3 n = NormalOf(facing);
            Vector3 side1 = Vector3.Cross(n, Mathf.Abs(n.y) > 0.5f ? Vector3.forward : Vector3.up).normalized;
            for (int k = 0; k < chunks; k++)
            {
                float face = Rand() < 0.5f ? -1f : 1f;
                var p = new Vector3(Mathf.Lerp(lo.x, hi.x, Mid()), Mathf.Lerp(lo.y, hi.y, Mid()), Mathf.Lerp(lo.z, hi.z, Mid()));
                Vector3 v = n * face * (2f + Rand() * 3.5f) + side1 * ((Rand() - 0.5f) * 3f) + Vector3.up * (0.8f + Rand() * 2.5f);
                // Big bursts mix rough lumps with brick-sized pieces; small ones (first crack) are flakes.
                Vector3 shape;
                if (!dust) shape = new Vector3(0.03f + Rand() * 0.05f, 0.012f + Rand() * 0.02f, 0.025f + Rand() * 0.04f);
                else if (Rand() < 0.45f) shape = new Vector3(0.20f, 0.065f, 0.10f) * (0.7f + Rand() * 0.5f);
                else
                {
                    float s = 0.06f + Rand() * 0.16f;
                    shape = new Vector3(s * (0.6f + Rand() * 0.9f), s * (0.35f + Rand() * 0.5f), s * (0.5f + Rand() * 0.8f));
                }
                Chunk(p, v, shape, col * (0.82f + Rand() * 0.3f), 0.7f + Rand() * 0.5f, lo.y);
            }
            if (!dust) return;
            Vector3 c = (lo + hi) * 0.5f;
            int puffs = 4 + chunks / 2;
            for (int k = 0; k < puffs; k++)
            {
                float face = Rand() < 0.5f ? -1f : 1f;
                var p = new Vector3(Mathf.Lerp(lo.x, hi.x, Mid()), Mathf.Lerp(lo.y, hi.y, Mid()), Mathf.Lerp(lo.z, hi.z, Mid()));
                p = Vector3.Lerp(p, c, 0.3f);
                Vector3 v = n * face * (0.8f + Rand() * 1.6f) + side1 * ((Rand() - 0.5f) * 1.2f) + Vector3.up * (0.2f + Rand() * 0.5f);
                Puff(p, v, 0.35f + Rand() * 0.45f, 1.1f + Rand() * 0.9f, 0.5f + Rand() * 0.2f, Color.Lerp(Dust, col, 0.35f));
            }
        }

        /// <summary>A bullet hit: a few small chips off the face and a small puff, tinted by the surface.</summary>
        public void Chip(Vector3 at, Vector3 normal, Color col, float amount)
        {
            int chips = Mathf.Max(1, Mathf.RoundToInt(4 * amount));
            Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up).normalized;
            Vector3 t2 = Vector3.Cross(normal, t1);
            for (int k = 0; k < chips; k++)
            {
                Vector3 v = normal * (1.5f + Rand() * 2.5f) + (t1 * (Rand() - 0.5f) + t2 * (Rand() - 0.5f)) * 3f + Vector3.up * Rand();
                float s = 0.015f + Rand() * 0.03f;
                Chunk(at + normal * 0.02f, v, new Vector3(s, s * (0.4f + Rand() * 0.5f), s * (0.6f + Rand() * 0.6f)),
                    col * (0.75f + Rand() * 0.3f), 0.35f + Rand() * 0.3f, at.y - 3f);
            }
            int puffs = amount > 0.75f ? 2 : 1;
            for (int k = 0; k < puffs; k++)
                Puff(at + normal * 0.06f, normal * (0.5f + Rand() * 0.8f) + Vector3.up * 0.15f, 0.12f + Rand() * 0.12f * amount,
                    0.5f + Rand() * 0.4f, 0.45f, Color.Lerp(Dust, col, 0.45f));
        }

        private void Chunk(Vector3 p, Vector3 v, Vector3 shape, Color col, float life, float floor)
        {
            int i = _next;
            _next = (_next + 1) % Max;
            _pos[i] = p;
            _vel[i] = v;
            _shape[i] = shape;
            _col[i] = col;
            _life[i] = life;
            _age[i] = 0;
            _floor[i] = floor;
            _spin[i] = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * 720f;
            _rot[i] = Quaternion.Euler(Rand() * 360f, Rand() * 360f, Rand() * 360f);
            _alive = Max;
        }

        private void Puff(Vector3 p, Vector3 v, float size, float life, float alpha, Color col)
        {
            if (_dustMesh == null) return;
            int i = _dNext;
            _dNext = (_dNext + 1) % MaxDust;
            _dPos[i] = p;
            _dVel[i] = v;
            _dSize[i] = size;
            _dLife[i] = life;
            _dAge[i] = 0;
            _dAlpha[i] = alpha;
            _dSpin[i] = (Rand() - 0.5f) * 1.5f + Rand() * 6.28f * 10f;
            _dCol[i] = col;
            _dAlive = MaxDust;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (_mesh != null && _alive > 0) UpdateChunks(dt);
            if (_dustMesh != null && _dAlive > 0) UpdateDust(dt);
        }

        private void UpdateChunks(float dt)
        {
            int nv = 0, nt = 0, alive = 0;
            for (int i = 0; i < Max; i++)
            {
                if (_age[i] >= _life[i]) continue;
                _age[i] += dt;
                if (_age[i] >= _life[i]) continue;
                alive++;
                _vel[i].y -= Gravity * dt;
                _pos[i] += _vel[i] * dt;
                float h = _shape[i].y * 0.5f;
                if (_pos[i].y < _floor[i] + h && _vel[i].y < 0)
                {
                    _pos[i].y = _floor[i] + h;
                    _vel[i] = new Vector3(_vel[i].x * 0.4f, -_vel[i].y * 0.25f, _vel[i].z * 0.4f);
                    _spin[i] *= 0.5f;
                }
                _rot[i] = Quaternion.Euler(_spin[i] * dt) * _rot[i];
                float f = _age[i] / _life[i];
                float s = Mathf.Clamp01((1f - f) / 0.3f); // keep size, shrink over the last 30%
                AddChunk(ref nv, ref nt, _pos[i], _rot[i], _shape[i] * (0.5f * s), _col[i], i);
            }
            _alive = alive;
            _mesh.Clear();
            if (nv == 0) return;
            _mesh.SetVertices(_v, 0, nv);
            _mesh.SetColors(_c, 0, nv);
            _mesh.SetTriangles(_t, 0, nt, 0);
            _mesh.RecalculateBounds();
        }

        private void UpdateDust(float dt)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 right = cam.transform.right, up = cam.transform.up;
            int nv = 0, nt = 0, alive = 0;
            for (int i = 0; i < MaxDust; i++)
            {
                if (_dAge[i] >= _dLife[i]) continue;
                _dAge[i] += dt;
                if (_dAge[i] >= _dLife[i]) continue;
                alive++;
                _dVel[i] *= Mathf.Exp(-2.5f * dt);
                _dVel[i].y += 0.15f * dt; // warm dust lifts a little
                _dPos[i] += _dVel[i] * dt;
                float f = _dAge[i] / _dLife[i];
                float size = _dSize[i] * (1f + 1.6f * Mathf.Sqrt(f));
                float a = _dAlpha[i] * Mathf.Clamp01(f * 8f) * (1f - f) * (1f - f);
                float ang = _dSpin[i] + f * 0.6f;
                Vector3 r = (right * Mathf.Cos(ang) + up * Mathf.Sin(ang)) * size;
                Vector3 u = (up * Mathf.Cos(ang) - right * Mathf.Sin(ang)) * size;
                Color c = _linear ? _dCol[i].linear : _dCol[i];
                c.a = a;
                Vector3 p = _dPos[i];
                _dv[nv] = p - r - u; _dv[nv + 1] = p - r + u; _dv[nv + 2] = p + r + u; _dv[nv + 3] = p + r - u;
                _dc[nv] = c; _dc[nv + 1] = c; _dc[nv + 2] = c; _dc[nv + 3] = c;
                _dt[nt] = nv; _dt[nt + 1] = nv + 1; _dt[nt + 2] = nv + 2;
                _dt[nt + 3] = nv; _dt[nt + 4] = nv + 2; _dt[nt + 5] = nv + 3;
                nv += 4;
                nt += 6;
            }
            _dAlive = alive;
            _dustMesh.Clear();
            if (nv == 0) return;
            _dustMesh.SetVertices(_dv, 0, nv);
            _dustMesh.SetColors(_dc, 0, nv);
            _dustMesh.SetUVs(0, _duv, 0, nv);
            _dustMesh.SetTriangles(_dt, 0, nt, 0);
            _dustMesh.RecalculateBounds();
        }

        private static readonly int[] CubeTris =
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
        };

        private static readonly int[] CornerOf = { 0, 1, 5, 4, 2, 3, 7, 6 };

        /// <summary>
        /// A stretched box with its corners pulled in or out a little (fixed per piece), so pieces look like broken
        /// lumps and bricks rather than cubes. Vertex alpha 0.5 = rough surface (no seams).
        /// </summary>
        private void AddChunk(ref int nv, ref int nt, Vector3 p, Quaternion q, Vector3 h, Color col, int id)
        {
            Color c = col;
            if (_linear) c = c.linear;
            c.a = 0.5f;
            uint s = (uint)(id * 2654435761u) ^ 0x5bd1e995u;
            for (int k = 0; k < 8; k++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                float j = 0.7f + (s & 0xFF) / 255f * 0.5f; // corner pulled in to 70%, out to 120%
                var corner = new Vector3((k & 1) == 0 ? -h.x : h.x, (k & 2) == 0 ? -h.y : h.y, (k & 4) == 0 ? -h.z : h.z) * j;
                _v[nv + k] = p + q * corner;
                // Cheap fixed shading: corners facing up are lighter.
                Color ck = c * ((k & 2) == 0 ? 0.68f : 1.0f);
                ck.a = 0.5f;
                _c[nv + k] = ck;
            }
            // Corner order 0..7 is (x,y,z) bits; CornerOf re-maps the CubeTris layout (bottom 0-3, top 4-7). Each
            // triangle is emitted reversed so it winds clockwise seen from outside (checked: outward normals).
            for (int k = 0; k < 36; k += 3)
            {
                _t[nt + k] = nv + CornerOf[CubeTris[k]];
                _t[nt + k + 1] = nv + CornerOf[CubeTris[k + 2]];
                _t[nt + k + 2] = nv + CornerOf[CubeTris[k + 1]];
            }
            nv += 8;
            nt += 36;
        }
    }
}
