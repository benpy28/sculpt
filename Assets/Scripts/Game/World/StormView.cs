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
            _next = Make("Next Circle", RingMesh(), _nextMat);
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

        private void LateUpdate()
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
                _next.position = new Vector3((float)st.ToX, 0.06f, (float)st.ToZ);
                float nr = (float)st.ToRadius;
                _next.localScale = new Vector3(nr, 1, nr);
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

        /// <summary>A thin flat ring on the ground at unit radius (0.6 m wide at any size is close enough: scaled with it).</summary>
        private static Mesh RingMesh()
        {
            var v = new Vector3[(Segments + 1) * 2];
            var c = new Color[v.Length];
            var t = new int[Segments * 6];
            for (int i = 0; i <= Segments; i++)
            {
                float a = i * Mathf.PI * 2 / Segments;
                v[i * 2] = new Vector3(Mathf.Cos(a) * 0.985f, 0, Mathf.Sin(a) * 0.985f);
                v[i * 2 + 1] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                c[i * 2] = c[i * 2 + 1] = Color.white;
            }
            int n = 0;
            for (int i = 0; i < Segments; i++)
            {
                int a = i * 2;
                t[n++] = a; t[n++] = a + 2; t[n++] = a + 1;
                t[n++] = a + 1; t[n++] = a + 2; t[n++] = a + 3;
            }
            var m = new Mesh { name = "Next circle" };
            m.vertices = v;
            m.colors = c;
            m.triangles = t;
            return m;
        }
    }
}
