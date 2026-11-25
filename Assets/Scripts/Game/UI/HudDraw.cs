using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Shared look for the game HUD: dark translucent rounded panels, thin bars and clean shadowed text. GL calls work in
    /// pixel space with y up (call inside GL.LoadPixelMatrix); text uses GUI space (y down). No purple (sculptable only).
    ///
    /// Built to cost little every frame (2026-11-25: the HUD was a third of the frame and made most of the garbage):
    /// - Shapes are plain triangles. Wrap a run of them in <see cref="BeginBatch"/> / <see cref="EndBatch"/> and the
    ///   whole run goes to the graphics card as one draw instead of one or two per shape.
    /// - <see cref="Label"/> draws the glyphs itself from the engine's built-in font (shadow and text in one draw)
    ///   instead of two GUI.Label calls, which cost several times more each and allocate.
    /// - <see cref="Num"/> hands out cached strings for small numbers, so a HUD never builds a string per frame for
    ///   health, ammo, a slot number or a compass heading.
    /// </summary>
    public static class HudDraw
    {
        public static readonly Color Panel = new Color(0.05f, 0.06f, 0.08f, 0.58f);
        public static readonly Color PanelEdge = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color Text = new Color(0.96f, 0.97f, 0.98f, 1f);
        public static readonly Color Dim = new Color(0.96f, 0.97f, 0.98f, 0.55f);
        public static readonly Color Red = new Color(1f, 0.27f, 0.22f, 1f);
        public static readonly Color Amber = new Color(1f, 0.74f, 0.25f, 1f);

        private static GUIStyle _style;

        /// <summary>
        /// Pixel-space projection for GL drawing inside OnGUI, y up from the bottom of the screen. On Direct3D the
        /// Editor's Game view draws OnGUI into a render texture, where a plain GL.LoadPixelMatrix comes out upside down
        /// (play-test 2026-11-20: panels at the top, damage arrows mirrored); GetGPUProjectionMatrix corrects for it,
        /// and is a no-op when drawing straight to the screen.
        /// </summary>
        public static void LoadPixels()
        {
            GL.LoadIdentity();
            Matrix4x4 ortho = Matrix4x4.Ortho(0, Screen.width, 0, Screen.height, -1f, 100f);
            GL.LoadProjectionMatrix(GL.GetGPUProjectionMatrix(ortho, RenderTexture.active != null));
        }

        // ---------------------------------------------------------------- Shapes (GL, y up)

        private static bool _batch;

        /// <summary>
        /// Start a run of shapes drawn as one batch (same material, same matrix). Everything in this class, and the
        /// <see cref="Quad(float,float,float,float,Color)"/> / <see cref="Tri"/> primitives, may be called inside;
        /// raw GL.Begin may not. Always pair with <see cref="EndBatch"/> in a finally block.
        /// </summary>
        public static void BeginBatch()
        {
            if (_batch) return;
            _batch = true;
            GL.Begin(GL.TRIANGLES);
        }

        public static void EndBatch()
        {
            if (!_batch) return;
            _batch = false;
            GL.End();
        }

        /// <summary>Opens a draw for one shape unless a batch is already open. Returns whether it opened one.</summary>
        private static bool Open()
        {
            if (_batch) return false;
            GL.Begin(GL.TRIANGLES);
            return true;
        }

        private static void Close(bool opened)
        {
            if (opened) GL.End();
        }

        /// <summary>Filled axis-aligned rectangle.</summary>
        public static void Quad(float x, float y, float w, float h, Color c)
        {
            if (w <= 0 || h <= 0) return;
            bool o = Open();
            GL.Color(c);
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0);
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
            Close(o);
        }

        /// <summary>Filled four-cornered shape, corners in order around it; each pair of corners can have its own colour (a to b in ca, c to d in cb).</summary>
        public static void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb)
        {
            bool o = Open();
            GL.Color(ca); GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0);
            GL.Color(cb); GL.Vertex3(c.x, c.y, 0);
            GL.Color(ca); GL.Vertex3(a.x, a.y, 0);
            GL.Color(cb); GL.Vertex3(c.x, c.y, 0); GL.Vertex3(d.x, d.y, 0);
            Close(o);
        }

        public static void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col) => Quad(a, b, c, d, col, col);

        public static void Tri(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            bool o = Open();
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0); GL.Vertex3(b.x, b.y, 0); GL.Vertex3(c.x, c.y, 0);
            Close(o);
        }

        /// <summary>Part of a ring: centre, radius, line thickness, start angle (0 = up, clockwise) and span in degrees.</summary>
        public static void Arc(float cx, float cy, float r, float thick, float fromDeg, float spanDeg, Color c)
        {
            if (spanDeg <= 0.01f) return;
            int steps = Mathf.Max(2, Mathf.CeilToInt(spanDeg / 6f));
            float ri = r - thick * 0.5f, ro = r + thick * 0.5f;
            bool o = Open();
            GL.Color(c);
            float a0 = fromDeg * Mathf.Deg2Rad;
            float s0 = Mathf.Sin(a0), c0 = Mathf.Cos(a0);
            for (int i = 0; i < steps; i++)
            {
                float a1 = (fromDeg + spanDeg * (i + 1) / steps) * Mathf.Deg2Rad;
                float s1 = Mathf.Sin(a1), c1 = Mathf.Cos(a1);
                GL.Vertex3(cx + s0 * ri, cy + c0 * ri, 0); GL.Vertex3(cx + s0 * ro, cy + c0 * ro, 0); GL.Vertex3(cx + s1 * ro, cy + c1 * ro, 0);
                GL.Vertex3(cx + s0 * ri, cy + c0 * ri, 0); GL.Vertex3(cx + s1 * ro, cy + c1 * ro, 0); GL.Vertex3(cx + s1 * ri, cy + c1 * ri, 0);
                s0 = s1; c0 = c1;
            }
            Close(o);
        }

        /// <summary>Filled rounded rectangle (GL, y up).</summary>
        public static void RoundRect(float x, float y, float w, float h, float r, Color c)
        {
            r = Mathf.Min(r, Mathf.Min(w, h) * 0.5f);
            bool o = Open();
            GL.Color(c);
            Rect6(x + r, y, w - 2 * r, h);
            Rect6(x, y + r, r, h - 2 * r);
            Rect6(x + w - r, y + r, r, h - 2 * r);
            if (r > 0.5f)
            {
                Corner(x + r, y + r, r, 180f);
                Corner(x + w - r, y + r, r, 90f);
                Corner(x + w - r, y + h - r, r, 0f);
                Corner(x + r, y + h - r, r, 270f);
            }
            Close(o);
        }

        private static void Rect6(float x, float y, float w, float h)
        {
            if (w <= 0 || h <= 0) return;
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0);
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
        }

        // Quarter-circle fan directions, worked out once (5 steps per corner, as before).
        private const int CornerSteps = 5;
        private static readonly float[] CornerSin = new float[4 * CornerSteps + 1], CornerCos = new float[4 * CornerSteps + 1];
        private static bool _cornerTable;

        /// <summary>Quarter disc; angle 0 = up, clockwise. fromDeg must be a multiple of 90.</summary>
        private static void Corner(float cx, float cy, float r, float fromDeg)
        {
            if (!_cornerTable)
            {
                _cornerTable = true;
                for (int i = 0; i <= 4 * CornerSteps; i++)
                {
                    float a = 90f * i / CornerSteps * Mathf.Deg2Rad;
                    CornerSin[i] = Mathf.Sin(a);
                    CornerCos[i] = Mathf.Cos(a);
                }
            }
            int first = ((Mathf.RoundToInt(fromDeg / 90f) % 4) + 4) % 4 * CornerSteps;
            for (int i = 0; i < CornerSteps; i++)
            {
                int k0 = (first + i) % (4 * CornerSteps), k1 = (first + i + 1) % (4 * CornerSteps);
                GL.Vertex3(cx, cy, 0);
                GL.Vertex3(cx + CornerSin[k0] * r, cy + CornerCos[k0] * r, 0);
                GL.Vertex3(cx + CornerSin[k1] * r, cy + CornerCos[k1] * r, 0);
            }
        }

        /// <summary>The standard HUD panel: soft shadow, translucent body, faint top edge.</summary>
        public static void PanelRect(float x, float y, float w, float h, float alpha = 1f)
        {
            bool o = Open();
            bool was = _batch;
            _batch = true; // the three parts share the draw this call opened (or the caller's batch)
            RoundRect(x + 1, y - 2, w, h, 8, new Color(0, 0, 0, 0.18f * alpha));
            RoundRect(x, y, w, h, 8, new Color(Panel.r, Panel.g, Panel.b, Panel.a * alpha));
            RoundRect(x + 8, y + h - 1, w - 16, 1, 0, new Color(PanelEdge.r, PanelEdge.g, PanelEdge.b, PanelEdge.a * alpha));
            _batch = was;
            Close(o);
        }

        /// <summary>Thin rounded bar with a track; k in 0..1.</summary>
        public static void Bar(float x, float y, float w, float h, float k, Color fill, float alpha = 1f)
        {
            bool o = Open();
            bool was = _batch;
            _batch = true;
            RoundRect(x, y, w, h, h * 0.5f, new Color(1, 1, 1, 0.12f * alpha));
            if (k > 0.001f) RoundRect(x, y, Mathf.Max(h, w * Mathf.Clamp01(k)), h, h * 0.5f, new Color(fill.r, fill.g, fill.b, fill.a * alpha));
            _batch = was;
            Close(o);
        }

        // ---------------------------------------------------------------- Text (GUI space, y down)

        private static Font _font;
        private static bool _fontTried;
        /// <summary>Set to false to draw text with GUI.Label as before (a switch for comparing, and the fallback when the built-in font is missing).</summary>
        public static bool FastText = true;

        private static bool FontReady()
        {
            if (_fontTried) return _font != null;
            _fontTried = true;
            // The engine's built-in font: the same face GUI.Label draws with when a skin sets none.
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null || !_font.dynamic || _font.material == null)
            {
                _font = null;
                Debug.LogWarning("[SCULPT] HUD: built-in font not available, drawing text with GUI.Label.");
            }
            return _font != null;
        }

        /// <summary>
        /// Shadowed text in GUI space (y down from the top of the screen). Draws on repaint only. The glyphs come from
        /// the built-in font's own texture: one draw for the shadow and the text together, no allocations.
        /// </summary>
        public static void Label(Rect r, string text, int size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = false)
        {
            if (string.IsNullOrEmpty(text) || Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!FastText || !FontReady())
            {
                SlowLabel(r, text, size, c, anchor, bold);
                return;
            }

            FontStyle fs = bold ? FontStyle.Bold : FontStyle.Normal;
            _font.RequestCharactersInTexture(text, size, fs);
            float width = 0;
            for (int i = 0; i < text.Length; i++)
                if (_font.GetCharacterInfo(text[i], out CharacterInfo m, size, fs)) width += m.advance;

            // Vertical metrics are given at the font's own size: scale them to this one.
            float scale = size / (float)Mathf.Max(1, _font.fontSize);
            float line = _font.lineHeight * scale, ascent = _font.ascent * scale;
            int col = (int)anchor % 3, row = (int)anchor / 3; // 0 left/upper, 1 centre/middle, 2 right/lower
            float x = col == 0 ? r.x : col == 1 ? r.x + (r.width - width) * 0.5f : r.xMax - width;
            float top = row == 0 ? r.y : row == 1 ? r.y + (r.height - line) * 0.5f : r.yMax - line;
            x = Mathf.Round(x);
            float baseline = Mathf.Round(top + ascent);
            float screenH = Screen.height;

            _font.material.SetPass(0);
            GL.PushMatrix();
            LoadPixels();
            GL.Begin(GL.QUADS);
            // Shadow first (one pixel right and down), then the text over it.
            for (int pass = 0; pass < 2; pass++)
            {
                GL.Color(pass == 0 ? new Color(0, 0, 0, 0.55f * c.a) : c);
                float pen = x + (pass == 0 ? 1f : 0f), b = baseline + (pass == 0 ? 1f : 0f);
                for (int i = 0; i < text.Length; i++)
                {
                    if (!_font.GetCharacterInfo(text[i], out CharacterInfo g, size, fs)) continue;
                    float x0 = pen + g.minX, x1 = pen + g.maxX;
                    float y0 = screenH - (b - g.minY), y1 = screenH - (b - g.maxY); // glyph bottom and top, y up
                    GL.TexCoord(g.uvBottomLeft); GL.Vertex3(x0, y0, 0);
                    GL.TexCoord(g.uvTopLeft); GL.Vertex3(x0, y1, 0);
                    GL.TexCoord(g.uvTopRight); GL.Vertex3(x1, y1, 0);
                    GL.TexCoord(g.uvBottomRight); GL.Vertex3(x1, y0, 0);
                    pen += g.advance;
                }
            }
            GL.End();
            GL.PopMatrix();
        }

        private static void SlowLabel(Rect r, string text, int size, Color c, TextAnchor anchor, bool bold)
        {
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { richText = false, clipping = TextClipping.Overflow, wordWrap = false };
            _style.fontSize = size;
            _style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            _style.alignment = anchor;
            _style.normal.textColor = new Color(0, 0, 0, 0.55f * c.a);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _style);
            _style.normal.textColor = c;
            GUI.Label(r, text, _style);
        }

        // ---------------------------------------------------------------- Cached strings

        private static readonly string[] NumCache = new string[1024];

        /// <summary>The number as text without building a new string each time (cached for 0..1023).</summary>
        public static string Num(int v)
        {
            if (v < 0 || v >= NumCache.Length) return v.ToString();
            return NumCache[v] ?? (NumCache[v] = v.ToString());
        }

        /// <summary>A GL-space rect (y up) as a GUI-space rect (y down).</summary>
        public static Rect ToGui(float x, float y, float w, float h) => new Rect(x, Screen.height - y - h, w, h);

        public static Color A(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);
    }
}
