using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Sculpting;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Sculpt HUD (IMGUI + GL, placeholder art): the bar as a thin arc around the crosshair with the hovered action's
    /// cost flashing, red feedback for refused confirms (with the reason), the targeted warning (purple pulse at the
    /// bottom edge and an arrow toward the sculptor; a throw lane warning in the thrower's team colour, never purple),
    /// hit marker and hurt flash for thrown pieces, and the hand state (sculpt mode, entering, exiting, disarmed).
    /// </summary>
    public sealed class SculptHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private NetSession _session;
        private SculptView _view;
        private Camera _camera;
        private Material _gl;
        private GUIStyle _small, _shadow;

        private static readonly Color Purple = new Color(0.62f, 0.33f, 1.0f); // "sculptable" / sculpt warning only
        private static readonly Color BarColour = new Color(0.95f, 0.95f, 0.95f, 0.9f);
        private static readonly Color BarBack = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color Red = new Color(1f, 0.22f, 0.18f, 1f);

        public void Init(LocalPlayer player, NetSession session, SculptView view, Camera camera)
        {
            _player = player;
            _session = session;
            _view = view;
            _camera = camera;
            Shader s = Shader.Find("Hidden/Internal-Colored");
            _gl = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void OnGUI()
        {
            if (_player == null || _session == null) return;
            NetClient net = _session.Client;
            SculptClient sc = net.Sculpt;
            if (sc == null || !net.Joined) return;
            if (_small == null)
            {
                _small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
                _small.normal.textColor = Color.white;
                _shadow = new GUIStyle(_small);
                _shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }

            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            HandState hand = _player.State.Hand;

            if (Event.current.type == EventType.Repaint)
            {
                _gl.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();
                DrawBar(sc, cx, Screen.height - cy, hand);
                DrawWarnings(sc);
                DrawHitMarker(cx, Screen.height - cy);
                GL.PopMatrix();
            }

            // Text under the crosshair.
            string label = null;
            Color col = Color.white;
            if (hand.Mode == HandMode.Disarmed) { label = "DISARMED"; col = Red; }
            else if (Time.time - _view.RejectTime < 0.7f) { label = Reason(_view.LastReject); col = Red; }
            else if (hand.Mode == HandMode.SculptEnter) label = "sculpt...";
            else if (hand.Mode == HandMode.Sculpt) label = _player.ReinforceHeld ? "SCULPT  reinforce" : "SCULPT";
            else if (hand.Mode == HandMode.SculptExit || hand.Mode == HandMode.Drawing) label = WeaponName(hand.Held) + "...";
            if (hand.InSculptMode && hand.SculptLockUntil > net.Tick) label = (label ?? "SCULPT") + "  (gun locked)";
            if (label != null)
            {
                var r = new Rect(cx - 160, cy + 38, 320, 20);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, _shadow);
                _small.normal.textColor = col;
                GUI.Label(r, label, _small);
                _small.normal.textColor = Color.white;
            }
            if (Time.time - _view.HurtTime < 0.6f)
            {
                string hurt = $"-{_view.LastHurt}";
                GUI.Label(new Rect(cx - 60, cy + 58, 120, 20), hurt, _shadow);
                _small.normal.textColor = Red;
                GUI.Label(new Rect(cx - 61, cy + 57, 120, 20), hurt, _small);
                _small.normal.textColor = Color.white;
            }
        }

        private static string WeaponName(Weapon w) => w == Weapon.Pump ? "Pump" : w == Weapon.Smg ? "SMG" : w == Weapon.Ar ? "AR" : "Pistol";

        private static string Reason(SculptResult r)
        {
            switch (r)
            {
                case SculptResult.DestinationBlocked: return "blocked";
                case SculptResult.OutOfHeightRange: return "height limit";
                case SculptResult.OutOfRange: return "out of range";
                case SculptResult.NoLineOfSight: return "no line of sight";
                case SculptResult.Locked: return "locked";
                case SculptResult.Busy: return "busy";
                case SculptResult.NotEnoughBar: return "not enough bar";
                case SculptResult.PlayerBlocked: return "player in the way";
                case SculptResult.CannotThrowReinforced: return "reinforced: cannot throw";
                case SculptResult.NotSculptable: return "not sculptable";
                case SculptResult.NotInSculptMode: return "not ready";
                case SculptResult.Disturbed: return "disturbed";
                default: return "refused";
            }
        }

        // ---------------------------------------------------------------- Bar arc

        private void DrawBar(SculptClient sc, float cx, float cy, HandState hand)
        {
            float bar = sc.DisplayBar / (float)SculptConfig.BarMax;
            const float radius = 26f, thick = 3f;
            const float start = 225f, span = 270f; // gap at the bottom, full bar reads clockwise from bottom-left
            float alpha = hand.InSculptMode ? 1f : (bar < 0.999f ? 0.55f : 0.0f);
            if (alpha <= 0) return;
            Arc(cx, cy, radius, thick, start, span, Tint(BarBack, alpha));
            Arc(cx, cy, radius, thick, start, span * bar, Tint(BarColour, alpha));
            if (_player.HasHover && hand.InSculptMode)
            {
                SculptCheck c = _player.HoverCheck;
                float cost = c.Cost / (float)SculptConfig.BarMax;
                if (cost > 0)
                {
                    bool afford = cost <= bar + 1e-4f;
                    float flash = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin(Time.time * 9f));
                    if (afford) Arc(cx, cy, radius + 0.5f, thick + 2f, start + span * (bar - cost), span * cost, Tint(new Color(1f, 1f, 1f, 1f), flash));
                    else Arc(cx, cy, radius + 0.5f, thick + 2f, start + span * bar, span * Mathf.Min(cost - bar, 1f - bar), Tint(Red, flash));
                }
            }
            if (Time.time - _view.RejectTime < 0.25f) Arc(cx, cy, radius + 6f, 2f, 0, 360, Tint(Red, 1f - (Time.time - _view.RejectTime) / 0.25f));
        }

        private static Color Tint(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        /// <summary>Arc in pixel space; angles in degrees, 0 = up, clockwise.</summary>
        private static void Arc(float cx, float cy, float r, float thick, float fromDeg, float spanDeg, Color c)
        {
            if (spanDeg <= 0.01f) return;
            int steps = Mathf.Max(2, Mathf.CeilToInt(spanDeg / 4f));
            GL.Begin(GL.QUADS);
            GL.Color(c);
            for (int i = 0; i < steps; i++)
            {
                float a0 = (fromDeg + spanDeg * i / steps) * Mathf.Deg2Rad;
                float a1 = (fromDeg + spanDeg * (i + 1) / steps) * Mathf.Deg2Rad;
                Vector2 d0 = new Vector2(Mathf.Sin(a0), Mathf.Cos(a0)), d1 = new Vector2(Mathf.Sin(a1), Mathf.Cos(a1));
                float ri = r - thick * 0.5f, ro = r + thick * 0.5f;
                GL.Vertex3(cx + d0.x * ri, cy + d0.y * ri, 0);
                GL.Vertex3(cx + d0.x * ro, cy + d0.y * ro, 0);
                GL.Vertex3(cx + d1.x * ro, cy + d1.y * ro, 0);
                GL.Vertex3(cx + d1.x * ri, cy + d1.y * ri, 0);
            }
            GL.End();
        }

        // ---------------------------------------------------------------- Warnings

        private void DrawWarnings(SculptClient sc)
        {
            if (_view.Warnings.Count == 0) return;
            float w = Screen.width, h = Screen.height;
            foreach (var warn in _view.Warnings)
            {
                float age = Time.time - warn.Time;
                float k = Mathf.Clamp01(1f - age / 1.5f);
                Color c = warn.Throw ? TeamOf(sc, warn.Sculptor) : Purple;
                float pulse = (0.5f + 0.5f * Mathf.Sin(age * 18f)) * k;
                if (!warn.Throw)
                {
                    // Purple pulse along the bottom edge (you stand on or touch the piece being sculpted).
                    GL.Begin(GL.QUADS);
                    GL.Color(Tint(c, 0.55f * pulse));
                    GL.Vertex3(0, 0, 0); GL.Vertex3(w, 0, 0);
                    GL.Color(Tint(c, 0f));
                    GL.Vertex3(w, h * 0.09f, 0); GL.Vertex3(0, h * 0.09f, 0);
                    GL.End();
                }
                // Arrow toward the sculptor, on a ring around the crosshair.
                PlayerBody b = sc.BodyOf(warn.Sculptor);
                if (b == null || _camera == null) continue;
                Vector3 to = b.Position.ToUnity() - _camera.transform.position;
                Vector3 local = _camera.transform.InverseTransformDirection(to);
                float ang = Mathf.Atan2(local.x, local.z); // 0 = straight ahead, clockwise positive
                float ring = 120f;
                Vector2 centre = new Vector2(w / 2f, h / 2f);
                Vector2 dir = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                Vector2 tip = centre + dir * (ring + 18f);
                Vector2 baseC = centre + dir * ring;
                Vector2 side = new Vector2(dir.y, -dir.x) * 14f;
                GL.Begin(GL.TRIANGLES);
                GL.Color(Tint(c, 0.4f + 0.6f * k));
                GL.Vertex3(tip.x, tip.y, 0);
                GL.Vertex3(baseC.x + side.x, baseC.y + side.y, 0);
                GL.Vertex3(baseC.x - side.x, baseC.y - side.y, 0);
                GL.End();
            }
        }

        private static Color TeamOf(SculptClient sc, int playerId)
        {
            PlayerBody b = sc.BodyOf(playerId);
            int team = b?.Team ?? 1;
            Color c = SculptView.Team[Mathf.Abs(team - 1) % SculptView.Team.Length];
            return new Color(c.r, c.g, c.b, 1f);
        }

        private void DrawHitMarker(float cx, float cy)
        {
            float age = Time.time - _view.HitMarkerTime;
            if (age > 0.25f) return;
            float a = 1f - age / 0.25f;
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1, 1, 1, a));
            for (int k = 0; k < 4; k++)
            {
                float ang = (45f + 90f * k) * Mathf.Deg2Rad;
                Vector2 d = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                Vector2 n = new Vector2(d.y, -d.x) * 1.2f;
                Vector2 p0 = new Vector2(cx, cy) + d * 9f, p1 = new Vector2(cx, cy) + d * 17f;
                GL.Vertex3(p0.x + n.x, p0.y + n.y, 0); GL.Vertex3(p1.x + n.x, p1.y + n.y, 0);
                GL.Vertex3(p1.x - n.x, p1.y - n.y, 0); GL.Vertex3(p0.x - n.x, p0.y - n.y, 0);
            }
            GL.End();
        }
    }
}
