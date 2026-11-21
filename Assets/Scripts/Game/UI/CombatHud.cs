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
                try
                {
                    HudDraw.LoadPixels();
                    if (!s.Dead) DrawCrosshair(s, cfg, w / 2f, h / 2f);
                    DrawHitMarker(w / 2f, h / 2f);
                    DrawHurt(w, h);
                    if (s.Dead) HudDraw.RoundRect(w / 2f - 220, h * 0.64f - 110, 440, 120, 12, new Color(0.05f, 0.06f, 0.08f, 0.6f));
                    DrawHealthBar(s, cfg);
                    DrawAmmo(s, cfg, net.Tick, w);
                    DrawHotbar(s, net.Tick, w);
                    DrawFeedPanels(net, w, h);
                }
                finally { GL.PopMatrix(); } // an exception must never leave the matrix stack pushed
            }

            // Health (bottom left).
            int hp = s.Health(cfg);
            bool low = hp <= cfg.MaxHealth / 3;
            HudDraw.Label(HudDraw.ToGui(HealthX + 16, Margin + 20, 120, 40), hp.ToString(), 30, low ? HudDraw.Red : HudDraw.Text, TextAnchor.MiddleLeft, true);
            HudDraw.Label(HudDraw.ToGui(HealthX + 80, Margin + 24, 60, 30), "HP", 12, HudDraw.Dim, TextAnchor.MiddleLeft, true);

            // Gun and ammo (bottom right); a grenade shows its count.
            if (!s.Dead && s.Hand.HeldItem == Item.Grenade)
            {
                float px = w - Margin - PanelW;
                HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 36, 150, 22), "GRENADE", 13, HudDraw.Dim, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 96, Margin + 16, 50, 40), s.Hand.Bag.Grenades.ToString(), 30, HudDraw.Text, TextAnchor.MiddleRight, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 44, Margin + 18, 40, 30), "/ " + _player.Config.Items.GrenadeCarry, 13, HudDraw.Dim, TextAnchor.MiddleLeft);
            }
            else if (!s.Dead && s.Hand.HasGun)
            {
                Weapon held = s.Hand.Held;
                WeaponStats st = cfg.Of(held);
                int ammo = s.Weapon.AmmoOf(held, cfg);
                float px = w - Margin - PanelW;
                bool reloading = s.Weapon.Reloading && s.Weapon.ReloadGun == held;
                Color ammoCol = ammo == 0 ? HudDraw.Red : ammo <= st.MagSize / 4 ? HudDraw.Amber : HudDraw.Text;
                HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 36, 150, 22), Names[(int)held].ToUpperInvariant(), 13, HudDraw.Dim, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 96, Margin + 16, 50, 40), ammo.ToString(), 30, ammoCol, TextAnchor.MiddleRight, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 44, Margin + 18, 40, 30), "/ " + st.MagSize, 13, HudDraw.Dim, TextAnchor.MiddleLeft);
                string hint = reloading ? "RELOADING" : ammo == 0 ? "EMPTY" : ammo <= st.MagSize / 4 ? "R  RELOAD" : null;
                if (hint != null && s.Hand.Mode == HandMode.Weapon)
                    HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 14, 150, 20), hint, 11, reloading ? HudDraw.Text : ammoCol, TextAnchor.MiddleLeft, true);
            }

            // Hotbar keys and what is in each slot.
            for (int i = 0; i < Items.Slots; i++)
            {
                UnityEngine.Rect r = SlotRect(i, w, h);
                Item it = s.Hand.Bag[i];
                bool held = i == s.Hand.Slot && it != Item.None;
                Color tc = it == Item.None ? HudDraw.A(HudDraw.Dim, 0.5f) : held ? HudDraw.Text : HudDraw.Dim;
                HudDraw.Label(HudDraw.ToGui(r.x + 7, r.y + r.height - 15, 20, 12), (i + 1).ToString(), 10, HudDraw.A(tc, 0.8f), TextAnchor.MiddleLeft, true);
                string name = it == Item.None ? "—" : it == Item.Grenade ? "Nade x" + s.Hand.Bag.Grenades : Items.Name(it);
                HudDraw.Label(HudDraw.ToGui(r.x, r.y + 3, r.width, r.height - 8), name, 12, tc, TextAnchor.MiddleCenter, held);
            }

            // Pickup prompt under the crosshair.
            if (!s.Dead && _player.PickTarget >= 0 && _player.PickTarget < net.Items.Count)
            {
                GroundItem g = net.Items[_player.PickTarget];
                PickupPlan plan = _player.PickPlan;
                string key = _player.InteractKeyName;
                string what = Items.Name(g.Kind) + (g.Count > 1 ? " x" + g.Count : "");
                string line;
                Color col = HudDraw.Text;
                if (plan.Result == PickupResult.Swapped) line = $"{key}  Swap {Items.Name(plan.SwapOut)} for {what}";
                else if (plan.Allowed) line = $"{key}  Pick up {what}";
                else if (plan.Result == PickupResult.AlreadyHave) { line = g.Kind == Item.Grenade ? "Grenades full" : "Have " + Items.Name(g.Kind); col = HudDraw.Dim; }
                else { line = what; col = HudDraw.Dim; }
                HudDraw.Label(new Rect(0, h / 2f + 46, w, 22), line, 14, col, TextAnchor.MiddleCenter, true);
            }
            else if (!s.Dead && _player.DropHold > 0f)
                HudDraw.Label(new Rect(0, h / 2f + 46, w, 22), "Dropping " + Items.Name(s.Hand.HeldItem) + "...", 13, HudDraw.Dim, TextAnchor.MiddleCenter, true);

            // Damage dealt by the last hit, next to the crosshair.
            if (Time.time - _view.HitTime < 0.5f)
                HudDraw.Label(new Rect(w / 2f + 20, h / 2f + 8, 80, 20), _view.LastHitDamage.ToString(), 14,
                     Time.time - _view.HeadHitTime < 0.5f ? HudDraw.Amber : HudDraw.Text, TextAnchor.MiddleLeft, true);

            DrawFeed(net, w);

            if (s.Dead)
            {
                float left = Mathf.Max(0, (s.RespawnTick - net.Tick) / (float)SimTime.TicksPerSecond);
                string by = _view.KilledBy != 0 && _view.KilledBy != net.PlayerId ? $"by P{_view.KilledBy}  ·  {SourceName(_view.KilledWith)}" : SourceName(_view.KilledWith);
                HudDraw.Label(new Rect(0, h * 0.36f, w, 40), "ELIMINATED", 30, HudDraw.Red, TextAnchor.MiddleCenter, true);
                HudDraw.Label(new Rect(0, h * 0.36f + 38, w, 22), by, 15, HudDraw.Text, TextAnchor.MiddleCenter);
                HudDraw.Label(new Rect(0, h * 0.36f + 64, w, 22), $"respawning in {left:0.0} s", 13, HudDraw.Dim, TextAnchor.MiddleCenter);
            }
        }

        private const float Margin = 24f, PanelW = 240f, PanelH = 64f, HealthX = 24f;

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

        private static string SourceName(DamageSource s)
        {
            switch (s)
            {
                case DamageSource.Throw: return "Throw";
                case DamageSource.Fall: return "Fall";
                case DamageSource.Grenade: return "Grenade";
                case DamageSource.Punch: return "Punch";
                default: return (int)s < Names.Length ? Names[(int)s] : s.ToString();
            }
        }

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
            if (!hand.HasGun)
            {
                // A grenade or empty hands: a plain dot (the throw arc arrives with grenades).
                Dot(cx, cy, 2f, hand.ItemReady ? new Color(1, 1, 1, 0.95f) : new Color(1, 1, 1, 0.4f));
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
            HudDraw.PanelRect(HealthX, Margin, PanelW, PanelH);
            Color c = k <= 1f / 3f ? HudDraw.Red : HudDraw.Text;
            if (Time.time - _view.HurtTime < 0.15f) c = Color.Lerp(c, HudDraw.Red, 0.7f);
            float bx = HealthX + 16, bw = PanelW - 32, by = Margin + 12;
            HudDraw.Bar(bx, by, bw, 5, k, c);
            // Quarter marks so you can read 150 at a glance.
            for (int q = 1; q < 4; q++) HudDraw.RoundRect(bx + bw * q / 4f - 0.5f, by, 1, 5, 0, new Color(0, 0, 0, 0.45f));
        }

        private void DrawAmmo(in MotorState s, WeaponsConfig cfg, int tick, float w)
        {
            if (s.Dead) return;
            float px = w - Margin - PanelW;
            HudDraw.PanelRect(px, Margin, PanelW, PanelH);
            if (s.Hand.HeldItem == Item.Grenade)
            {
                float gx = px + 16, gy = Margin + 12;
                int carry = Mathf.Max(1, _player.Config.Items.GrenadeCarry);
                float pw = (PanelW - 32 - 6 * (carry - 1)) / carry;
                for (int i = 0; i < carry; i++)
                    HudDraw.RoundRect(gx + i * (pw + 6), gy, pw, 5, 1.5f, i < s.Hand.Bag.Grenades ? HudDraw.A(HudDraw.Text, 0.9f) : new Color(1, 1, 1, 0.12f));
                return;
            }
            if (!s.Hand.HasGun) return;
            Weapon held = s.Hand.Held;
            WeaponStats st = cfg.Of(held);
            int ammo = s.Weapon.AmmoOf(held, cfg);
            float bx = px + 16, bw = PanelW - 32, by = Margin + 12;
            if (s.Weapon.Reloading && s.Weapon.ReloadGun == held)
            {
                // Progress of the magazine (or of the next shell for the pump).
                int total = st.ShellReload ? st.ShellTicks : st.ReloadTicks;
                float k = Mathf.Clamp01(1f - (s.Weapon.ReloadTick - tick) / (float)Mathf.Max(1, total));
                HudDraw.Bar(bx, by, bw, 5, k, HudDraw.Text);
            }
            else
            {
                // One pip per round (thin segments; the pump's 5 shells read as shells).
                int n = st.MagSize;
                float gap = n > 20 ? 1.5f : 3f, pw = (bw - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                    HudDraw.RoundRect(bx + i * (pw + gap), by, pw, 5, n > 20 ? 0 : 1.5f, i < ammo ? HudDraw.A(HudDraw.Text, 0.9f) : new Color(1, 1, 1, 0.12f));
            }
        }

        private static UnityEngine.Rect SlotRect(int i, float w, float h)
        {
            const float sw = 74, sh = 38, gap = 6;
            float total = 5 * sw + 4 * gap;
            float x = (w - total) / 2f + i * (sw + gap);
            return new UnityEngine.Rect(x, Margin, sw, sh); // GL space: y up from the bottom
        }

        private void DrawHotbar(in MotorState s, int tick, float w)
        {
            for (int i = 0; i < Items.Slots; i++)
            {
                UnityEngine.Rect r = SlotRect(i, w, Screen.height);
                Item it = s.Hand.Bag[i];
                bool held = i == s.Hand.Slot && it != Item.None;
                HudDraw.RoundRect(r.x, r.y, r.width, r.height, 7, held ? new Color(0.05f, 0.06f, 0.08f, 0.72f) : new Color(0.05f, 0.06f, 0.08f, it == Item.None ? 0.25f : 0.42f));
                if (held) HudDraw.RoundRect(r.x + 14, r.y + 3, r.width - 28, 2, 1, HudDraw.Text);
                if (it == Item.Pump)
                {
                    float k = Mathf.Clamp01(1f - (s.Weapon.NextPump - tick * 1000L) / (float)_player.Config.Weapons.Of(Weapon.Pump).FireIntervalMilliTicks);
                    if (k < 1f) HudDraw.RoundRect(r.x + 6, r.y + r.height - 4, (r.width - 12) * k, 2, 1, HudDraw.A(HudDraw.Text, 0.6f));
                }
            }
        }

        private string FeedText(NetClient net, in WeaponView.FeedLine f)
        {
            string killer = f.Killer == 0 || f.Killer == f.Victim ? "" : Name(net, f.Killer) + "   ";
            return $"{killer}{SourceName(f.Source).ToUpperInvariant()}{(f.Head ? " · HEAD" : "")}   {Name(net, f.Victim)}";
        }

        private float FeedWidth(string line) => 22f + line.Length * 7.4f;

        /// <summary>Kill feed (top right): one rounded chip per kill, yours outlined in amber. GL part.</summary>
        private void DrawFeedPanels(NetClient net, float w, float h)
        {
            float y = 16;
            for (int i = _view.Feed.Count - 1; i >= 0; i--)
            {
                var f = _view.Feed[i];
                float age = Time.time - f.Time;
                if (age > 6f) continue;
                float a = Mathf.Clamp01((6f - age) / 0.5f);
                float fw = FeedWidth(FeedText(net, f));
                bool mine = f.Killer == net.PlayerId || f.Victim == net.PlayerId;
                float gy = h - y - 24;
                if (mine) HudDraw.RoundRect(w - Margin - fw - 1, gy - 1, fw + 2, 26, 7, HudDraw.A(HudDraw.Amber, 0.55f * a));
                HudDraw.RoundRect(w - Margin - fw, gy, fw, 24, 6, HudDraw.A(HudDraw.Panel, a));
                y += 30;
            }
        }

        private void DrawFeed(NetClient net, float w)
        {
            float y = 16;
            for (int i = _view.Feed.Count - 1; i >= 0; i--)
            {
                var f = _view.Feed[i];
                float age = Time.time - f.Time;
                if (age > 6f) continue;
                float a = Mathf.Clamp01((6f - age) / 0.5f);
                string line = FeedText(net, f);
                float fw = FeedWidth(line);
                bool mine = f.Killer == net.PlayerId || f.Victim == net.PlayerId;
                HudDraw.Label(new Rect(w - Margin - fw, y, fw, 24), line, 12, HudDraw.A(mine ? HudDraw.Amber : HudDraw.Text, a), TextAnchor.MiddleCenter, true);
                y += 30;
            }
        }

        private static string Name(NetClient net, int id) => id == net.PlayerId ? "You" : "P" + id;
    }
}
