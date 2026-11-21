using System.Collections.Generic;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Draws the building brushes (greybox, Ben 2026-11-21) from code: one mesh per 16 m block of the map, built once.
    /// Colour-coded so unbreakable parts read at a glance: floors warm concrete, frame (columns, beams) dark blue
    /// steel, stairs concrete with a yellow nose on every step, ramps lighter concrete with yellow edges, plain walls
    /// slate. Never purple (reserved for sculptable). Stairs are drawn as steps but collide as a smooth ramp: each
    /// step's tread is centred on the slope, so the drawn steps stay within half a step (7.5 cm) of what you touch.
    /// </summary>
    public sealed class BrushView : MonoBehaviour
    {
        private const float BlockSize = 16f;
        private const double StepRise = 0.15;

        public static readonly Color Floor = new Color(0.66f, 0.62f, 0.56f);
        public static readonly Color Frame = new Color(0.27f, 0.33f, 0.42f);
        public static readonly Color Stairs = new Color(0.60f, 0.58f, 0.55f);
        public static readonly Color StairNose = new Color(0.95f, 0.76f, 0.18f);
        public static readonly Color Ramp = new Color(0.72f, 0.70f, 0.66f);
        public static readonly Color Wall = new Color(0.40f, 0.43f, 0.46f);
        public static readonly Color Ground = new Color(0.46f, 0.52f, 0.38f);
        public static readonly Color Marker = new Color(0.20f, 0.85f, 0.45f);

        private readonly List<GameObject> _blocks = new List<GameObject>();
        private Material _mat;
        private bool _linear;

        public void Build(BrushWorld world)
        {
            foreach (GameObject g in _blocks) Destroy(g);
            _blocks.Clear();
            Shader shader = Shader.Find("SCULPT/Greybox");
            if (shader == null)
            {
                Debug.LogError("[SCULPT] Shader SCULPT/Greybox not found; buildings are not drawn.");
                return;
            }
            _mat = new Material(shader) { name = "Greybox (runtime)" };
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

            var byBlock = new Dictionary<Vector2Int, MeshBits>();
            for (int i = 0; i < world.Count; i++)
            {
                Brush b = world[i];
                Vector3 c = ((b.Bounds.Min + b.Bounds.Max) * 0.5).ToUnity();
                var key = new Vector2Int(Mathf.FloorToInt(c.x / BlockSize), Mathf.FloorToInt(c.z / BlockSize));
                if (!byBlock.TryGetValue(key, out MeshBits m)) byBlock[key] = m = new MeshBits();
                AddBrush(m, b);
            }
            foreach (var kv in byBlock)
            {
                var mesh = new Mesh { name = $"Brushes {kv.Key}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(kv.Value.V);
                mesh.SetNormals(kv.Value.N);
                mesh.SetColors(kv.Value.C);
                mesh.SetTriangles(kv.Value.T, 0);
                mesh.RecalculateBounds();
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _blocks.Add(go);
            }
        }

        private sealed class MeshBits
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
        }

        private static Color ColorOf(BrushKind k)
        {
            switch (k)
            {
                case BrushKind.Floor: return Floor;
                case BrushKind.Frame: return Frame;
                case BrushKind.Stairs: return Stairs;
                case BrushKind.Ramp: return Ramp;
                case BrushKind.Wall: return Wall;
                case BrushKind.Marker: return Marker;
                default: return Ground;
            }
        }

        private void AddBrush(MeshBits m, in Brush b)
        {
            Color col = ColorOf(b.Kind);
            if (b.Kind == BrushKind.Stairs && b.IsRamp) { AddStairs(m, b, col); return; }
            if (b.IsRamp) { AddWedge(m, b, col); return; }
            AddBox(m, b.Bounds.Min.ToUnity(), b.Bounds.Max.ToUnity(), col);
        }

        // ---- shapes ----

        /// <summary>Fixed sun shading per face, baked into the colour (tops brightest, sides darker).</summary>
        private static float Shade(Vector3 n)
        {
            if (n.y > 0.5f) return 1.0f - (1f - n.y) * 0.4f;
            if (n.y < -0.5f) return 0.45f;
            return Mathf.Abs(n.z) > Mathf.Abs(n.x) ? 0.80f : 0.68f;
        }

        private void Quad(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            Color s = col * Shade(n);
            s.a = 1;
            if (_linear) s = s.linear;
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c); m.V.Add(d);
            for (int k = 0; k < 4; k++) { m.N.Add(n); m.C.Add(s); }
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
            m.T.Add(i); m.T.Add(i + 2); m.T.Add(i + 3);
        }

        private void Tri(MeshBits m, Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            Color s = col * Shade(n);
            s.a = 1;
            if (_linear) s = s.linear;
            int i = m.V.Count;
            m.V.Add(a); m.V.Add(b); m.V.Add(c);
            for (int k = 0; k < 3; k++) { m.N.Add(n); m.C.Add(s); }
            m.T.Add(i); m.T.Add(i + 1); m.T.Add(i + 2);
        }

        private void AddBox(MeshBits m, Vector3 lo, Vector3 hi, Color col)
        {
            Vector3 p000 = lo, p111 = hi;
            Vector3 p100 = new Vector3(hi.x, lo.y, lo.z), p010 = new Vector3(lo.x, hi.y, lo.z), p001 = new Vector3(lo.x, lo.y, hi.z);
            Vector3 p110 = new Vector3(hi.x, hi.y, lo.z), p101 = new Vector3(hi.x, lo.y, hi.z), p011 = new Vector3(lo.x, hi.y, hi.z);
            // Winding: clockwise seen from outside (Unity front faces).
            Quad(m, p010, p011, p111, p110, col); // top
            Quad(m, p000, p100, p101, p001, col); // bottom
            Quad(m, p000, p010, p110, p100, col); // south (-z)
            Quad(m, p101, p111, p011, p001, col); // north (+z)
            Quad(m, p001, p011, p010, p000, col); // west (-x)
            Quad(m, p100, p110, p111, p101, col); // east (+x)
        }

        /// <summary>Maps (u along the rise 0..1, v across 0..1, y) to world for a wedge's footprint.</summary>
        private static Vector3 Local(in Brush b, double u, double v, double y)
        {
            Aabb w = b.Bounds;
            double x, z;
            switch (b.Rise)
            {
                case Dir.East: x = w.Min.X + u * (w.Max.X - w.Min.X); z = w.Min.Z + v * (w.Max.Z - w.Min.Z); break;
                case Dir.West: x = w.Max.X - u * (w.Max.X - w.Min.X); z = w.Max.Z - v * (w.Max.Z - w.Min.Z); break;
                case Dir.North: z = w.Min.Z + u * (w.Max.Z - w.Min.Z); x = w.Max.X - v * (w.Max.X - w.Min.X); break;
                default: z = w.Max.Z - u * (w.Max.Z - w.Min.Z); x = w.Min.X + v * (w.Max.X - w.Min.X); break;
            }
            return new Vector3((float)x, (float)y, (float)z);
        }

        private void AddWedge(MeshBits m, in Brush b, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            Vector3 l0 = Local(b, 0, 0, y0), l1 = Local(b, 0, 1, y0);   // low edge
            Vector3 h0 = Local(b, 1, 0, y1), h1 = Local(b, 1, 1, y1);   // high edge (top)
            Vector3 b0 = Local(b, 1, 0, y0), b1 = Local(b, 1, 1, y0);   // high end, bottom
            Quad(m, l0, l1, h1, h0, col);          // slope
            Quad(m, b0, h0, h1, b1, col);          // high end face
            Quad(m, l0, b0, b1, l1, col);          // bottom
            Tri(m, l0, h0, b0, col);               // sides
            Tri(m, l1, b1, h1, col);
            // Yellow edge lines along the low edge and the crest so the ramp's ends read.
            AddStripe(m, b, 0.0, 0.08, y0, y0, StairNose);
            AddStripe(m, b, 1.0 - 0.04, 1.0, y1, y1, StairNose);
        }

        /// <summary>A thin painted band across the wedge at rise fraction u0..u1, lifted 1 cm (cosmetic only).</summary>
        private void AddStripe(MeshBits m, in Brush b, double u0, double u1, double ya, double yb, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            double h0 = y0 + (y1 - y0) * u0 + 0.01, h1 = y0 + (y1 - y0) * u1 + 0.01;
            Quad(m, Local(b, u0, 0, h0), Local(b, u0, 1, h0), Local(b, u1, 1, h1), Local(b, u1, 0, h1), col);
        }

        private void AddStairs(MeshBits m, in Brush b, Color col)
        {
            double y0 = b.Bounds.Min.Y, y1 = b.Bounds.Max.Y;
            double rise = y1 - y0;
            int n = Mathf.Max(1, (int)System.Math.Round(rise / StepRise));
            for (int i = 0; i < n; i++)
            {
                double u0 = i / (double)n, u1 = (i + 1) / (double)n;
                double top = y0 + rise * (i + 0.5) / n; // tread centred on the slope
                Vector3 lo = Local(b, u0, 0, y0), hi = Local(b, u1, 1, top);
                AddBox(m, Vector3.Min(lo, hi), Vector3.Max(lo, hi), col);
                // Yellow nose strip on the front 6 cm of each tread.
                // The nose is the step's low-side edge (the edge you walk off going down).
                double um = u0 + (u1 - u0) * 0.18;
                Vector3 a = Local(b, u0, 0, top + 0.005), c = Local(b, um, 1, top + 0.005);
                Vector3 mn = Vector3.Min(a, c), mx = Vector3.Max(a, c);
                Quad(m, new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mn.z), StairNose);
            }
        }
    }
}
