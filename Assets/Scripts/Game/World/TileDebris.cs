using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Short pooled debris and dust for wall tiles (cosmetic only, never collides, never blocks sight for long). A break
    /// throws chunks out of both faces plus a puff of pale dust; a first crack knocks off a few chips. Everything lives
    /// in fixed arrays and is drawn as one mesh rebuilt each frame while anything is alive, so there are no GameObjects
    /// per chunk and nothing to garbage collect.
    /// </summary>
    public sealed class TileDebris : MonoBehaviour
    {
        private const int Max = 512;
        private const float Gravity = 15f;
        private static readonly Color Dust = new Color(0.82f, 0.80f, 0.76f);

        private readonly Vector3[] _pos = new Vector3[Max];
        private readonly Vector3[] _vel = new Vector3[Max];
        private readonly Vector3[] _spin = new Vector3[Max];
        private readonly Quaternion[] _rot = new Quaternion[Max];
        private readonly float[] _size = new float[Max];
        private readonly float[] _life = new float[Max];
        private readonly float[] _age = new float[Max];
        private readonly float[] _floor = new float[Max];
        private readonly bool[] _dust = new bool[Max];
        private readonly Color[] _col = new Color[Max];
        private int _next;
        private int _alive;

        private Mesh _mesh;
        private Vector3[] _v;
        private Color[] _c;
        private int[] _t;
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
        }

        private float Rand()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>Chunks (and dust if <paramref name="dust"/>) out of both faces of a tile.</summary>
        public void Burst(Aabb tile, Dir facing, Color col, int chunks, bool dust)
        {
            Vector3 lo = tile.Min.ToUnity(), hi = tile.Max.ToUnity();
            Vector3 n = facing == Dir.North ? Vector3.forward : facing == Dir.Up ? Vector3.up : Vector3.right;
            int total = chunks + (dust ? chunks / 2 + 4 : 0);
            for (int k = 0; k < total; k++)
            {
                int i = _next;
                _next = (_next + 1) % Max;
                bool isDust = k >= chunks;
                float side = Rand() < 0.5f ? -1f : 1f;
                _pos[i] = new Vector3(Mathf.Lerp(lo.x, hi.x, Rand()), Mathf.Lerp(lo.y, hi.y, Rand()), Mathf.Lerp(lo.z, hi.z, Rand()));
                Vector3 sideways = Vector3.Cross(n, Vector3.up) * (Rand() - 0.5f);
                if (isDust)
                {
                    _vel[i] = n * side * (0.6f + Rand()) + sideways + Vector3.up * (0.3f + Rand() * 0.5f);
                    _size[i] = 0.10f + Rand() * 0.10f;
                    _life[i] = 0.5f + Rand() * 0.4f;
                    _col[i] = Dust;
                }
                else
                {
                    _vel[i] = n * side * (2f + Rand() * 3f) + sideways * 3f + Vector3.up * (1f + Rand() * 2.5f);
                    _size[i] = (dust ? 0.08f : 0.04f) + Rand() * (dust ? 0.16f : 0.05f);
                    _life[i] = 0.6f + Rand() * 0.4f;
                    _col[i] = col * (0.85f + Rand() * 0.25f);
                }
                _dust[i] = isDust;
                _spin[i] = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * 720f;
                _rot[i] = Quaternion.Euler(Rand() * 360f, Rand() * 360f, Rand() * 360f);
                _age[i] = 0;
                _floor[i] = lo.y;
            }
            _alive = Max;
        }

        private void LateUpdate()
        {
            if (_mesh == null) return;
            if (_alive == 0) return;
            float dt = Time.deltaTime;
            int nv = 0, nt = 0, alive = 0;
            for (int i = 0; i < Max; i++)
            {
                if (_age[i] >= _life[i]) continue;
                _age[i] += dt;
                if (_age[i] >= _life[i]) continue;
                alive++;
                if (_dust[i])
                {
                    _vel[i] *= Mathf.Exp(-3f * dt);
                    _pos[i] += _vel[i] * dt;
                }
                else
                {
                    _vel[i].y -= Gravity * dt;
                    _pos[i] += _vel[i] * dt;
                    if (_pos[i].y < _floor[i] + _size[i] * 0.5f && _vel[i].y < 0)
                    {
                        _pos[i].y = _floor[i] + _size[i] * 0.5f;
                        _vel[i] = new Vector3(_vel[i].x * 0.4f, -_vel[i].y * 0.25f, _vel[i].z * 0.4f);
                    }
                    _rot[i] = Quaternion.Euler(_spin[i] * dt) * _rot[i];
                }
                float f = _age[i] / _life[i];
                // Dust grows then shrinks away; chunks keep their size, then shrink over the last 30%.
                float s = _dust[i] ? _size[i] * (1f + 2.5f * f) * (1f - f * f) : _size[i] * Mathf.Clamp01((1f - f) / 0.3f);
                AddCube(ref nv, ref nt, _pos[i], _rot[i], s * 0.5f, _col[i]);
            }
            _alive = alive;
            _mesh.Clear();
            if (nv == 0) return;
            _mesh.SetVertices(_v, 0, nv);
            _mesh.SetColors(_c, 0, nv);
            _mesh.SetTriangles(_t, 0, nt, 0);
            _mesh.RecalculateBounds();
        }

        private static readonly int[] CubeTris =
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
        };

        private static readonly int[] CornerOf = { 0, 1, 5, 4, 2, 3, 7, 6 };

        private void AddCube(ref int nv, ref int nt, Vector3 p, Quaternion q, float h, Color col)
        {
            Color c = col;
            c.a = 1;
            if (_linear) c = c.linear;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? -h : h, (k & 2) == 0 ? -h : h, (k & 4) == 0 ? -h : h);
                _v[nv + k] = p + q * corner;
                // Cheap fixed shading: corners facing up are lighter.
                _c[nv + k] = c * ((k & 2) == 0 ? 0.7f : 1.0f);
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
