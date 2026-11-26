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
        private string _doorKey, _openLine, _shutLine; // door prompt, built when the bind changes (no string per frame)
        private LocalPlayer _player;
        private NetSession _session;
        private WeaponView _view;
        private Camera _camera;
        private Material _gl;

        private static readonly Color Red = new Color(1f, 0.22f, 0.18f, 1f);
        private static readonly Color Amber = new Color(1f, 0.72f, 0.2f, 1f);
        private static readonly string[] Names = { "Pump", "SMG", "AR", "Pistol" };
        // Text the HUD shows every frame, built once (a string made per frame is garbage per frame).
        private static readonly string[] NamesUpper = { "PUMP", "SMG", "AR", "PISTOL" };
        private static readonly string[] SlashCache = new string[128], NadeCache = new string[16];
        private readonly System.Collections.Generic.Dictionary<long, string> _feedText = new System.Collections.Generic.Dictionary<long, string>();

        private static string Slash(int n) => n < 0 || n >= SlashCache.Length ? "/ " + n : SlashCache[n] ?? (SlashCache[n] = "/ " + n);
        private static readonly string[] C4Cache = new string[16], GlCache = new string[16];
        private static string Counted(string[] cache, string prefix, int n) => n < 0 || n >= cache.Length ? prefix + n : cache[n] ?? (cache[n] = prefix + n);
        private static readonly System.Collections.Generic.Dictionary<Item, string> UpperCache = new System.Collections.Generic.Dictionary<Item, string>();
        private static string UpperName(Item it) { if (!UpperCache.TryGetValue(it, out string u)) UpperCache[it] = u = Items.Name(it).ToUpperInvariant(); return u; }
        // The C4 detonate hint, rebuilt only when what it says changes.
        private string _hintText, _hintFire, _hintAim, _keyFire, _keyAim; private float _hintKeysAt; private bool _hintEmpty; private int _hintOut = -1;
        private string DetonateHint(bool empty, int out_)
        {
            // Reading a bind's display name builds strings, so look the keys up at most twice a second (a rebind shows within 0.5 s).
            if (_hintFire == null || Time.unscaledTime >= _hintKeysAt) { _keyFire = _player.FireKeyName; _keyAim = _player.AimKeyName; _hintKeysAt = Time.unscaledTime + 0.5f; }
            string fire = _keyFire, aim = _keyAim;
            if (_hintText == null || empty != _hintEmpty || out_ != _hintOut || fire != _hintFire || aim != _hintAim)
            {
                _hintEmpty = empty; _hintOut = out_; _hintFire = fire; _hintAim = aim;
                _hintText = (empty ? fire + " / " : "") + aim + "  DETONATE  (" + out_ + " out)";
            }
            return _hintText;
        }

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
            // Draw-only HUD: nothing here reacts to mouse or key events, so only the repaint call does any work.
            if (Event.current.type != EventType.Repaint) return;
            if (net.IsSpectator)
            {
                // Watching: only the kill feed (the watch HUD shows the watched player).
                float sw = Screen.width, sh = Screen.height;
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    HudDraw.BeginBatch();
                    DrawFeedPanels(net, sw, sh);
                }
                finally
                {
                    HudDraw.EndBatch();
                    GL.PopMatrix();
                }
                DrawFeed(net, sw);
                return;
            }

            MotorState s = _player.State;
            WeaponsConfig cfg = _player.Config.Weapons;
            float w = Screen.width, h = Screen.height;

            {
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    HudDraw.BeginBatch(); // every shape below goes out as one draw
                    if (!s.Dead) DrawCrosshair(s, cfg, w / 2f, h / 2f);
                    DrawHitMarker(w / 2f, h / 2f);
                    DrawHurt(w, h);
                    if (s.Dead) HudDraw.RoundRect(w / 2f - 220, h * 0.64f - 110, 440, 120, 12, new Color(0.05f, 0.06f, 0.08f, 0.6f));
                    DrawHealthBar(s, cfg);
                    DrawAmmo(s, cfg, net.Tick, w);
                    DrawHotbar(s, net.Tick, w);
                    DrawFeedPanels(net, w, h);
                }
                finally
                {
                    // An exception must never leave the batch open or the matrix stack pushed.
                    HudDraw.EndBatch();
                    GL.PopMatrix();
                }
            }

            // Health (bottom left).
            int hp = s.Health(cfg);
            bool low = hp <= cfg.MaxHealth / 3;
            HudDraw.Label(HudDraw.ToGui(HealthX + 16, Margin + 20, 120, 40), HudDraw.Num(hp), 30, low ? HudDraw.Red : HudDraw.Text, TextAnchor.MiddleLeft, true);
            HudDraw.Label(HudDraw.ToGui(HealthX + 80, Margin + 24, 60, 30), "HP", 12, HudDraw.Dim, TextAnchor.MiddleLeft, true);

            // Gun and ammo (bottom right); grenades, C4 and the launcher show their count.
            Item heldItem = s.Hand.HeldItem;
            if (!s.Dead && Items.Stacks(heldItem))
            {
                float px = w - Margin - PanelW;
                int count = s.Hand.Bag.CountIn(s.Hand.Slot);
                int out_ = GrenadeRules.ActiveOf(s, ThrowKind.C4);
                string title = heldItem == Item.C4 && count == 0 ? "DETONATOR" : UpperName(heldItem);
                HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 36, 150, 22), title, 13, HudDraw.Dim, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 96, Margin + 16, 50, 40), HudDraw.Num(count), 30, count == 0 ? HudDraw.Dim : HudDraw.Text, TextAnchor.MiddleRight, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 44, Margin + 18, 40, 30), Slash(_player.Config.Items.Carry(heldItem)), 13, HudDraw.Dim, TextAnchor.MiddleLeft);
                string hint = null;
                if (heldItem == Item.C4 && out_ > 0) hint = DetonateHint(count == 0, out_);
                else if (heldItem == Item.Launcher && net.Tick < s.LauncherReadyTick) hint = "LOADING";
                if (hint != null) HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 14, 200, 20), hint, 11, heldItem == Item.C4 ? HudDraw.Amber : HudDraw.Dim, TextAnchor.MiddleLeft, true);
            }
            else if (!s.Dead && s.Hand.HasGun)
            {
                Weapon held = s.Hand.Held;
                WeaponStats st = cfg.Of(held);
                int ammo = s.Weapon.AmmoOf(held, cfg);
                float px = w - Margin - PanelW;
                bool reloading = s.Weapon.Reloading && s.Weapon.ReloadGun == held;
                Color ammoCol = ammo == 0 ? HudDraw.Red : ammo <= st.MagSize / 4 ? HudDraw.Amber : HudDraw.Text;
                Rarity heldRar = s.Hand.Bag.RarityOf(s.Hand.Slot);
                HudDraw.Label(HudDraw.ToGui(px + 16, Margin + 36, 150, 22), NamesUpper[(int)held], 13, heldRar != Rarity.None ? RarityColours.Of(heldRar) : HudDraw.Dim, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 96, Margin + 16, 50, 40), HudDraw.Num(ammo), 30, ammoCol, TextAnchor.MiddleRight, true);
                HudDraw.Label(HudDraw.ToGui(px + PanelW - 44, Margin + 18, 40, 30), Slash(st.MagSize), 13, HudDraw.Dim, TextAnchor.MiddleLeft);
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
                Rarity rar = s.Hand.Bag.RarityOf(i);
                if (it != Item.None && rar != Rarity.None) tc = HudDraw.A(RarityColours.Of(rar), held ? 1f : 0.7f); // battle royale: the slot reads its rarity
                HudDraw.Label(HudDraw.ToGui(r.x + 7, r.y + r.height - 15, 20, 12), HudDraw.Num(i + 1), 10, HudDraw.A(tc, 0.8f), TextAnchor.MiddleLeft, true);
                int n = s.Hand.Bag.Count(i);
                string name = it == Item.None ? "—" : it == Item.Grenade ? Counted(NadeCache, "Nade x", n)
                    : it == Item.C4 ? (n == 0 ? "Det." : Counted(C4Cache, "C4 x", n))
                    : it == Item.Launcher ? Counted(GlCache, "GL x", n) : Items.Name(it);
                HudDraw.Label(HudDraw.ToGui(r.x, r.y + 3, r.width, r.height - 8), name, 12, tc, TextAnchor.MiddleCenter, held);
            }

            // Pickup prompt under the crosshair.
            if (!s.Dead && _player.PickTarget >= 0 && _player.PickTarget < net.Items.Count)
            {
                GroundItem g = net.Items[_player.PickTarget];
                PickupPlan plan = _player.PickPlan;
                string key = _player.InteractKeyName;
                string what = Items.Name(g.Kind, g.Rarity) + (g.Count > 1 ? " x" + g.Count : "");
                string line;
                Color col = g.Rarity != Rarity.None ? RarityColours.Of(g.Rarity) : HudDraw.Text;
                if (plan.Opens) line = $"{key}  Open chest";
                else if (plan.Result == PickupResult.Swapped && plan.SwapOut == g.Kind) line = $"{key}  Swap your {Items.Name(g.Kind, s.Hand.Bag.RarityOf(plan.Slot))} for {what}";
                else if (plan.Result == PickupResult.Swapped) line = $"{key}  Swap {Items.Name(plan.SwapOut)} for {what}";
                else if (plan.Allowed) line = $"{key}  Pick up {what}";
                else if (plan.Result == PickupResult.AlreadyHave) { line = g.Kind == Item.Grenade ? "Grenades full" : g.Kind == Item.C4 ? "C4 full" : g.Kind == Item.Launcher ? "Launcher rounds full" : "Have " + Items.Name(g.Kind); col = HudDraw.Dim; }
                else { line = what; col = HudDraw.Dim; }
                HudDraw.Label(new Rect(0, h / 2f + 46, w, 22), line, 14, col, TextAnchor.MiddleCenter, true);
            }
            else if (!s.Dead && _player.DoorTarget >= 0)
            {
                string key = _player.InteractKeyName;
                if (key != _doorKey) { _doorKey = key; _openLine = key + "  Open door"; _shutLine = key + "  Close door"; }
                HudDraw.Label(new Rect(0, h / 2f + 46, w, 22), _player.DoorTargetOpen ? _shutLine : _openLine, 14, HudDraw.Text, TextAnchor.MiddleCenter, true);
            }
            else if (!s.Dead && _player.DropHold > 0f)
                HudDraw.Label(new Rect(0, h / 2f + 46, w, 22), "Dropping " + Items.Name(s.Hand.HeldItem) + "...", 13, HudDraw.Dim, TextAnchor.MiddleCenter, true);

            // Damage dealt by the last hit, next to the crosshair.
            if (Time.time - _view.HitTime < 0.5f)
                HudDraw.Label(new Rect(w / 2f + 20, h / 2f + 8, 80, 20), HudDraw.Num(_view.LastHitDamage), 14,
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

        private static string SourceName(DamageSource s)
        {
            switch (s)
            {
                case DamageSource.Throw: return "Throw";
                case DamageSource.Fall: return "Fall";
                case DamageSource.Grenade: return "Grenade";
                case DamageSource.Punch: return "Punch";
                case DamageSource.C4: return "C4";
                case DamageSource.Launcher: return "Launcher";
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

        private static void Rect(float x, float y, float w, float h, Color c) => HudDraw.Quad(x, y, w, h, c);

        private static void Ring(float cx, float cy, float r, float th, Color c) => HudDraw.Arc(cx, cy, r, th, 0, 360, c);

        private static void Arc(float cx, float cy, float r, float thick, float fromDeg, float spanDeg, Color c) => HudDraw.Arc(cx, cy, r, thick, fromDeg, spanDeg, c);

        // ---------------------------------------------------------------- Hits

        private void DrawHitMarker(float cx, float cy)
        {
            // Sharp, not soft: the marker pops in oversized and snaps to size in ~50 ms, holds full strength for most of
            // its life, then cuts out quickly. A dark outline keeps it readable on bright walls and sky.
            float now = Time.time;
            bool kill = now - _view.KillMarkTime < KillLife;
            float age = kill ? now - _view.KillMarkTime : now - _view.HitTime;
            float life = kill ? KillLife : HitLife;
            if (age > life) return;
            float u = age / life;
            float a = u < 0.6f ? 1f : 1f - (u - 0.6f) / 0.4f;
            float pop = 1f + (kill ? 0.55f : 0.35f) * Mathf.Exp(-age * 60f);
            bool head = now - _view.HeadHitTime < HitLife;
            Color c = kill ? Red : head ? Amber : Color.white;
            float inner = (kill ? 9f : 7f) * pop, outer = (kill ? 24f : head ? 18f : 15f) * pop, th = kill ? 2.4f : head ? 1.8f : 1.5f;
            var col = new Color(c.r, c.g, c.b, a);
            var dark = new Color(0f, 0f, 0f, a * 0.55f);
            Vector2 ctr = new Vector2(cx, cy);
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < 4; k++)
                {
                    float ang = (45f + 90f * k) * Mathf.Deg2Rad;
                    Vector2 d = new Vector2(Mathf.Sin(ang), Mathf.Cos(ang));
                    float t = pass == 0 ? th + 1f : th;
                    Vector2 n = new Vector2(d.y, -d.x) * t;
                    float grow = pass == 0 ? 1f : 0f;
                    Vector2 p0 = ctr + d * (inner - grow), p1 = ctr + d * (outer + grow);
                    HudDraw.Quad(p0 + n, p1 + n, p1 - n, p0 - n, pass == 0 ? dark : col);
                }
            if (kill)
            {
                // A thin ring snapping outward from the X: the kill reads at a glance, even mid-slide.
                float r = 16f + 26f * Mathf.Sqrt(Mathf.Clamp01(age / 0.22f));
                float ra = Mathf.Clamp01(1f - age / 0.22f);
                if (ra > 0f) HudDraw.Arc(cx, cy, r, 2f, 0, 360, new Color(Red.r, Red.g, Red.b, 0.8f * ra));
            }
        }

        private const float HitLife = 0.2f, KillLife = 0.5f;

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
                HudDraw.Tri(tip, b + side, b - side, new Color(Red.r, Red.g, Red.b, 0.9f * k));
            }
        }

        private static void Edge(float x, float y, float w, float hgt, float a, bool bottom)
        {
            Color solid = new Color(Red.r, Red.g * 0.5f, Red.b * 0.5f, a), clear = new Color(Red.r, 0, 0, 0);
            HudDraw.Quad(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + hgt), new Vector2(x, y + hgt), bottom ? solid : clear, bottom ? clear : solid);
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
            if (Items.Stacks(s.Hand.HeldItem))
            {
                float gx = px + 16, gy = Margin + 12;
                int carry = Mathf.Max(1, _player.Config.Items.Carry(s.Hand.HeldItem));
                int have = s.Hand.Bag.CountIn(s.Hand.Slot);
                float pw = (PanelW - 32 - 6 * (carry - 1)) / carry;
                for (int i = 0; i < carry; i++)
                    HudDraw.RoundRect(gx + i * (pw + 6), gy, pw, 5, 1.5f, i < have ? HudDraw.A(HudDraw.Text, 0.9f) : new Color(1, 1, 1, 0.12f));
                if (s.Hand.HeldItem == Item.Launcher && tick < s.LauncherReadyTick)
                {
                    // The next round loading: a thin bar under the pips.
                    float k = Mathf.Clamp01(1f - (s.LauncherReadyTick - tick) / (float)Mathf.Max(1, _player.Config.Items.Launcher.ThrowRecoveryTicks));
                    HudDraw.Bar(gx, gy + 8, PanelW - 32, 2, k, HudDraw.Dim);
                }
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
            // The same few lines are asked for twice a frame for six seconds: keep them by what they say.
            long key = ((long)(f.Killer & 0xFFFF) << 40) | ((long)(f.Victim & 0xFFFF) << 24) | ((long)((int)f.Source & 0xFF) << 16) | (f.Head ? 1L << 8 : 0) | (uint)(net.PlayerId & 0xFF);
            if (_feedText.TryGetValue(key, out string line)) return line;
            if (_feedText.Count > 256) _feedText.Clear();
            string killer = f.Killer == 0 || f.Killer == f.Victim ? "" : Name(net, f.Killer) + "   ";
            line = $"{killer}{SourceName(f.Source).ToUpperInvariant()}{(f.Head ? " · HEAD" : "")}   {Name(net, f.Victim)}";
            _feedText[key] = line;
            return line;
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
