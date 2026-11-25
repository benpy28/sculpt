using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Where a bullet traced on this client hit a building brush (WeaponView raises it for your own shots at once and for
    /// other players' shots when the server's shot arrives). Returns true when the building view drew the impact, so
    /// the gun only adds its sound. Visual only.
    /// </summary>
    public static class WallImpacts
    {
        /// <summary>(point, surface normal, brush index, amount 0..1) → handled.</summary>
        public static System.Func<Vector3, Vector3, int, float, bool> OnBullet;
    }

    /// <summary>
    /// Bullet marks on buildings: a small dark chipped crater with a lighter ring of knocked-off surface, in the colour
    /// of what was hit. They stay until the tile breaks or is healed (the oldest go past <see cref="Max"/>). Marks never
    /// hang past the edge of the face they are on, so they never draw into a hole. One mesh, rebuilt only when a mark is
    /// added or cleared.
    /// </summary>
    public sealed class WallMarks : MonoBehaviour
    {
        private const int Max = 400;
        private const int Ring = 8, Crater = 6;

        private struct Mark
        {
            public Vector3 P, N;
            public float R;
            public uint Seed;
            public Color C;
            public int Tile;
            public bool Live;
        }

        private readonly Mark[] _marks = new Mark[Max];
        private int _next;
        private bool _dirty;
        private bool _linear;
        private uint _seed = 0x1234567u;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Vector3[] _v;
        private Color[] _c;
        private int[] _t;

        public void Init(Material mat, bool linear)
        {
            _linear = linear;
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "Bullet marks" };
                _mesh.MarkDynamic();
                int verts = Max * (Ring + Crater + 2);
                _v = new Vector3[verts];
                _c = new Color[verts];
                _t = new int[Max * (Ring + Crater) * 3];
                gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _renderer = gameObject.AddComponent<MeshRenderer>();
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
            }
            _renderer.sharedMaterial = mat;
            _renderer.enabled = mat != null;
        }

        private uint _rs = 1u;

        /// <summary>Per-mark shape random (seeded from the mark, so a mark keeps its shape on every rebuild).</summary>
        private float Rnd()
        {
            _rs ^= _rs << 13; _rs ^= _rs >> 17; _rs ^= _rs << 5;
            return (_rs & 0xFFFFFF) / (float)0x1000000;
        }

        private float Rand()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xFFFFFF) / (float)0x1000000;
        }

        public void ClearAll()
        {
            for (int i = 0; i < Max; i++) _marks[i].Live = false;
            _dirty = true;
        }

        public void ClearTile(int tile)
        {
            if (tile < 0) return;
            for (int i = 0; i < Max; i++)
                if (_marks[i].Live && _marks[i].Tile == tile) { _marks[i].Live = false; _dirty = true; }
        }

        /// <param name="clamp">Shrink the mark to stay on the face of <paramref name="bounds"/> (boxes; not wedges).</param>
        public void Add(Vector3 at, Vector3 normal, int tile, Aabb bounds, bool clamp, Color col)
        {
            if (_mesh == null) return;
            float r = 0.03f + Rand() * 0.025f;
            if (clamp)
            {
                Vector3 lo = bounds.Min.ToUnity(), hi = bounds.Max.ToUnity();
                float room = float.MaxValue;
                for (int a = 0; a < 3; a++)
                    if (Mathf.Abs(normal[a]) < 0.5f) room = Mathf.Min(room, Mathf.Min(at[a] - lo[a], hi[a] - at[a]));
                r = Mathf.Min(r, room / 1.45f); // the ring reaches 1.4 r
                if (r < 0.01f) return;
            }
            _marks[_next] = new Mark { P = at, N = normal.normalized, R = r, Seed = (uint)(Rand() * 16777215f) + 1u, C = col, Tile = tile, Live = true };
            _next = (_next + 1) % Max;
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (!_dirty || _mesh == null) return;
            _dirty = false;
            int nv = 0, nt = 0;
            for (int i = 0; i < Max; i++)
            {
                if (!_marks[i].Live) continue;
                Mark m = _marks[i];
                _rs = m.Seed;
                BrushView.Basis2(m.N, out Vector3 r, out Vector3 u);
                float a0 = Rnd() * 6.283f;
                // Ring of knocked-off surface (lighter dust-white mixed in), then the dark crater on top.
                Color ring = Color.Lerp(m.C * 0.8f, new Color(0.85f, 0.82f, 0.77f), 0.25f);
                Fan(ref nv, ref nt, m.P + m.N * 0.007f, m.N, r, u, m.R * 1.15f, 0.22f, Ring, a0, ring);
                Fan(ref nv, ref nt, m.P + m.N * 0.008f, m.N, r, u, m.R * 0.5f, 0.3f, Crater, a0 + 0.4f, m.C * 0.3f);
            }
            _mesh.Clear();
            if (nv == 0) return;
            _mesh.SetVertices(_v, 0, nv);
            _mesh.SetColors(_c, 0, nv);
            _mesh.SetTriangles(_t, 0, nt, 0);
            _mesh.RecalculateBounds();
        }

        /// <summary>A jagged fan facing n (Cross(r, u) = n, so increasing angle gives front faces toward n).</summary>
        private void Fan(ref int nv, ref int nt, Vector3 c, Vector3 n, Vector3 r, Vector3 u, float radius, float jitter, int verts,
            float a0, Color col)
        {
            Color k = _linear ? col.linear : col;
            k.a = 0.5f; // rough surface in SCULPT/Greybox
            int centre = nv;
            _v[nv] = c; _c[nv] = k; nv++;
            for (int j = 0; j < verts; j++)
            {
                float a = a0 + j * 6.2831853f / verts;
                float rr = radius * (1f + (Rnd() - 0.5f) * 2f * jitter);
                _v[nv] = c + (r * Mathf.Cos(a) + u * Mathf.Sin(a)) * rr;
                _c[nv] = k * (0.9f + Rnd() * 0.15f);
                _c[nv].a = 0.5f;
                nv++;
            }
            for (int j = 0; j < verts; j++)
            {
                _t[nt++] = centre;
                _t[nt++] = centre + 1 + j;
                _t[nt++] = centre + 1 + (j + 1) % verts;
            }
        }
    }
}
