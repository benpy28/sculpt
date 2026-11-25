using System.Collections.Generic;
using Sculpt.Game.Input;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Settings menu (Esc): FOV, mouse sensitivity, viewmodel size, master volume, head bob, speed FOV kick, speed lines,
    /// and every key bind (click a bind, press the new key or button; Esc cancels; mouse wheel works too). The game
    /// keeps running underneath (it is multiplayer), but your movement and aim are blocked while the menu is open.
    /// Values apply live and are saved when the menu closes. IMGUI placeholder art; no purple.
    /// </summary>
    public sealed class SettingsMenu : MonoBehaviour
    {
        private LocalPlayer _player;
        private ClientSettings _settings;
        private GameInput _input;
        private bool _open;
        private Vector2 _scroll;
        private InputActionRebindingExtensions.RebindingOperation _rebind;
        private InputAction _rebindAction;
        private int _rebindIndex;
        private int _rebindEndedFrame = -10;
        private GUIStyle _title, _label, _value, _button, _bindButton, _header, _sub, _warn;
        private Texture2D _panel;

        private struct Row
        {
            public InputAction Action;
            public int Index;
            public string Label;
            public string Header; // a section header row when set
        }

        private readonly List<Row> _rows = new List<Row>();

        // Duplicate-bind warning: the bind just set (A) is also used by B in a context that can be live at the same time.
        private string _rebindOldPath;
        private InputAction _clashA, _clashB;
        private int _clashAIndex, _clashBIndex;
        private string _clashOldPathA;

        public bool IsOpen => _open;

        public void Init(LocalPlayer player, ClientSettings settings, GameInput input)
        {
            _player = player;
            _settings = settings;
            _input = input;
            AudioListener.volume = _settings.MasterVolume;
            BuildRows();
        }

        private void BuildRows()
        {
            _rows.Clear();
            string section = null;
            foreach (InputAction a in _input.Player.actions)
            {
                if (a == _input.Look) continue; // the mouse is the mouse
                string s = SectionOf(_input.ContextOf(a));
                if (s != section) { section = s; _rows.Add(new Row { Header = s }); }
                AddRows(a);
            }
            if (GameInput.DevBuild)
            {
                _rows.Add(new Row { Header = "Dev keys (dev builds only)" });
                foreach (InputAction a in _input.Dev.actions) AddRows(a);
            }
        }

        private static string SectionOf(GameInput.Context c) =>
            c == GameInput.Context.Gun ? "Guns" : c == GameInput.Context.Sculpt ? "Sculpt mode" : "Movement and modes";

        private void AddRows(InputAction a)
        {
            int second = 0;
            for (int i = 0; i < a.bindings.Count; i++)
            {
                InputBinding b = a.bindings[i];
                if (b.isComposite) continue;
                string label = b.isPartOfComposite ? $"{a.name} {Nice(b.name)}" : (second++ == 0 ? a.name : $"{a.name} (second)");
                _rows.Add(new Row { Action = a, Index = i, Label = label });
            }
        }

        /// <summary>After a bind changed: is its key also bound to another action that can be live at the same time?</summary>
        private void CheckClash(InputAction a, int index, string oldPath)
        {
            _clashA = null;
            string path = a.bindings[index].effectivePath;
            if (string.IsNullOrEmpty(path)) return;
            GameInput.Context ca = _input.ContextOf(a);
            foreach (Row r in _rows)
            {
                if (r.Header != null || (r.Action == a && r.Index == index)) continue;
                if (r.Action == a) continue; // a second bind on the same action is fine
                if (!GameInput.Clashes(ca, _input.ContextOf(r.Action))) continue;
                if (!string.Equals(r.Action.bindings[r.Index].effectivePath, path, System.StringComparison.OrdinalIgnoreCase)) continue;
                _clashA = a; _clashAIndex = index; _clashB = r.Action; _clashBIndex = r.Index; _clashOldPathA = oldPath;
                return;
            }
        }

        private void SwapClash()
        {
            if (_clashA == null) return;
            _clashB.ApplyBindingOverride(_clashBIndex, string.IsNullOrEmpty(_clashOldPathA) ? "" : _clashOldPathA);
            _clashA = null;
            _input.SaveOverrides();
        }

        private static string Nice(string part) => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part.Substring(1);

        private void Update()
        {
            if (_player == null) return;
            AudioListener.volume = _settings.MasterVolume;
            var kb = Keyboard.current;
            if (_rebind != null)
            {
                // The wheel is not a button the rebinder listens for: catch it by hand.
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    float y = mouse.scroll.ReadValue().y;
                    if (y > 0.1f) FinishRebindWith("<Mouse>/scroll/up");
                    else if (y < -0.1f) FinishRebindWith("<Mouse>/scroll/down");
                }
                return;
            }
            if (kb == null || !kb.escapeKey.wasPressedThisFrame || Time.frameCount - _rebindEndedFrame < 2) return;
            if (_open) Close();
            else if (!_player.InputBlocked && Time.frameCount != MainMenu.ClosedFrame) Open(); // not while another box (join address) has the keyboard
        }

        private void Open()
        {
            _open = true;
            _player.InputBlocked = true;
            LocalPlayer.LockCursor(false);
        }

        private void Close()
        {
            CancelRebind();
            _open = false;
            _settings.Clamped().Save();
            _input.SaveOverrides();
            _player.InputBlocked = false;
            LocalPlayer.LockCursor(true);
        }

        // ---------------------------------------------------------------- Rebinding

        private void StartRebind(InputAction action, int index)
        {
            CancelRebind();
            _rebindAction = action;
            _rebindIndex = index;
            _rebindOldPath = action.bindings[index].effectivePath;
            _clashA = null;
            action.Disable();
            _rebind = action.PerformInteractiveRebinding(index)
                .WithControlsExcluding("<Mouse>/position")
                .WithControlsExcluding("<Mouse>/delta")
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Pointer>/delta")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.05f)
                .OnComplete(op => EndRebind(true))
                .OnCancel(op => EndRebind(false))
                .Start();
        }

        private void FinishRebindWith(string path)
        {
            InputAction a = _rebindAction;
            int i = _rebindIndex;
            string old = _rebindOldPath;
            CancelRebind();
            if (a == null) return;
            a.ApplyBindingOverride(i, path);
            _input.SaveOverrides();
            CheckClash(a, i, old);
        }

        private void EndRebind(bool saved)
        {
            InputAction a = _rebindAction;
            int index = _rebindIndex;
            _rebind?.Dispose();
            _rebind = null;
            _rebindAction = null;
            _rebindEndedFrame = Time.frameCount;
            a?.Enable();
            if (saved && a != null)
            {
                _input.SaveOverrides();
                CheckClash(a, index, _rebindOldPath);
            }
        }

        private void CancelRebind()
        {
            if (_rebind == null) return;
            var op = _rebind;
            op.Cancel(); // calls EndRebind(false)
            if (_rebind == op) EndRebind(false);
        }

        private void ResetBinds()
        {
            CancelRebind();
            _input.Player.RemoveAllBindingOverrides();
            _input.Dev.RemoveAllBindingOverrides();
            _clashA = null;
            _input.SaveOverrides();
        }

        // ---------------------------------------------------------------- Drawing

        private void Styles()
        {
            if (_title != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.07f, 0.075f, 0.085f, 0.94f));
            _panel.Apply();
            _title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            _title.normal.textColor = Color.white;
            _header = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _header.normal.textColor = new Color(1f, 0.85f, 0.5f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _label.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            _value = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            _bindButton = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            _sub = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _sub.normal.textColor = new Color(0.75f, 0.8f, 0.85f);
            _warn = new GUIStyle(_label) { fontStyle = FontStyle.Bold, wordWrap = true };
            _warn.normal.textColor = new Color(1f, 0.72f, 0.2f);
        }

        private bool HoldToggle(string name, bool toggle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(300));
            if (GUILayout.Toggle(!toggle, " Hold", GUILayout.Width(80)) && toggle) toggle = false;
            if (GUILayout.Toggle(toggle, " Toggle", GUILayout.Width(90)) && !toggle) toggle = true;
            GUILayout.EndHorizontal();
            return toggle;
        }

        private void OnGUI()
        {
            if (!_open || _player == null) return;
            Styles();
            // Lay out at 1080p and scale to the screen.
            float k = Screen.height / 1080f;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float w = Screen.width / k, h = 1080f;
            var box = new Rect(w / 2f - 380, 70, 760, h - 140);
            GUI.DrawTexture(box, _panel);
            GUILayout.BeginArea(new Rect(box.x + 28, box.y + 20, box.width - 56, box.height - 40));

            GUILayout.Label("Settings", _title);
            GUILayout.Space(8);
            GUILayout.Label("View", _header);
            _settings.Fov = Slider("Field of view (horizontal at 16:9)", _settings.Fov, ClientSettings.MinFov, ClientSettings.MaxFov, "0");
            _settings.Sensitivity = Slider("Mouse sensitivity", _settings.Sensitivity, ClientSettings.MinSensitivity, ClientSettings.MaxSensitivity, "0.00");
            GUILayout.Label($"   {_settings.Sensitivity * 0.022f:0.0000} degrees per mouse count", _label);
            _settings.ViewmodelSize = Slider("Weapon size", _settings.ViewmodelSize, ClientSettings.MinViewmodel, ClientSettings.MaxViewmodel, "0.00");
            _settings.HeadBob = Toggle("Head bob", _settings.HeadBob);
            _settings.SpeedFovKick = Toggle("FOV kick at high speed", _settings.SpeedFovKick);
            _settings.SpeedLines = Toggle("Speed lines", _settings.SpeedLines);
            GUILayout.Space(6);
            VideoSettingsPanel.Draw(_header, _label, _value);
            GUILayout.Space(6);
            GUILayout.Label("Audio", _header);
            _settings.MasterVolume = Slider("Master volume", _settings.MasterVolume, 0f, 1f, "0%", 100f);
            _settings.AmbienceVolume = Slider("Ambience (wind, birds)", _settings.AmbienceVolume, 0f, 1f, "0%", 100f);
            GUILayout.Space(6);
            GUILayout.Label("Controls", _header);
            _settings.SprintToggle = HoldToggle("Sprint", _settings.SprintToggle);
            _settings.CrouchToggle = HoldToggle("Crouch", _settings.CrouchToggle);
            _settings.ZoomToggle = HoldToggle("Zoom (right mouse, AR and pistol)", _settings.ZoomToggle);

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Key binds", _header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset all binds", _button, GUILayout.Width(150))) ResetBinds();
            GUILayout.EndHorizontal();
            GUILayout.Label(_rebind != null ? "Press a key or mouse button for the highlighted bind (Esc cancels)." : "Click a bind to change it.", _label);
            if (_clashA != null)
            {
                string key = _clashA.GetBindingDisplayString(_clashAIndex);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{key} is also bound to {_clashB.name}.", _warn, GUILayout.Width(380));
                string was = string.IsNullOrEmpty(_clashOldPathA) ? "nothing" : InputControlPath.ToHumanReadableString(_clashOldPathA, InputControlPath.HumanReadableStringOptions.OmitDevice);
                if (GUILayout.Button($"Swap ({_clashB.name} gets {was})", _button)) SwapClash();
                if (GUILayout.Button("Keep both", _button, GUILayout.Width(110))) _clashA = null;
                GUILayout.EndHorizontal();
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (Row r in _rows)
            {
                if (r.Header != null)
                {
                    GUILayout.Space(6);
                    GUILayout.Label(r.Header, _sub);
                    continue;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(r.Label, _label, GUILayout.Width(300));
                bool waiting = _rebind != null && _rebindAction == r.Action && _rebindIndex == r.Index;
                string shown = waiting ? "..." : r.Action.GetBindingDisplayString(r.Index);
                if (string.IsNullOrEmpty(shown)) shown = "(none)";
                if (GUILayout.Button(shown, _bindButton, GUILayout.Width(220)) && _rebind == null) StartRebind(r.Action, r.Index);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Resume (Esc)", _button, GUILayout.Height(32))) Close();
            if (GUILayout.Button("Leave to main menu", _button, GUILayout.Height(32), GUILayout.Width(190))) { Close(); Sculpt.Game.Bootstrap.GameBootstrap.Relaunch(Sculpt.Game.Bootstrap.GameBootstrap.Launch.Menu, Sculpt.Game.Bootstrap.GameBootstrap.CurrentMap); }
            if (GUILayout.Button("Quit game", _button, GUILayout.Height(32), GUILayout.Width(140))) Application.Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        private float Slider(string name, float v, float min, float max, string fmt, float show = 1f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(300));
            float n = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(300));
            string txt = fmt.EndsWith("%") ? (n * show).ToString("0") + "%" : n.ToString(fmt);
            GUILayout.Label(txt, _value, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            return n;
        }

        private bool Toggle(string name, bool v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(300));
            bool n = GUILayout.Toggle(v, v ? " On" : " Off");
            GUILayout.EndHorizontal();
            return n;
        }

        private void OnDestroy()
        {
            _rebind?.Dispose();
            if (_panel != null) Destroy(_panel);
        }
    }
}
