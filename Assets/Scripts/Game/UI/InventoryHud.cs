using Sculpt.Core;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The inventory screen (I, Ben 2026-11-26): the five slots large, drag one onto another (or click one, then the
    /// other) to swap them. Each swap is a normal predicted command, so the bag changes on the next tick and the hand
    /// follows the item it holds (nothing is redrawn, nothing gets faster). While it is open the mouse is free: no mouse
    /// look and no firing, but the keys still move you (<see cref="LocalPlayer.InventoryOpen"/>). Auto-sort on pickup is
    /// set in Esc settings and shown under each slot. I or Esc closes it.
    /// </summary>
    public sealed class InventoryHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private GameInput _input;
        private bool _open;
        private int _drag = -1, _picked = -1;
        private Vector2 _mouse;
        private Texture2D _white;
        private GUIStyle _name, _small, _key, _title;

        public bool IsOpen => _open;

        public void Init(LocalPlayer player, GameInput input)
        {
            _player = player;
            _input = input;
            useGUILayout = false; // plain GUI calls only
        }

        private void Update()
        {
            if (_player == null || _input == null) return;
            bool can = _player.Net != null && _player.Net.Joined && !_player.Net.IsSpectator && !_player.State.Dead;
            if (_open && (!can || _player.InputBlocked))
            {
                // Settings (Esc) or the home screen took over, or you died: let go without grabbing the mouse back.
                Close(!_player.InputBlocked && can);
                return;
            }
            if (!_input.Inventory.WasPressedThisFrame()) return;
            if (_open) Close(true);
            else if (can && !_player.InputBlocked) Open();
        }

        private void Open()
        {
            _open = true;
            _drag = _picked = -1;
            _player.InventoryOpen = true;
            LocalPlayer.LockCursor(false);
        }

        private void Close(bool relock)
        {
            _open = false;
            _drag = _picked = -1;
            _player.InventoryOpen = false;
            if (relock) LocalPlayer.LockCursor(true);
        }

        private void OnDisable()
        {
            if (_open && _player != null) _player.InventoryOpen = false;
            _open = false;
        }

        private void Styles()
        {
            if (_white != null) return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _name = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            _key = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        private const float SlotW = 150, SlotH = 130, Gap = 14;

        private static Rect SlotRect(int k, float w)
        {
            float total = Items.Slots * SlotW + (Items.Slots - 1) * Gap;
            return new Rect((w - total) / 2f + k * (SlotW + Gap), 1080f - 330f, SlotW, SlotH);
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void OnGUI()
        {
            if (!_open || _player == null) return;
            Styles();
            float k = Screen.height / 1080f;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float w = Screen.width / k;
            Event e = Event.current;
            Vector2 m = e.mousePosition; // already in the scaled space
            _mouse = m;

            HandState h = _player.State.Hand;
            int over = -1;
            for (int i = 0; i < Items.Slots; i++) if (SlotRect(i, w).Contains(m)) over = i;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (over >= 0 && h.Bag[over] != Item.None) _drag = over;
                else if (over >= 0 && _picked >= 0) { Swap(_picked, over); _picked = -1; }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _drag >= 0)
            {
                if (over >= 0 && over != _drag) { Swap(_drag, over); _picked = -1; }
                else if (over == _drag)
                {
                    // A click without a drag: pick it, then click where it should go.
                    if (_picked >= 0 && _picked != over) { Swap(_picked, over); _picked = -1; }
                    else _picked = _picked == over ? -1 : over;
                }
                _drag = -1;
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 1) { _picked = -1; _drag = -1; e.Use(); }

            if (e.type == EventType.Repaint)
            {
                Rect first = SlotRect(0, w), last = SlotRect(Items.Slots - 1, w);
                Rect panel = new Rect(first.x - 24, first.y - 64, last.xMax - first.x + 48, SlotH + 130);
                Fill(panel, new Color(0.05f, 0.06f, 0.08f, 0.82f));
                _title.normal.textColor = HudDraw.Text;
                GUI.Label(new Rect(panel.x, panel.y + 10, panel.width, 30), "INVENTORY", _title);
                SlotPrefs prefs = NetSession.LocalSlotPrefs;
                for (int i = 0; i < Items.Slots; i++)
                {
                    Rect r = SlotRect(i, w);
                    Item it = h.Bag[i];
                    bool held = i == h.Slot && it != Item.None;
                    bool lit = i == over || i == _picked || i == _drag;
                    Fill(r, new Color(0.12f, 0.13f, 0.16f, it == Item.None ? 0.55f : 0.9f));
                    if (lit) Fill(new Rect(r.x, r.y, r.width, 3), HudDraw.Text);
                    if (held) Fill(new Rect(r.x, r.yMax - 3, r.width, 3), HudDraw.Amber);
                    Rarity rar = h.Bag.RarityOf(i);
                    Color tc = it == Item.None ? HudDraw.Dim : rar != Rarity.None ? RarityColours.Of(rar) : HudDraw.Text;
                    _key.normal.textColor = HudDraw.Dim;
                    GUI.Label(new Rect(r.x + 8, r.y + 6, 30, 20), (i + 1).ToString(), _key);
                    if (i != _drag)
                    {
                        _name.normal.textColor = tc;
                        GUI.Label(new Rect(r.x + 6, r.y + 22, r.width - 12, 56), it == Item.None ? "empty" : Items.Name(it, rar), _name);
                        int n = h.Bag.CountIn(i);
                        if (it != Item.None && Items.Stacks(it))
                        {
                            _small.normal.textColor = HudDraw.Text;
                            GUI.Label(new Rect(r.x, r.y + 78, r.width, 20), it == Item.C4 && n == 0 ? "detonator" : "x " + n, _small);
                        }
                    }
                    _small.normal.textColor = HudDraw.Dim;
                    GUI.Label(new Rect(r.x, r.yMax + 4, r.width, 20), prefs[i] == SlotPref.Any ? "auto: anything" : "auto: " + SlotPrefs.Name(prefs[i]), _small);
                }
                _small.normal.textColor = HudDraw.Dim;
                GUI.Label(new Rect(panel.x, panel.yMax - 30, panel.width, 22),
                    "Drag a slot onto another to swap (or click one, then the other).  You can still move.  I closes.  Auto-sort on pickup: Esc settings.", _small);
                if (_drag >= 0 && h.Bag[_drag] != Item.None)
                {
                    _name.normal.textColor = HudDraw.Text;
                    Rect g = new Rect(_mouse.x - SlotW / 2, _mouse.y - 28, SlotW, 56);
                    Fill(g, new Color(0.2f, 0.22f, 0.26f, 0.9f));
                    GUI.Label(g, Items.Name(h.Bag[_drag], h.Bag.RarityOf(_drag)), _name);
                }
            }
            GUI.matrix = old;
        }

        private void Swap(int a, int b)
        {
            if (a == b) return;
            _player.RequestSwap(a, b);
        }
    }
}
