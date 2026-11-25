using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sculpt.Game.Bootstrap;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// Front menu for the friend demo: host a LAN 1v1, join one by IP, play the 1v1 against a bot, or practise offline (1v1 map, or the breach test
    /// map with the dummy and the movement course). Each choice reloads the scene with that map and start, so every mode
    /// builds the world the same way. Also shows "connecting..." while a join is under way, and why a join failed.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        private const string JoinKey = "sculpt.lastJoin";
        private LocalPlayer _player;
        private NetSession _session;
        private bool _open;
        private string _address = "";
        private string _error = "";
        private List<string> _ips;
        private GUIStyle _title, _sub, _label, _button, _field, _warn, _big;
        private Texture2D _panel, _shade;

        public bool IsOpen => _open;

        public void Init(LocalPlayer player, NetSession session)
        {
            _player = player;
            _session = session;
            _address = PlayerPrefs.GetString(JoinKey, "");
        }

        public void Open()
        {
            _open = true;
            _player.InputBlocked = true;
            LocalPlayer.LockCursor(false);
        }

        /// <summary>Frame the menu last closed (so the same Esc press doesn't also open the settings).</summary>
        public static int ClosedFrame = -10;

        private void Close()
        {
            ClosedFrame = Time.frameCount;
            _open = false;
            _player.InputBlocked = false;
        }

        private void Update()
        {
            if (_session == null) return;
            // A failed join drops back offline: bring the menu back with the reason.
            if (_session.JoinError.Length > 0 && _session.Mode != NetMode.Client && _error != _session.JoinError)
            {
                _error = _session.JoinError;
                Open();
            }
            if (_open && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame
                && _session.Mode != NetMode.Client && _ips != null)
                Close();
        }

        private void Styles()
        {
            if (_title != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.06f, 0.065f, 0.08f, 0.94f));
            _panel.Apply();
            _shade = new Texture2D(1, 1);
            _shade.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.45f));
            _shade.Apply();
            _title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = Color.white;
            _big = new GUIStyle(_title) { fontSize = 26 };
            _sub = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            _sub.normal.textColor = new Color(1f, 0.85f, 0.5f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _label.normal.textColor = new Color(0.88f, 0.9f, 0.92f);
            _warn = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _warn.normal.textColor = HudDraw.Red;
            _button = new GUIStyle(GUI.skin.button) { fontSize = 17 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
        }

        private void OnGUI()
        {
            if (_session == null) return;
            bool joining = _session.Mode == NetMode.Client && !_session.Client.Joined;
            if (!_open && !joining) return;
            Styles();
            float k = Screen.height / 1080f;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float w = Screen.width / k, h = 1080f;
            GUI.DrawTexture(new Rect(0, 0, w, h), _shade);
            if (joining) DrawJoining(w, h);
            else DrawMenu(w, h);
            GUI.matrix = old;
        }

        private void DrawJoining(float w, float h)
        {
            var box = new Rect(w / 2 - 300, h / 2 - 110, 600, 220);
            GUI.DrawTexture(box, _panel);
            GUILayout.BeginArea(new Rect(box.x + 30, box.y + 24, box.width - 60, box.height - 48));
            GUILayout.Label($"Connecting to {_session.JoinAddress}...", _big);
            GUILayout.Space(10);
            if (_session.JoinWaitSeconds > 4)
                GUILayout.Label("Still trying. Check both PCs are on the same network, the address is the host's LAN address (shown on the host's screen), and the host allowed SCULPT through Windows Firewall on private networks.", _label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", _button, GUILayout.Height(36))) GameBootstrap.Relaunch(GameBootstrap.Launch.Menu, GameBootstrap.CurrentMap);
            GUILayout.EndArea();
        }

        private void DrawMenu(float w, float h)
        {
            _ips ??= LanAddresses();
            var box = new Rect(w / 2 - 360, 110, 720, h - 220);
            GUI.DrawTexture(box, _panel);
            GUILayout.BeginArea(new Rect(box.x + 36, box.y + 26, box.width - 72, box.height - 52));
            GUILayout.Label("SCULPT", _title);
            GUILayout.Label("1v1: most kills in 5 minutes", new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(18);

            if (_error.Length > 0)
            {
                GUILayout.Label(_error, _warn);
                GUILayout.Space(8);
            }

            GUILayout.Label("Play on your local network", _sub);
            if (GUILayout.Button("Host a 1v1", _button, GUILayout.Height(44)))
                GameBootstrap.Relaunch(GameBootstrap.Launch.Host, GameBootstrap.MapChoice.Duel);
            GUILayout.Label(_ips.Count > 0
                ? $"Your friend joins with: {string.Join("  or  ", _ips)}   (UDP port {FishNetTransport.DefaultPort})"
                : "No network address found: are you connected to a network?", _label);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host's address", _label, GUILayout.Width(130), GUILayout.Height(40));
            GUI.SetNextControlName("joinField");
            _address = GUILayout.TextField(_address, 64, _field, GUILayout.Height(40));
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && GUI.GetNameOfFocusedControl() == "joinField";
            if ((GUILayout.Button("Join", _button, GUILayout.Width(120), GUILayout.Height(40)) || enter) && _address.Trim().Length > 0)
            {
                PlayerPrefs.SetString(JoinKey, _address.Trim());
                PlayerPrefs.Save();
                GameBootstrap.Relaunch(GameBootstrap.Launch.Join, GameBootstrap.MapChoice.Duel, _address.Trim());
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(22);
            GUILayout.Label("Play vs bot (the same 1v1 match, offline)", _sub);
            GUILayout.BeginHorizontal();
            foreach (Sculpt.Core.Net.BotLevel level in new[] { Sculpt.Core.Net.BotLevel.Easy, Sculpt.Core.Net.BotLevel.Normal, Sculpt.Core.Net.BotLevel.Hard })
                if (GUILayout.Button(level.ToString(), _button, GUILayout.Height(40)))
                {
                    GameBootstrap.NextBotLevel = level;
                    GameBootstrap.Relaunch(GameBootstrap.Launch.VsBot, GameBootstrap.MapChoice.Duel);
                }
            GUILayout.EndHorizontal();

            GUILayout.Space(22);
            GUILayout.Label("Practice", _sub);
            if (GUILayout.Button("Offline on the 1v1 map", _button, GUILayout.Height(40)))
                GameBootstrap.Relaunch(GameBootstrap.Launch.Offline, GameBootstrap.MapChoice.Duel);
            if (GUILayout.Button("Breach test map (practice dummy, movement course: F6 race the TAS)", _button, GUILayout.Height(40)))
                GameBootstrap.Relaunch(GameBootstrap.Launch.Offline, GameBootstrap.MapChoice.Breach);

            GUILayout.FlexibleSpace();
            GUILayout.Label("Esc in game: settings, binds and back to this menu.", _label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Back to game (Esc)", _button, GUILayout.Height(36))) { _error = _session.JoinError; Close(); }
            if (GUILayout.Button("Quit", _button, GUILayout.Width(140), GUILayout.Height(36))) Application.Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>This PC's IPv4 addresses on real networks (Wi-Fi, Ethernet), private ranges first.</summary>
        public static List<string> LanAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string n = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (n.Contains("virtual") || n.Contains("vmware") || n.Contains("hyper-v") || n.Contains("vethernet") || n.Contains("bluetooth")) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                        string s = a.Address.ToString();
                        if (s.StartsWith("169.254.")) continue; // no DHCP: not a usable network
                        if (!list.Contains(s)) list.Add(s);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[SCULPT] Could not list network addresses: " + e.Message);
            }
            list.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
            return list;
        }

        private static int Rank(string ip) => ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : ip.StartsWith("172.") ? 2 : 3;
    }
}
