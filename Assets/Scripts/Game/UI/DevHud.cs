using Sculpt.Core;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Temporary M2 HUD (IMGUI): crosshair, movement readout and quick setting keys for the feel test.
    /// Replaced by the real HUD and settings menu in M4-M6.
    /// </summary>
    public sealed class DevHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private ClientSettings _settings;
        private bool _show = true;
        private Texture2D _white;
        private GUIStyle _style;
        private float _fps;
        private string _lastEvent = "";
        private float _lastEventTime;
        private int _lastDamage;

        public void Init(LocalPlayer player, ClientSettings settings)
        {
            _player = player;
            _settings = settings;
            _white = Texture2D.whiteTexture;
        }

        private void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            var kb = Keyboard.current;
            if (kb == null || _settings == null) return;
            bool changed = false;
            if (kb.f1Key.wasPressedThisFrame) _show = !_show;
            if (kb.f2Key.wasPressedThisFrame) { _settings.Fov = Mathf.Clamp(_settings.Fov - 5, 60, 120); changed = true; }
            if (kb.f3Key.wasPressedThisFrame) { _settings.Fov = Mathf.Clamp(_settings.Fov + 5, 60, 120); changed = true; }
            if (kb.f4Key.wasPressedThisFrame) { _settings.Sensitivity = Mathf.Max(0.1f, _settings.Sensitivity - 0.1f); changed = true; }
            if (kb.f5Key.wasPressedThisFrame) { _settings.Sensitivity += 0.1f; changed = true; }
            if (kb.f6Key.wasPressedThisFrame) { _settings.HeadBob = !_settings.HeadBob; changed = true; }
            if (kb.f7Key.wasPressedThisFrame) { _settings.SpeedFovKick = !_settings.SpeedFovKick; changed = true; }
            if (changed) _settings.Save();

            foreach (var e in _player.Events)
            {
                if (e.Type == MoveEventType.Landed && e.Value > 0) _lastDamage = e.Value;
                if (e.Type == MoveEventType.Landed && e.Value == 0) continue;
                if (e.Type == MoveEventType.Jumped) continue;
                _lastEvent = e.Type == MoveEventType.Landed ? $"Landed: {e.Amount:0.0} levels, {e.Value} dmg" : e.Type.ToString();
                _lastEventTime = Time.time;
            }
            _player.Events.Clear();
        }

        private void OnGUI()
        {
            if (_player == null) return;
            DrawCrosshair();
            if (!_show) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                _style.normal.textColor = Color.white;
            }

            MotorState s = _player.State;
            string text =
                $"Speed {s.HorizontalSpeed:0.00} m/s   vY {s.Velocity.Y:0.00}\n" +
                $"Mode {s.Mode}{(s.Crouched ? " (crouched)" : "")}{(s.Sprinting ? " sprint" : "")}   Grounded {s.Grounded}\n" +
                $"Can shoot {s.CanShoot}   Slide boost ready {(s.Tick - s.LastSlideBoostTick >= _player.Config.SlideBoostCooldownTicks ? "yes" : "no")}\n" +
                $"Height {s.Position.Y / VoxelGrid.BlockSize:0.00} levels   FPS {_fps:0}\n" +
                (Time.time - _lastEventTime < 1.5f ? $">> {_lastEvent}\n" : "\n") +
                (_lastDamage > 0 ? $"Last fall damage {_lastDamage}\n" : "") +
                $"\nFOV {_settings.Fov:0} (F2/F3)   Sens {_settings.Sensitivity:0.0} (F4/F5)\n" +
                $"Head bob {(_settings.HeadBob ? "on" : "off")} (F6)   Speed FOV kick {(_settings.SpeedFovKick ? "on" : "off")} (F7)\n" +
                "R respawn   Esc free mouse   F1 hide";
            var r = new Rect(12, 10, 560, 260);
            var shadow = new GUIStyle(_style);
            shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, shadow);
            GUI.Label(r, text, _style);

            if (!_player.CursorLocked)
                GUI.Label(new Rect(Screen.width / 2f - 120, Screen.height / 2f + 30, 300, 30), "Click to capture the mouse", _style);
        }

        private void DrawCrosshair()
        {
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            const float len = 7, gap = 4, th = 2;
            var outline = new Color(0, 0, 0, 0.7f);
            void Bar(float x, float y, float w, float h)
            {
                GUI.color = outline;
                GUI.DrawTexture(new Rect(x - 1, y - 1, w + 2, h + 2), _white);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x, y, w, h), _white);
            }
            Bar(cx - gap - len, cy - th / 2, len, th);
            Bar(cx + gap, cy - th / 2, len, th);
            Bar(cx - th / 2, cy - gap - len, th, len);
            Bar(cx - th / 2, cy + gap, th, len);
            GUI.color = Color.white;
        }
    }
}
