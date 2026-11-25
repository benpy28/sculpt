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
        private bool _show; // hidden by default (F2), so it never sits over the HUD
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
        private GUIStyle _big;
        private float _peak, _peakTime;
        private readonly float[] _trail = new float[180]; // last 3 s of speed, one sample per frame-tick
        private int _trailAt;

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
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                _style.normal.textColor = Color.white;
            }
            // The speedometer shows with the dev HUD, and always while the TAS runs (F7) or you race it (F6).
            if (_show || _player.Showcase != null || _player.Race != null) DrawSpeedometer();
            if (!_show) return; // the crosshair lives in CombatHud now (it shows the real spread)


            MotorState s = _player.State;
            string text =
                $"Speed {s.HorizontalSpeed:0.00} m/s   vY {s.Velocity.Y:0.00}\n" +
                $"Mode {s.Mode}{(s.Crouched ? " (crouched)" : "")}{(s.Sprinting ? " sprint" : "")}   Grounded {s.Grounded}\n" +
                $"Can shoot {s.CanShoot}   Slide boost ready {(s.Tick - s.LastSlideBoostTick >= _player.Config.SlideBoostCooldownTicks ? "yes" : "no")}\n" +
                $"Height {s.Position.Y / SubGrid.TileSize:0.00} levels   FPS {_fps:0}\n" +
                (Time.time - _lastEventTime < 1.5f ? $">> {_lastEvent}\n" : "\n") +
                (_lastDamage > 0 ? $"Last fall damage {_lastDamage}\n" : "") +
                ShowcaseText() +
                "\nF1 respawn   F2 hide this   F6 race the TAS   F7 watch the TAS   Esc settings (FOV, sensitivity, binds, volume)\n\n" + NetText();
            var r = new Rect(12, 10, 640, 480);
            var shadow = new GUIStyle(_style);
            shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, shadow);
            GUI.Label(r, text, _style);

            if (!_player.CursorLocked && !_player.InputBlocked)
                GUI.Label(new Rect(Screen.width / 2f - 120, Screen.height / 2f + 30, 300, 30), "Click to capture the mouse", _style);
            if (_joinBox) DrawJoinBox();
        }

        private string ShowcaseText()
        {
            ShowcaseRun r = _player.Showcase ?? _player.LastShowcase;
            if (r == null) return "";
            string state = _player.Showcase != null ? $"running: {r.Current}" :
                r.Current == ShowcaseRun.Phase.Done ? "finished" : r.Current == ShowcaseRun.Phase.Failed ? "failed: " + r.FailReason : "stopped";
            return $"Showcase {state}   {r.Ticks / (float)SimTime.TicksPerSecond:0.00} s   top {r.TopSpeed:0.0} m/s\n";
        }

        /// <summary>
        /// Speedometer (Ben 2026-11-21): big horizontal speed readout, a bar with marks at run, sprint, the charge
        /// breach threshold (80% of sprint) and the slide cap, the peak of the last 3 s and a short trace.
        /// </summary>
        private void DrawSpeedometer()
        {
            MovementConfig c = _player.Config;
            float v = _player.RenderSpeed;
            if (Event.current.type == EventType.Repaint)
            {
                if (v >= _peak || Time.time - _peakTime > 3f) { _peak = v; _peakTime = Time.time; }
                _trail[_trailAt] = v;
                _trailAt = (_trailAt + 1) % _trail.Length;
            }

            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _big.normal.textColor = Color.white;
            }
            const float maxV = 16f, w = 320f, h = 10f;
            float x = Screen.width / 2f - w / 2f, y = Screen.height - 150f;
            var back = new Color(0, 0, 0, 0.45f);
            GUI.color = back;
            GUI.DrawTexture(new Rect(x - 10, y - 52, w + 20, 104), _white);

            // Number.
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y - 50, w, 40), $"{v:0.0} m/s", _big);

            // Bar: white up to sprint, amber above it (momentum), red above the slide cap.
            float fill = Mathf.Clamp01(v / maxV) * w;
            GUI.color = new Color(1, 1, 1, 0.15f);
            GUI.DrawTexture(new Rect(x, y, w, h), _white);
            GUI.color = v <= c.SprintSpeed + 0.01f ? Color.white : v <= c.SlideMaxSpeed + 0.01f ? new Color(1f, 0.72f, 0.2f) : new Color(1f, 0.3f, 0.25f);
            GUI.DrawTexture(new Rect(x, y, fill, h), _white);
            // Peak marker.
            GUI.color = new Color(1, 1, 1, 0.9f);
            GUI.DrawTexture(new Rect(x + Mathf.Clamp01(_peak / maxV) * w - 1, y - 3, 2, h + 6), _white);

            // Marks with labels.
            Mark(x, y, w, h, (float)c.RunSpeed, maxV, "run");
            Mark(x, y, w, h, (float)c.SprintSpeed, maxV, "sprint");
            double chargeAt = _session.Client.Walls != null ? _session.Client.Walls.Config.ChargeSpeedFraction : 0.8;
            Mark(x, y, w, h, (float)(c.SprintSpeed * chargeAt), maxV, "charge");
            Mark(x, y, w, h, (float)c.SlideMaxSpeed, maxV, "cap");

            // Trace of the last 3 s.
            float ty = y + 44f, th = 22f, step = w / _trail.Length;
            GUI.color = new Color(1f, 0.85f, 0.4f, 0.8f);
            for (int i = 0; i < _trail.Length; i++)
            {
                float s = _trail[(_trailAt + i) % _trail.Length];
                float bh = Mathf.Clamp01(s / maxV) * th;
                GUI.DrawTexture(new Rect(x + i * step, ty - bh, Mathf.Max(1f, step), bh), _white);
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y + 12, w, 18), $"peak {_peak:0.0}", _style);
        }

        private void Mark(float x, float y, float w, float h, float at, float maxV, string label)
        {
            float mx = x + Mathf.Clamp01(at / maxV) * w;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.DrawTexture(new Rect(mx - 1, y - 2, 2, h + 4), _white);
            GUI.color = new Color(1, 1, 1, 0.7f);
            var st = new GUIStyle(_style) { fontSize = 11, alignment = TextAnchor.UpperCenter };
            GUI.Label(new Rect(mx - 30, y + h + 2, 60, 16), label, st);
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
