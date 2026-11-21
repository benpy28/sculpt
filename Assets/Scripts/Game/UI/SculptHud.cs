using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Sculpting;
using UnityEngine;
using UnityEngine.InputSystem;

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
            if (_player == null || _session == null || _view == null) return;
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

            // What a left click does right now and what it costs, or why it can't happen (live while you aim), as a
            // chip under the crosshair; the sculpt binds as key caps above the hotbar while in sculpt mode.
            string label = null, cost = null;
            Color col = HudDraw.Text;
            bool red = false;
            bool refusedRecently = Time.time - _view.RejectTime < 0.9f;
            if (hand.Mode == HandMode.Disarmed) { label = "DISARMED"; red = true; }
            else if (refusedRecently) { label = Reason(_view.LastReject).ToUpperInvariant(); red = true; }
            else if (_player.Heals && Time.time - _player.HealRejectTime < 0.9f) { label = HealReason(_player.LastHealReject).ToUpperInvariant(); red = true; }
            else if (_player.Heals && hand.InSculptMode) HealChip(net, hand, out label, out cost, out col);
            else if (hand.Mode == HandMode.SculptEnter) label = "SCULPT";
            else if (hand.Mode == HandMode.Sculpt)
            {
                if (!_player.Target.Valid) { label = "AIM AT ROCK WITHIN 6 M"; col = HudDraw.Dim; }
                else if (_player.HasHover)
                {
                    SculptCheck c = _player.HoverCheck;
                    string what = "PUSH " + DirName(_player.HoverAction.Direction).ToUpperInvariant() + (_player.ReinforceHeld ? "  +  REINFORCE" : "");
                    if (c.IsValid) { label = what; cost = Mathf.RoundToInt(c.Cost * 100f / SculptConfig.BarMax) + "%"; }
                    // Only hovering: say why, quietly. Red is for a click that was actually refused.
                    else if (c.Result == SculptResult.DestinationBlocked) { label = "CAN'T PUSH HERE  ·  WHEEL TO RAISE OR SINK"; col = HudDraw.Dim; }
                    else { label = what + "  ·  " + Reason(c.Result).ToUpperInvariant(); col = HudDraw.Dim; }
                }
            }
            else if (hand.Mode == HandMode.SculptExit || hand.Mode == HandMode.Drawing) { label = hand.HeldItem == Item.None ? null : Items.Name(hand.HeldItem).ToUpperInvariant(); col = HudDraw.Dim; }
            if (hand.InSculptMode && hand.SculptLockUntil > net.Tick) label = (label ?? "SCULPT") + "  ·  GUN LOCKED";
            if (red) col = HudDraw.Red;
            string chip = label == null ? null : cost != null ? label + "     " + cost : label;
            float chipW = chip == null ? 0 : 28f + chip.Length * 7.6f;
            string[] caps = hand.InSculptMode ? Caps() : null;

            if (Event.current.type == EventType.Repaint)
            {
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    DrawBar(net, cx, Screen.height - cy, hand);
                    DrawHealRing(net, cx, Screen.height - cy);
                    DrawWarnings(sc);
                    DrawHitMarker(cx, Screen.height - cy);
                    if (chip != null)
                    {
                        float gy = Screen.height - cy - 66;
                        if (red) HudDraw.RoundRect(cx - chipW / 2f - 1, gy - 1, chipW + 2, 28, 9, HudDraw.A(HudDraw.Red, 0.6f));
                        HudDraw.RoundRect(cx - chipW / 2f, gy, chipW, 26, 8, HudDraw.Panel);
                    }
                    if (caps != null) DrawCaps(caps, cx, false);
                }
                finally { GL.PopMatrix(); } // an exception must never leave the matrix stack pushed
            }

            if (chip != null)
            {
                Rect r = new Rect(cx - chipW / 2f, cy + 40, chipW, 26);
                if (cost != null)
                {
                    HudDraw.Label(new Rect(r.x + 14, r.y, r.width - 28, r.height), label, 12, col, TextAnchor.MiddleLeft, true);
                    HudDraw.Label(new Rect(r.x + 14, r.y, r.width - 28, r.height), cost, 12, HudDraw.Dim, TextAnchor.MiddleRight, true);
                }
                else HudDraw.Label(r, label, 12, col, TextAnchor.MiddleCenter, true);
            }
            if (caps != null) DrawCaps(caps, cx, true);
            if (hand.InSculptMode)
            {
                // Bar percentage beside the arc.
                int pct = Mathf.RoundToInt(net.DisplayBar * 100f / SculptConfig.BarMax);
                HudDraw.Label(new Rect(cx + 34, cy + 14, 60, 18), pct + "%", 11, HudDraw.Dim, TextAnchor.MiddleLeft, true);
            }
            if (Time.time - _view.HurtTime < 0.6f)
                HudDraw.Label(new Rect(cx - 60, cy + 70, 120, 20), $"-{_view.LastHurt}", 14, HudDraw.Red, TextAnchor.MiddleCenter, true);
        }

        // ---------------------------------------------------------------- Key caps

        private string[] _caps;
        private float _capsAt = -10f;

        /// <summary>"key|action" pairs from the live binds (refreshed twice a second, so rebinds show).</summary>
        private string[] Caps()
        {
            if (_caps != null && Time.unscaledTime - _capsAt < 0.5f) return _caps;
            _capsAt = Time.unscaledTime;
            var inp = _player.Input;
            if (inp == null) return _caps = System.Array.Empty<string>();
            if (_player.Heals)
                return _caps = new[] { Key(inp.SculptPush) + "|HEAL", Key(inp.SculptReinforce) + " + " + Key(inp.SculptPush) + "|REINFORCE", Key(inp.SculptToggle) + "|GUN" };
            _caps = new[]
            {
                Key(inp.SculptPush) + "|PUSH", Key(inp.SculptPull) + "|PULL", Key(inp.SculptRaise) + "|RAISE", Key(inp.SculptSink) + "|SINK",
                Key(inp.SculptThrow) + "|THROW", Key(inp.SculptReinforce) + "|REINFORCE", Key(inp.SculptToggle) + "|GUN",
            };
            return _caps;
        }

        public static string Key(UnityEngine.InputSystem.InputAction a)
        {
            string s = a.GetBindingDisplayString(0);
            if (string.IsNullOrEmpty(s)) return "—";
            s = s.Replace("Left Button", "LMB").Replace("Right Button", "RMB").Replace("Back", "M4").Replace("Forward", "M5")
                 .Replace("Scroll Up", "WHEEL ▲").Replace("Scroll/Up", "WHEEL ▲").Replace("Scroll Down", "WHEEL ▼").Replace("Scroll/Down", "WHEEL ▼");
            return s.ToUpperInvariant();
        }

        /// <summary>Key caps in a row above the hotbar: GL pass draws the caps, the GUI pass the text.</summary>
        private static void DrawCaps(string[] caps, float cx, bool text)
        {
            const float y = 78f, h = 22f, gap = 14f;
            float total = 0;
            foreach (var c in caps) total += CapWidth(c) + gap;
            total -= gap;
            float x = cx - total / 2f;
            foreach (var c in caps)
            {
                int bar = c.IndexOf('|');
                string key = c.Substring(0, bar), act = c.Substring(bar + 1);
                float kw = 12f + key.Length * 7f, w = CapWidth(c);
                if (!text)
                {
                    HudDraw.RoundRect(x, y, w, h, 6, new Color(0.05f, 0.06f, 0.08f, 0.45f));
                    HudDraw.RoundRect(x + 3, y + 3, kw, h - 6, 4, new Color(1, 1, 1, 0.16f));
                }
                else
                {
                    Rect kr = HudDraw.ToGui(x + 3, y + 3, kw, h - 6);
                    HudDraw.Label(kr, key, 10, HudDraw.Text, TextAnchor.MiddleCenter, true);
                    HudDraw.Label(HudDraw.ToGui(x + kw + 8, y, w - kw - 8, h), act, 10, HudDraw.Dim, TextAnchor.MiddleLeft, true);
                }
                x += w + gap;
            }
        }

        private static float CapWidth(string c)
        {
            int bar = c.IndexOf('|');
            return 12f + bar * 7f + 14f + (c.Length - bar - 1) * 6.6f;
        }

        private void Label(Rect r, string text, Color col, int size)
        {
            _small.fontSize = size;
            _shadow.fontSize = size;
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _shadow);
            _small.normal.textColor = col;
            GUI.Label(r, text, _small);
            _small.normal.textColor = Color.white;
            _small.fontSize = 13;
            _shadow.fontSize = 13;
        }

        private static string DirName(Dir d)
        {
            switch (d)
            {
                case Dir.North: return "north";
                case Dir.South: return "south";
                case Dir.East: return "east";
                case Dir.West: return "west";
                case Dir.Up: return "up";
                default: return "down";
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
                default: return r.ToString();
            }
        }

        // ---------------------------------------------------------------- Bar arc

        private void DrawBar(NetClient net, float cx, float cy, HandState hand)
        {
            float bar = net.DisplayBar / (float)SculptConfig.BarMax;
            const float radius = 26f, thick = 3f;
            const float start = 225f, span = 270f; // gap at the bottom, full bar reads clockwise from bottom-left
            float alpha = hand.InSculptMode ? 1f : (bar < 0.999f ? 0.55f : 0.0f);
            if (alpha <= 0) return;
            Arc(cx, cy, radius, thick, start, span, Tint(BarBack, alpha));
            Arc(cx, cy, radius, thick, start, span * bar, Tint(BarColour, alpha));
            int hoverCost = 0;
            if (_player.Heals && _player.HealTarget.Valid && net.Heals != null)
            {
                HealResult r = _player.HealCheck;
                if (r == HealResult.Ok || r == HealResult.NotEnoughBar || r == HealResult.NotInSculptMode) hoverCost = TileHealing.CostOf(_player.HealKindNow, net.Heals.Config);
            }
            else if (_player.HasHover) hoverCost = _player.HoverCheck.Cost;
            if (hoverCost > 0 && hand.InSculptMode)
            {
                float cost = hoverCost / (float)SculptConfig.BarMax;
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

        /// <summary>Your heal's progress as a second, outer ring that fills over the heal time.</summary>
        private void DrawHealRing(NetClient net, float cx, float cy)
        {
            if (!_player.Heals || !net.TryGetOwnHeal(out HealJob j, out _)) return;
            float p = Mathf.Clamp01((float)((net.Tick + net.Alpha - j.StartTick) / Mathf.Max(1, j.EndTick - j.StartTick)));
            Color c = j.Kind == HealKind.Reinforce ? new Color(0.80f, 0.86f, 1f, 0.95f) : new Color(0.78f, 0.62f, 1f, 0.95f);
            Arc(cx, cy, 33f, 2.5f, 0, 360, Tint(BarBack, 1f));
            Arc(cx, cy, 33f, 2.5f, 0, 360 * p, c);
        }

        /// <summary>What a click would do in heal mode (live while you aim), its cost, or why not (quietly).</summary>
        private void HealChip(NetClient net, HandState hand, out string label, out string cost, out Color col)
        {
            label = null;
            cost = null;
            col = HudDraw.Text;
            bool reinforce = _player.HealKindNow == HealKind.Reinforce;
            string what = reinforce ? "REINFORCE" : "HEAL";
            if (net.TryGetOwnHeal(out HealJob mine, out _) && (!_player.HealTarget.Valid || _player.HealTarget.Tile == mine.Tile))
            {
                label = mine.Kind == HealKind.Reinforce ? "REINFORCING" : "HEALING";
                return;
            }
            if (hand.Mode == HandMode.SculptEnter) { label = "SCULPT"; return; }
            if (!_player.HealTarget.Valid) { label = "AIM AT A CRACKED OR BROKEN WALL WITHIN " + Mathf.RoundToInt((float)net.Walls.Config.HealRange) + " M"; col = HudDraw.Dim; return; }
            HealResult r = _player.HealCheck;
            if (r == HealResult.Ok)
            {
                label = what;
                cost = Mathf.RoundToInt(TileHealing.CostOf(_player.HealKindNow, net.Heals.Config) * 100f / SculptConfig.BarMax) + "%";
                return;
            }
            label = (r == HealResult.NothingToHeal ? "NOTHING TO HEAL  ·  HOLD " + Key(_player.Input.SculptReinforce) + " TO REINFORCE" : what + "  ·  " + HealReason(r).ToUpperInvariant());
            col = HudDraw.Dim;
        }

        private static string HealReason(HealResult r)
        {
            switch (r)
            {
                case HealResult.NoTile: return "nothing to heal there";
                case HealResult.NothingToHeal: return "nothing to heal";
                case HealResult.AlreadyReinforced: return "already reinforced";
                case HealResult.OutOfRange: return "out of range";
                case HealResult.NoLineOfSight: return "no line of sight";
                case HealResult.NotEnoughBar: return "not enough bar";
                case HealResult.TileBusy: return "someone is already on it";
                case HealResult.NotInSculptMode: return "not ready";
                case HealResult.PlayerInside: return "someone is in the way";
                case HealResult.LeftRange: return "too far away";
                case HealResult.Stopped: return "stopped";
                case HealResult.Replaced: return "switched tile";
                default: return r.ToString();
            }
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
