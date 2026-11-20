using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Weapons;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Combat HUD (IMGUI + GL, placeholder art until M6): a crosshair whose gap is the real spread cone of your next shot
    /// (the pump shows its pellet ring), server-confirmed hit markers (white body, amber head, red kill), red damage
    /// arrows toward whoever hit you, health, the 1-5 hotbar with the pump cycle, kill feed and the death screen.
    /// No purple anywhere (purple means sculptable).
    /// </summary>
    public sealed class CombatHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private NetSession _session;
        private WeaponView _view;
        private Camera _camera;
        private Material _gl;
        private GUIStyle _label, _big, _shadow;

        private static readonly Color Red = new Color(1f, 0.22f, 0.18f, 1f);
        private static readonly Color Amber = new Color(1f, 0.72f, 0.2f, 1f);
        private static readonly string[] Names = { "Pump", "SMG", "AR", "Pistol" };

        public void Init(LocalPlayer player, NetSession session, WeaponView view, Camera camera)
        {
            _player = player;
            _session = session;
            _view = view;
            _camera = camera;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void OnGUI()
        {
            if (_player == null) return;
            NetClient net = _session.Client;
            if (!net.Joined) return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter };
                _label.normal.textColor = Color.white;
                _big = new GUIStyle(_label) { fontSize = 30, fontStyle = FontStyle.Bold };
                _shadow = new GUIStyle(_label);
            }

            MotorState s = _player.State;
            WeaponsConfig cfg = _player.Config.Weapons;
            float w = Screen.width, h = Screen.height;

            if (Event.current.type == EventType.Repaint)
            {
                _gl.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();
                if (!s.Dead) DrawCrosshair(s, cfg, w / 2f, h / 2f);
                DrawHitMarker(w / 2f, h / 2f);
                DrawHurt(w, h);
                DrawHealthBar(s, cfg);
                DrawHotbar(s, net.Tick, w);
                GL.PopMatrix();
            }

            // Health number.
            int hp = s.Health(cfg);
            Text(new Rect(24, h - 78, 120, 40), hp.ToString(), _big, hp <= cfg.MaxHealth / 3 ? Red : Color.white, TextAnchor.MiddleLeft);
            // Hotbar labels.
            for (int i = 0; i < 5; i++)
            {
                Rect r = SlotRect(i, w, h);
                string n = i < 4 ? $"{i + 1}  {Names[i]}" : "5  —";
                Text(new Rect(r.x, h - r.yMax, r.width, r.height), n, _label, i < 4 && i == (int)s.Hand.Held ? Color.white : new Color(1, 1, 1, 0.55f), TextAnchor.MiddleCenter);
            }
            // Damage dealt by the last hit, under the crosshair.
            if (Time.time - _view.HitTime < 0.5f)
                Text(new Rect(w / 2f + 18, h / 2f + 10, 80, 20), _view.LastHitDamage.ToString(), _label,
                     Time.time - _view.HeadHitTime < 0.5f ? Amber : Color.white, TextAnchor.MiddleLeft);

            DrawFeed(net, w);

            if (s.Dead)
            {
                float left = Mathf.Max(0, (s.RespawnTick - net.Tick) / (float)SimTime.TicksPerSecond);
                string by = _view.KilledBy != 0 && _view.KilledBy != net.PlayerId ? $"ELIMINATED by P{_view.KilledBy}  [{SourceName(_view.KilledWith)}]" : "ELIMINATED";
                Text(new Rect(0, h * 0.38f, w, 40), by, _big, Red, TextAnchor.MiddleCenter);
                Text(new Rect(0, h * 0.38f + 44, w, 24), $"respawning in {left:0.0} s", _label, Color.white, TextAnchor.MiddleCenter);
            }
        }

        private void Text(Rect r, string t, GUIStyle st, Color c, TextAnchor a)
        {
            st.alignment = a;
            Color old = st.normal.textColor;
            st.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), t, st);
            st.normal.textColor = c;
            GUI.Label(r, t, st);
            st.normal.textColor = old;
        }

        private static string SourceName(DamageSource s) =>
            s == DamageSource.Throw ? "Throw" : s == DamageSource.Fall ? "Fall" : Names[(int)s];

        // ---------------------------------------------------------------- Crosshair

        /// <summary>Pixels on screen for an angle off the view centre (vertical FOV based, same in both axes).</summary>
        private float Px(double degrees)
        {
            float half = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            return Mathf.Tan((float)degrees * Mathf.Deg2Rad) / Mathf.Tan(half) * (Screen.height * 0.5f);
        }

        private void DrawCrosshair(in MotorState s, WeaponsConfig cfg, float cx, float cy)
        {
            HandState hand = s.Hand;
            if (hand.InSculptMode)
            {
                Cross(cx, cy, 3f, 5f, 1.5f, new Color(1, 1, 1, 0.9f));
                return;
            }
            WeaponStats st = cfg.Of(hand.Held);
            double spread = WeaponRules.Spread(s, _player.Config, st);
            bool ready = hand.CanFire && s.CanShoot;
            Color c = ready ? new Color(1, 1, 1, 0.95f) : new Color(1, 1, 1, 0.4f);
            if (st.Pellets > 1)
            {
                // Pump: the pellet ring at its real size, plus a centre dot.
                float r = Px(st.PatternSpread * 1.06 + spread) + 2f; // the outer pellets
                Ring(cx, cy, r, 1.5f, c);
                Dot(cx, cy, 1.5f, c);
                // Cycle: the ring fills back in as the pump comes ready.
                long next = s.Weapon.NextPump, now = _session.Client.Tick * 1000L;
                float k = Mathf.Clamp01(1f - (next - now) / (float)st.FireIntervalMilliTicks);
                if (k < 1f) Arc(cx, cy, r + 5f, 2f, 0, 360 * k, new Color(1, 1, 1, 0.6f));
                return;
            }
            float gap = Mathf.Max(3f, Px(spread));
            Cross(cx, cy, gap, 7f, 2f, c);
            Dot(cx, cy, 1f, c);
        }

        private static void Cross(float cx, float cy, float gap, float len, float th, Color c)
        {
            var outline = new Color(0, 0, 0, c.a * 0.6f);
            Rect(cx - gap - len - 1, cy - th / 2 - 1, len + 2, th + 2, outline);
            Rect(cx + gap - 1, cy - th / 2 - 1, len + 2, th + 2, outline);
            Rect(cx - th / 2 - 1, cy - gap - len - 1, th + 2, len + 2, outline);
            Rect(cx - th / 2 - 1, cy + gap - 1, th + 2, len + 2, outline);
            Rect(cx - gap - len, cy - th / 2, len, th, c);
            Rect(cx + gap, cy - th / 2, len, th, c);
            Rect(cx - th / 2, cy - gap - len, th, len, c);
            Rect(cx - th / 2, cy + gap, th, len, c);
        }

        private static void Dot(float cx, float cy, float r, Color c) => Rect(cx - r, cy - r, 2 * r, 2 * r, c);

        private static void Rect(float x, float y, float w, float h, Color c)
        {
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0); GL.Vertex3(x + w, y + h, 0); GL.Vertex3(x, y + h, 0);
            GL.End();
        }

        private static void Ring(float cx, float cy, float r, float th, Color c) => Arc(cx, cy, r, th, 0, 360, c);

        private static void Arc(float cx, float cy, float r, float thick, float fromDeg, float spanDeg, Color c)
        {
            if (spanDeg <= 0.01f) return;
            int steps = Mathf.Max(2, Mathf.CeilToInt(spanDeg / 6f));
            GL.Begin(GL.QUADS);
            GL.Color(c);
            for (int i = 0; i < steps; i++)
            {
                float a0 = (fromDeg + spanDeg * i / steps) * Mathf.Deg2Rad, a1 = (fromDeg + spanDeg * (i + 1) / steps) * Mathf.Deg2Rad;
                Vector2 d0 = new Vector2(Mathf.Sin(a0), Mathf.Cos(a0)), d1 = new Vector2(Mathf.Sin(a1), Mathf.Cos(a1));
                float ri = r - thick * 0.5f, ro = r + thick * 0.5f;
                GL.Vertex3(cx + d0.x * ri, cy + d0.y * ri, 0);
                GL.Vertex3(cx + d0.x * ro, cy + d0.y * ro, 0);
                GL.Vertex3(cx + d1.x * ro, cy + d1.y * ro, 0);
                GL.Vertex3(cx + d1.x * ri, cy + d1.y * ri, 0);
            }
            GL.End();
        }

        // ---------------------------------------------------------------- Hits

        private void DrawHitMarker(float cx, float cy)
        {
            float now = Time.time;
            bool kill = now - _view.KillMarkTime < 0.45f;
            float age = kill ? now - _view.KillMarkTime : now - _view.HitTime;
            float life = kill ? 0.45f : 0.22f;
            if (age > life) return;
            float a = 1f - age / life;
            bool head = now - _view.HeadHitTime < 0.22f;
            Color c = kill ? Red : head ? Amber : Color.white;
            float inner = kill ? 10f : 8f, outer = kill ? 22f : head ? 18f : 15f, th = kill ? 2.2f : 1.5f;
            GL.Begin(GL.QUADS);
            GL.Color(new Color(c.r, c.g, c.b, a));
            for (int k = 0; k < 4; k++)
            {
                float ang = (45f + 90f * k) * Mathf.Deg2Rad;
                Vector2 d = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                Vector2 n = new Vector2(d.y, -d.x) * th;
                Vector2 p0 = new Vector2(cx, cy) + d * inner, p1 = new Vector2(cx, cy) + d * outer;
                GL.Vertex3(p0.x + n.x, p0.y + n.y, 0); GL.Vertex3(p1.x + n.x, p1.y + n.y, 0);
                GL.Vertex3(p1.x - n.x, p1.y - n.y, 0); GL.Vertex3(p0.x - n.x, p0.y - n.y, 0);
            }
            GL.End();
        }

        private void DrawHurt(float w, float h)
        {
            float age = Time.time - _view.HurtTime;
            if (age < 0.35f)
            {
                // Red edge vignette, stronger for bigger hits.
                float a = (1f - age / 0.35f) * Mathf.Clamp01(0.25f + _view.LastHurt / 80f) * 0.55f;
                float e = h * 0.12f;
                Edge(0, 0, w, e, a, true); Edge(0, h - e, w, e, a, false);
            }
            foreach (var arrow in _view.Arrows)
            {
                float k = Mathf.Clamp01(1f - (Time.time - arrow.Time) / 1.4f);
                Vector3 local = _camera.transform.InverseTransformDirection(arrow.From - _camera.transform.position);
                float ang = Mathf.Atan2(local.x, local.z);
                Vector2 centre = new Vector2(w / 2f, h / 2f);
                Vector2 dir = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                float ring = 95f;
                float spanDeg = Mathf.Clamp(20f + arrow.Damage * 0.4f, 20f, 60f);
                Arc(centre.x, centre.y, ring, 5f, ang * Mathf.Rad2Deg - spanDeg / 2, spanDeg, new Color(Red.r, Red.g, Red.b, 0.85f * k));
                Vector2 tip = centre + dir * (ring + 16f), b = centre + dir * (ring + 4f), side = new Vector2(dir.y, -dir.x) * 9f;
                GL.Begin(GL.TRIANGLES);
                GL.Color(new Color(Red.r, Red.g, Red.b, 0.9f * k));
                GL.Vertex3(tip.x, tip.y, 0); GL.Vertex3(b.x + side.x, b.y + side.y, 0); GL.Vertex3(b.x - side.x, b.y - side.y, 0);
                GL.End();
            }
        }

        private static void Edge(float x, float y, float w, float hgt, float a, bool bottom)
        {
            GL.Begin(GL.QUADS);
            Color solid = new Color(Red.r, Red.g * 0.5f, Red.b * 0.5f, a), clear = new Color(Red.r, 0, 0, 0);
            GL.Color(bottom ? solid : clear); GL.Vertex3(x, y, 0); GL.Vertex3(x + w, y, 0);
            GL.Color(bottom ? clear : solid); GL.Vertex3(x + w, y + hgt, 0); GL.Vertex3(x, y + hgt, 0);
            GL.End();
        }

        // ---------------------------------------------------------------- Health, hotbar, feed

        private void DrawHealthBar(in MotorState s, WeaponsConfig cfg)
        {
            float k = Mathf.Clamp01(s.Health(cfg) / (float)cfg.MaxHealth);
            const float x = 24, y = 26, bw = 220, bh = 8;
            Rect(x - 1, y - 1, bw + 2, bh + 2, new Color(0, 0, 0, 0.55f));
            Color c = k <= 1f / 3f ? Red : new Color(0.92f, 0.94f, 0.95f, 0.95f);
            if (Time.time - _view.HurtTime < 0.15f) c = Color.Lerp(c, Red, 0.7f);
            Rect(x, y, bw * k, bh, c);
        }

        private static UnityEngine.Rect SlotRect(int i, float w, float h)
        {
            const float sw = 96, sh = 30, gap = 6;
            float total = 5 * sw + 4 * gap;
            float x = (w - total) / 2f + i * (sw + gap);
            return new UnityEngine.Rect(x, 22, sw, sh); // GL space: y up from the bottom
        }

        private void DrawHotbar(in MotorState s, int tick, float w)
        {
            for (int i = 0; i < 5; i++)
            {
                UnityEngine.Rect r = SlotRect(i, w, Screen.height);
                bool held = i < 4 && i == (int)s.Hand.Held;
                Rect(r.x, r.y, r.width, r.height, new Color(0, 0, 0, held ? 0.55f : 0.3f));
                if (held) Rect(r.x, r.y, r.width, 2, Color.white);
                if (i == (int)Weapon.Pump)
                {
                    float k = Mathf.Clamp01(1f - (s.Weapon.NextPump - tick * 1000L) / (float)_player.Config.Weapons.Of(Weapon.Pump).FireIntervalMilliTicks);
                    if (k < 1f) Rect(r.x, r.y + r.height - 3, r.width * k, 3, new Color(1, 1, 1, 0.6f));
                }
            }
        }

        private void DrawFeed(NetClient net, float w)
        {
            float y = 14;
            for (int i = _view.Feed.Count - 1; i >= 0; i--)
            {
                var f = _view.Feed[i];
                float age = Time.time - f.Time;
                if (age > 6f) continue;
                string killer = f.Killer == 0 || f.Killer == f.Victim ? "" : Name(net, f.Killer) + "  ";
                string line = $"{killer}[{SourceName(f.Source)}{(f.Head ? " HS" : "")}]  {Name(net, f.Victim)}";
                bool mine = f.Killer == net.PlayerId || f.Victim == net.PlayerId;
                Text(new Rect(w - 420, y, 400, 22), line, _label, mine ? Amber : Color.white, TextAnchor.MiddleRight);
                y += 22;
            }
        }

        private static string Name(NetClient net, int id) => id == net.PlayerId ? "You" : "P" + id;
    }
}
