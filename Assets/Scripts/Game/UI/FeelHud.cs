using Sculpt.Core;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The compass strip at the top of the HUD (N/E/S/W plus intercardinals and degree ticks, heading under the
    /// marker) and the subtle speed lines at the screen edge above sprint speed. Speed lines stay in the outer ring
    /// of the screen and are faint, so they never cover the middle where fights happen. No purple.
    /// </summary>
    public sealed class FeelHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private ClientSettings _settings;
        private Material _gl;
        private GUIStyle _letter, _small;
        private float _speedLines;

        private const int Lines = 30;
        private readonly float[] _angle = new float[Lines];
        private readonly float[] _phase = new float[Lines];
        private readonly float[] _len = new float[Lines];
        private readonly float[] _rate = new float[Lines];

        /// <summary>How wide the compass strip is in degrees of heading.</summary>
        public const float CompassSpan = 120f;

        public void Init(LocalPlayer player, ClientSettings settings)
        {
            _player = player;
            _settings = settings;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            var rng = new System.Random(77);
            for (int i = 0; i < Lines; i++)
            {
                _angle[i] = (i + (float)rng.NextDouble() * 0.7f) / Lines * Mathf.PI * 2f;
                _phase[i] = (float)rng.NextDouble();
                _len[i] = 0.08f + 0.12f * (float)rng.NextDouble();
                _rate[i] = 1.6f + 1.4f * (float)rng.NextDouble();
            }
        }

        private void Update()
        {
            if (_player == null) return;
            // Above sprint speed only (slides, slide-jumps, ramp launches, long falls).
            MovementConfig c = _player.Config;
            Vec3d v = _player.State.Velocity;
            float speed = (float)System.Math.Sqrt(v.X * v.X + v.Z * v.Z + 0.35 * v.Y * v.Y);
            float target = _settings.SpeedLines && !_player.State.Dead ? Mathf.InverseLerp((float)c.SprintSpeed + 0.5f, (float)c.SlideMaxSpeed, speed) : 0f;
            _speedLines = Mathf.MoveTowards(_speedLines, target, Time.unscaledDeltaTime * 4f);
            for (int i = 0; i < Lines; i++) _phase[i] = Mathf.Repeat(_phase[i] + Time.unscaledDeltaTime * _rate[i] * (0.5f + _speedLines), 1f);
        }

        private void OnGUI()
        {
            if (_player == null || !_player.Net.Joined) return;
            if (_letter == null)
            {
                _letter = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _small = new GUIStyle(_letter) { fontSize = 11, fontStyle = FontStyle.Normal };
            }
            float w = Screen.width, h = Screen.height;
            float stripW = Mathf.Min(520f, w * 0.42f), stripH = 30f, top = 10f;
            float cx = w / 2f;
            float yaw = Mathf.Repeat(_player.Yaw, 360f);

            if (Event.current.type == EventType.Repaint)
            {
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    if (_speedLines > 0.01f) DrawSpeedLines(w, h);
                    DrawCompassTicks(cx, h - top, stripW, stripH, yaw);
                }
                finally { GL.PopMatrix(); } // an exception must never leave the matrix stack pushed
            }

            // Letters and heading.
            for (int deg = 0; deg < 360; deg += 45)
            {
                float off = Mathf.DeltaAngle(yaw, deg);
                if (Mathf.Abs(off) > CompassSpan / 2f - 4f) continue;
                float x = cx + off / (CompassSpan / 2f) * (stripW / 2f);
                bool cardinal = deg % 90 == 0;
                string label = Name(deg);
                float fade = 1f - Mathf.Pow(Mathf.Abs(off) / (CompassSpan / 2f), 3f);
                Color col = deg == 0 ? new Color(1f, 0.85f, 0.35f, fade) : new Color(1, 1, 1, (cardinal ? 1f : 0.7f) * fade);
                Shadowed(new Rect(x - 20, top + 2, 40, 20), label, cardinal ? _letter : _small, col);
            }
            Shadowed(new Rect(cx - 30, top + stripH - 2, 60, 18), Mathf.RoundToInt(yaw) % 360 + "°", _small, new Color(1, 1, 1, 0.85f));
        }

        private static string Name(int deg)
        {
            switch (deg)
            {
                case 0: return "N";
                case 45: return "NE";
                case 90: return "E";
                case 135: return "SE";
                case 180: return "S";
                case 225: return "SW";
                case 270: return "W";
                default: return "NW";
            }
        }

        private void Shadowed(Rect r, string t, GUIStyle st, Color c)
        {
            st.normal.textColor = new Color(0, 0, 0, 0.7f * c.a);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), t, st);
            st.normal.textColor = c;
            GUI.Label(r, t, st);
        }

        /// <summary>GL (pixel coordinates, y up). yTop is the strip's top edge.</summary>
        private void DrawCompassTicks(float cx, float yTop, float stripW, float stripH, float yaw)
        {
            GL.Begin(GL.QUADS);
            // Faint backing band.
            Quad(cx - stripW / 2f, yTop - stripH, stripW, stripH, new Color(0, 0, 0, 0.18f));
            for (int deg = 0; deg < 360; deg += 5)
            {
                float off = Mathf.DeltaAngle(yaw, deg);
                if (Mathf.Abs(off) > CompassSpan / 2f) continue;
                if (deg % 45 == 0) continue; // the letters stand there
                float x = cx + off / (CompassSpan / 2f) * (stripW / 2f);
                float fade = 1f - Mathf.Pow(Mathf.Abs(off) / (CompassSpan / 2f), 3f);
                bool major = deg % 15 == 0;
                float len = major ? 8f : 4f;
                Quad(x - 0.75f, yTop - 4f - len, 1.5f, len, new Color(1, 1, 1, (major ? 0.7f : 0.4f) * fade));
            }
            // Centre marker.
            Quad(cx - 1f, yTop - stripH - 2f, 2f, 8f, new Color(1, 1, 1, 0.95f));
            GL.End();
        }

        private void DrawSpeedLines(float w, float h)
        {
            float cx = w / 2f, cy = h / 2f;
            float r = Mathf.Sqrt(cx * cx + cy * cy);
            GL.Begin(GL.QUADS);
            for (int i = 0; i < Lines; i++)
            {
                Vector2 dir = new Vector2(Mathf.Cos(_angle[i]), Mathf.Sin(_angle[i]));
                // Each streak travels outward from 62% to 100% of the half-diagonal, fading in and out.
                float p = _phase[i];
                float r0 = r * (0.62f + 0.38f * p);
                float r1 = r0 + r * _len[i] * (0.6f + 0.6f * _speedLines);
                float a = 0.16f * _speedLines * Mathf.Sin(p * Mathf.PI);
                Vector2 n = new Vector2(-dir.y, dir.x) * (1.2f + 1.3f * _speedLines);
                Vector2 p0 = new Vector2(cx, cy) + dir * r0, p1 = new Vector2(cx, cy) + dir * r1;
                GL.Color(new Color(1, 1, 1, a));
                GL.Vertex3(p0.x - n.x, p0.y - n.y, 0);
                GL.Vertex3(p0.x + n.x, p0.y + n.y, 0);
                GL.Color(new Color(1, 1, 1, 0));
                GL.Vertex3(p1.x + n.x * 0.3f, p1.y + n.y * 0.3f, 0);
                GL.Vertex3(p1.x - n.x * 0.3f, p1.y - n.y * 0.3f, 0);
            }
            GL.End();
        }

        private static void Quad(float x, float y, float w, float h, Color c)
        {
            GL.Color(c);
            GL.Vertex3(x, y, 0);
            GL.Vertex3(x + w, y, 0);
            GL.Vertex3(x + w, y + h, 0);
            GL.Vertex3(x, y + h, 0);
        }
    }
}
