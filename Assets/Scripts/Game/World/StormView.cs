using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// The battle royale circle in the world: a tall see-through blue wall where the circle is now, with bands that run
    /// up it so it reads as moving, and a faint white ring on the ground where the next circle will be. Drawn from the
    /// match message (the same numbers the server hurts with), so the wall is exactly where the damage starts.
    /// One mesh, scaled each frame; nothing per segment.
    /// </summary>
    public sealed class StormView : MonoBehaviour
    {
        private NetSession _session;
        private Transform _wall, _next;
        private Material _wallMat, _nextMat;
        private const int Segments = 160;
        private const float Height = 140f, Below = 20f;
        /// <summary>The ground's height at a map position (the battle royale's hills, map v3): the next circle's ring is
        /// laid on it. Null: flat ground at 0.</summary>
        public static System.Func<double, double, double> GroundAt;
        private Mesh _ringMesh;
        private double _ringX = double.NaN, _ringZ, _ringR;

        public void Init(NetSession session)
        {
            _session = session;
            Shader overlay = Shader.Find("SCULPT/Overlay");
            if (overlay == null) overlay = Shader.Find("Universal Render Pipeline/Unlit");
            _wallMat = new Material(overlay) { name = "Storm wall" };
            _wallMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            _wallMat.renderQueue = 3050;
            _nextMat = new Material(overlay) { name = "Next circle" };
            _nextMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            _nextMat.renderQueue = 3040;
            _wall = Make("Storm Wall", WallMesh(), _wallMat);
            _ringMesh = new Mesh { name = "Next circle", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _next = Make("Next Circle", _ringMesh, _nextMat);
        }

        private Transform Make(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        private static readonly Unity.Profiling.ProfilerMarker MkLateUpdateProf = new Unity.Profiling.ProfilerMarker("SCULPT.Storm");

        private void LateUpdate()
        {
            using (MkLateUpdateProf.Auto()) LateUpdateBody();
        }

        private void LateUpdateBody()
        {
            NetClient net = _session?.Client;
            MatchState m = net?.Match;
            bool on = m != null && m.IsBr && m.Phase == MatchPhase.Playing && m.Storm.Active;
            if (!on)
            {
                if (_wall.gameObject.activeSelf) _wall.gameObject.SetActive(false);
                if (_next.gameObject.activeSelf) _next.gameObject.SetActive(false);
                return;
            }
            StormState st = m.Storm;
            double now = net.ServerTimeEstimate;
            float r = Mathf.Max(0.05f, (float)st.RadiusAt(now));
            _wall.gameObject.SetActive(true);
            _wall.position = new Vector3((float)st.CentreXAt(now), -Below, (float)st.CentreZAt(now));
            _wall.localScale = new Vector3(r, 1, r);
            // Stronger while it closes in; bands drift upward (vertex colours carry the bands, the material the pulse).
            bool closing = now >= st.ShrinkStart && now < st.ShrinkEnd;
            float pulse = closing ? 0.85f + 0.15f * Mathf.Sin(Time.time * 5f) : 0.7f;
            _wallMat.SetColor("_Color", new Color(0.55f, 0.78f, 1f, pulse));
            _wall.rotation = Quaternion.Euler(0, Time.time * 2f, 0);

            bool showNext = now < st.ShrinkEnd && st.ToRadius > 0.5;
            _next.gameObject.SetActive(showNext);
            if (showNext)
            {
                if (st.ToX != _ringX || st.ToZ != _ringZ || st.ToRadius != _ringR)
                {
                    _ringX = st.ToX; _ringZ = st.ToZ; _ringR = st.ToRadius;
                    LayRing(_ringMesh, (float)_ringX, (float)_ringZ, (float)_ringR);
                }
                _nextMat.SetColor("_Color", new Color(1f, 1f, 1f, 0.55f));
            }
        }

        /// <summary>A unit-radius open cylinder from 0 to Height + Below, with soft horizontal bands in its vertex alpha.</summary>
        private static Mesh WallMesh()
        {
            const int rows = 28;
            var v = new Vector3[(Segments + 1) * (rows + 1)];
            var c = new Color[v.Length];
            var t = new int[Segments * rows * 6];
            for (int j = 0; j <= rows; j++)
            {
                float y = (Height + Below) * j / rows;
                float fade = Mathf.Clamp01(1f - j / (float)rows * 0.9f);
                float band = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(j * 0.9f));
                for (int i = 0; i <= Segments; i++)
                {
                    float a = i * Mathf.PI * 2 / Segments;
                    int k = j * (Segments + 1) + i;
                    v[k] = new Vector3(Mathf.Cos(a), y, Mathf.Sin(a));
                    c[k] = new Color(1, 1, 1, 0.28f * fade * band);
                }
            }
            int n = 0;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < Segments; i++)
                {
                    int a = j * (Segments + 1) + i, b = a + 1, d = a + Segments + 1, e = d + 1;
                    t[n++] = a; t[n++] = d; t[n++] = b;
                    t[n++] = b; t[n++] = d; t[n++] = e;
                }
            var m = new Mesh { name = "Storm wall" };
            m.vertices = v;
            m.colors = c;
            m.triangles = t;
            m.bounds = new Bounds(new Vector3(0, (Height + Below) / 2, 0), new Vector3(2, Height + Below, 2));
            return m;
        }

        /// <summary>
        /// A 0.6 m wide ring laid on the ground (a vertex every 3 m or so, 15 cm up), in world space: rebuilt only when the
        /// next circle changes, once a phase.
        /// </summary>
        private static void LayRing(Mesh m, float cx, float cz, float r)
        {
            int segs = Mathf.Clamp(Mathf.CeilToInt(2 * Mathf.PI * r / 3f), Segments, 4096);
            var v = new Vector3[(segs + 1) * 2];
            var c = new Color[v.Length];
            var t = new int[segs * 6];
            for (int i = 0; i <= segs; i++)
            {
                float a = i * Mathf.PI * 2 / segs, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                for (int k = 0; k < 2; k++)
                {
                    float rr = k == 0 ? Mathf.Max(0, r - 0.6f) : r;
                    double x = cx + ca * rr, z = cz + sa * rr;
                    double y = GroundAt != null ? GroundAt(x, z) : 0;
                    if (double.IsInfinity(y) || double.IsNaN(y)) y = 0;
                    v[i * 2 + k] = new Vector3((float)x, (float)y + 0.15f, (float)z);
                    c[i * 2 + k] = Color.white;
                }
            }
            int n = 0;
            for (int i = 0; i < segs; i++)
            {
                int a = i * 2;
                t[n++] = a; t[n++] = a + 2; t[n++] = a + 1;
                t[n++] = a + 1; t[n++] = a + 2; t[n++] = a + 3;
            }
            m.Clear();
            m.vertices = v;
            m.colors = c;
            m.triangles = t;
            m.RecalculateBounds();
        }
    }
}
