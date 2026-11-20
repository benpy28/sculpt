using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Shared look for the game HUD: dark translucent rounded panels, thin bars and clean shadowed text. GL calls work in
    /// pixel space with y up (call inside GL.LoadPixelMatrix); text uses GUI space (y down). No purple (sculptable only).
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

        /// <summary>Filled rounded rectangle (GL, y up).</summary>
        public static void RoundRect(float x, float y, float w, float h, float r, Color c)
        {
            r = Mathf.Min(r, Mathf.Min(w, h) * 0.5f);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            Quad(x + r, y, w - 2 * r, h);
            Quad(x, y + r, r, h - 2 * r);
            Quad(x + w - r, y + r, r, h - 2 * r);
            GL.End();
            if (r <= 0.5f) return;
            GL.Begin(GL.TRIANGLES);
            GL.Color(c);
            Corner(x + r, y + r, r, 180f);
            Corner(x + w - r, y + r, r, 90f);
            Corner(x + w - r, y + h - r, r, 0f);
            Corner(x + r, y + h - r, r, 270f);
            GL.End();
        }

        private static void Quad(float x, float y, float w, float h)
        {
            if (w <= 0 || h <= 0) return;
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
        }

        /// <summary>Quarter disc; angle 0 = up, clockwise.</summary>
        private static void Corner(float cx, float cy, float r, float fromDeg)
        {
            const int steps = 5;
            for (int i = 0; i < steps; i++)
            {
                float a0 = (fromDeg + 90f * i / steps) * Mathf.Deg2Rad, a1 = (fromDeg + 90f * (i + 1) / steps) * Mathf.Deg2Rad;
                GL.Vertex3(cx, cy, 0);
                GL.Vertex3(cx + Mathf.Sin(a0) * r, cy + Mathf.Cos(a0) * r, 0);
                GL.Vertex3(cx + Mathf.Sin(a1) * r, cy + Mathf.Cos(a1) * r, 0);
            }
        }

        /// <summary>The standard HUD panel: soft shadow, translucent body, faint top edge.</summary>
        public static void PanelRect(float x, float y, float w, float h, float alpha = 1f)
        {
            RoundRect(x + 1, y - 2, w, h, 8, new Color(0, 0, 0, 0.18f * alpha));
            RoundRect(x, y, w, h, 8, new Color(Panel.r, Panel.g, Panel.b, Panel.a * alpha));
            RoundRect(x + 8, y + h - 1, w - 16, 1, 0, new Color(PanelEdge.r, PanelEdge.g, PanelEdge.b, PanelEdge.a * alpha));
        }

        /// <summary>Thin rounded bar with a track; k in 0..1.</summary>
        public static void Bar(float x, float y, float w, float h, float k, Color fill, float alpha = 1f)
        {
            RoundRect(x, y, w, h, h * 0.5f, new Color(1, 1, 1, 0.12f * alpha));
            if (k > 0.001f) RoundRect(x, y, Mathf.Max(h, w * Mathf.Clamp01(k)), h, h * 0.5f, new Color(fill.r, fill.g, fill.b, fill.a * alpha));
        }

        /// <summary>Shadowed text in GUI space (y down from the top of the screen).</summary>
        public static void Label(Rect r, string text, int size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = false)
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

        /// <summary>A GL-space rect (y up) as a GUI-space rect (y down).</summary>
        public static Rect ToGui(float x, float y, float w, float h) => new Rect(x, Screen.height - y - h, w, h);

        public static Color A(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);
    }
}
