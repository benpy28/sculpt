using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
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
        private NetSession _session;
        private bool _joinBox;
        private string _joinText = "";
        private int _corrWindowStart;
        private float _corrWindowTime;
        private float _corrPerSec;

        private static readonly int[] PingSteps = { 0, 50, 100, 150, 250 };
        private static readonly float[] LossSteps = { 0, 2, 5, 10 };
        private static readonly int[] JitterSteps = { 0, 5, 15, 30 };
        private const string JoinKey = "sculpt.joinAddress";

        public void Init(LocalPlayer player, ClientSettings settings, NetSession session)
        {
            _player = player;
            _settings = settings;
            _session = session;
            _white = Texture2D.whiteTexture;
            _joinText = PlayerPrefs.GetString(JoinKey, "127.0.0.1");
        }

        private int SimPingMs => (int)Mathf.Round((float)(_session.SimUp.LatencyMs + _session.SimDown.LatencyMs));

        private void SetSimPing(int rttMs)
        {
            _session.SimUp.LatencyMs = rttMs / 2.0;
            _session.SimDown.LatencyMs = rttMs / 2.0;
        }

        private static T Next<T>(T[] steps, System.Func<T, bool> isCurrent)
        {
            for (int i = 0; i < steps.Length; i++) if (isCurrent(steps[i])) return steps[(i + 1) % steps.Length];
            return steps[0];
        }

        private void NetKeys(Sculpt.Game.Input.GameInput input)
        {
            if (_joinBox) return; // typing an address
            if (input.DevSimPing.WasPressedThisFrame()) SetSimPing(Next(PingSteps, v => v == SimPingMs));
            if (input.DevSimLoss.WasPressedThisFrame())
            {
                float l = Next(LossSteps, v => Mathf.Approximately(v, (float)_session.SimUp.LossPercent));
                _session.SimUp.LossPercent = l;
                _session.SimDown.LossPercent = l;
            }
            if (input.DevSimJitter.WasPressedThisFrame())
            {
                int j = Next(JitterSteps, v => v == (int)_session.SimUp.JitterMs);
                _session.SimUp.JitterMs = j;
                _session.SimDown.JitterMs = j;
            }
            if (input.DevHost.WasPressedThisFrame())
            {
                if (_session.Mode == NetMode.Offline) _session.StartHost(_session.Port);
                else _session.StartOffline();
            }
            if (input.DevJoin.WasPressedThisFrame())
            {
                _joinBox = true;
                _player.InputBlocked = true;
            }
        }

        private void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            var input = _player.Input;
            if (input == null || _settings == null || !Sculpt.Game.Input.GameInput.DevBuild) { _show = false; return; }
            if (_player.InputBlocked) return; // the settings menu or the join box has the keyboard
            if (input.DevHud.WasPressedThisFrame()) _show = !_show;
            NetKeys(input);
        }

        private void LateUpdate()
        {
            if (_player == null) return;
            NetClient net = _session.Client;
            _corrWindowTime += Time.unscaledDeltaTime;
            if (_corrWindowTime >= 1f)
            {
                _corrPerSec = Mathf.Max(0, net.Corrections - _corrWindowStart) / _corrWindowTime;
                _corrWindowStart = net.Corrections;
                _corrWindowTime = 0;
            }

            foreach (var e in _player.Events)
            {
                if (e.Type == MoveEventType.Landed && e.Value > 0) _lastDamage = e.Value;
                if (e.Type == MoveEventType.Landed && e.Value == 0) continue;
                if (e.Type == MoveEventType.Jumped) continue;
                _lastEvent = e.Type == MoveEventType.Landed ? $"Landed: {e.Amount:0.0} levels, {e.Value} dmg" : e.Type.ToString();
                _lastEventTime = Time.time;
            }
        }

        private void OnGUI()
        {
            if (_player == null) return;
            if (!_show) return; // the crosshair lives in CombatHud now (it shows the real spread)

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
                $"Height {s.Position.Y / SubGrid.TileSize:0.00} levels   FPS {_fps:0}\n" +
                (Time.time - _lastEventTime < 1.5f ? $">> {_lastEvent}\n" : "\n") +
                (_lastDamage > 0 ? $"Last fall damage {_lastDamage}\n" : "") +
                "\nF1 respawn   F2 hide this   Esc settings (FOV, sensitivity, binds, volume)\n\n" + NetText();
            var r = new Rect(12, 10, 640, 480);
            var shadow = new GUIStyle(_style);
            shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, shadow);
            GUI.Label(r, text, _style);

            if (!_player.CursorLocked && !_player.InputBlocked)
                GUI.Label(new Rect(Screen.width / 2f - 120, Screen.height / 2f + 30, 300, 30), "Click to capture the mouse", _style);
            if (_joinBox) DrawJoinBox();
        }

        private string NetText()
        {
            NetClient n = _session.Client;
            string mode = _session.Mode == NetMode.Client ? "Client" : _session.Mode == NetMode.Host ? "Host" : "Offline";
            double tickMs = 1000.0 / SimTime.TicksPerSecond;
            string lead = n.LastServerLead == Protocol.NoLead ? "-" : n.LastServerLead.ToString();
            string t =
                $"NET {mode}: {_session.Status}\n" +
                $"Round trip ~{System.Math.Max(0, n.RttTicks) * tickMs:0} ms   Input lead {lead} ticks   Clock {n.Dilation * 100:+0.0;-0.0;0}%\n" +
                $"Corrections {n.Corrections} ({_corrPerSec:0.0}/s, last {n.LastCorrectionMeters * 100:0} cm)   Remote players {n.RemoteCount}\n";
            ServerPlayer sp = _session.LocalServerPlayer;
            if (n.Sculpt != null) t += $"Sculpt: bar {n.Sculpt.DisplayBar / 100}%   pending {n.Sculpt.Local.Count}   mispredicted {n.Sculpt.Mispredictions}   server refused {n.Sculpt.ServerRejections}\n";
            if (sp != null) t += $"Server: tick {_session.Server.Tick}, players {_session.Server.Players.Count}, your missed inputs {sp.MissedInputs}, late {sp.LateInputs}\n";
            t += $"Sim: ping {SimPingMs} ms (F8)  loss {_session.SimUp.LossPercent:0}% (F9)  jitter +/-{_session.SimUp.JitterMs:0} ms (F10)\n" +
                 $"F11 {(_session.Mode == NetMode.Offline ? "host on port " + _session.Port : "go offline")}   F12 join a server";
            return t;
        }

        private void DrawJoinBox()
        {
            var box = new Rect(Screen.width / 2f - 180, Screen.height / 2f - 50, 360, 100);
            GUI.Box(box, "Join server (address, Enter to join, Esc to cancel)");
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                string addr = _joinText.Trim();
                if (addr.Length > 0)
                {
                    PlayerPrefs.SetString(JoinKey, addr);
                    PlayerPrefs.Save();
                    ushort port = _session.Port;
                    int colon = addr.LastIndexOf(':');
                    if (colon > 0 && ushort.TryParse(addr.Substring(colon + 1), out ushort p)) { port = p; addr = addr.Substring(0, colon); }
                    _session.Join(addr, port);
                }
                CloseJoinBox();
                e.Use();
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                CloseJoinBox();
                e.Use();
                return;
            }
            GUI.SetNextControlName("joinField");
            _joinText = GUI.TextField(new Rect(box.x + 20, box.y + 40, box.width - 40, 26), _joinText, 64);
            GUI.FocusControl("joinField");
        }

        private void CloseJoinBox()
        {
            _joinBox = false;
            _player.InputBlocked = false;
        }
    }
}
