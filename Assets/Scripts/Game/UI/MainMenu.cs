using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Bootstrap;
using Sculpt.Game.Cameras;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The home screen, laid out like Ben's approved mockup A "side panel" (2026-11-25): the 1v1 map behind it with a
    /// blasted brick wall (<see cref="MainMenuScene"/>), the cracked-S logo, a play panel on the left driven by the mode
    /// tiles under it, a what's-new card on the right and key hints at the bottom.
    ///
    /// Joining takes a LAN room code (the host's screen shows it; <see cref="LanRoomFinder"/> finds the host on the local
    /// network and the right map from its answer) or an address. The Royale card picks your locker gun (<see cref="LockerPick"/>).
    /// The footer is the server info line (roadmap item 22). Every launch fades through the <see cref="LoadingScreen"/>.
    ///
    /// Only what works today is on it (Ben: everything else on the mockup is future roadmap, no stubs): 1v1 Duel (host,
    /// or join by IP), vs Bot (Easy, Normal, Hard), Practice (free roam on the 1v1 map, the breach test map), settings
    /// and quit. Each launch choice reloads the scene with that map and start (GameBootstrap.Relaunch), as before. Also
    /// shows the "connecting..." card while a join is under way and why a join failed.
    ///
    /// Drawn with <see cref="HudDraw"/> (one batched draw for the shapes, glyph text), no GUILayout and no per-frame
    /// strings; only the join box is a GUI.TextField. Nothing is drawn or run while the menu is closed.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        private const string JoinKey = "sculpt.lastJoin";
        private const string ModeKey = "sculpt.menuMode";
        private LocalPlayer _player;
        private NetSession _session;
        private SettingsMenu _settings;
        private bool _open;
        private string _address = "";
        private string _error = "";
        private List<string> _ips;
        private string _ipText, _ipHint;
        private Material _gl;
        /// <summary>A room code lookup under way (Join by room code), or null.</summary>
        private LanRoomFinder _finder;
        private Mode _finderMode;
        // Joining by address: what to do if the host doesn't say what it runs (the tab's map, as before).
        private GameBootstrap.Launch _fallbackLaunch;
        private GameBootstrap.MapChoice _fallbackMap;
        private string _fallbackAddress;
        private Item _locker;

        public bool IsOpen => _open;

        // ---------------------------------------------------------------- Menu state

        private enum Mode { Duel, Bot, Practice, Replays, Br }
        private enum BrNet { Offline, Host, Join }
        private BrNet _brNet = BrNet.Offline;
        private enum WatchPick { Saved, Live }
        private WatchPick _watch = WatchPick.Saved;
        private List<ReplayLibrary.Entry> _replays;
        private int _replayAt;
        private enum DuelPick { Host, Join }
        private enum PracticePick { FreeRoam, Breach }

        private Mode _mode = Mode.Duel;
        private DuelPick _duel = DuelPick.Host;
        private PracticePick _practice = PracticePick.FreeRoam;
        private bool _focusJoin;

        // Background scene: camera, hidden HUDs and gun, the blasted wall.
        private CameraRig _rig;
        private ICameraMode _prevCamera;
        private readonly List<Behaviour> _hidden = new List<Behaviour>();
        private readonly List<GameObject> _hiddenObjects = new List<GameObject>();
        private List<int> _blasted;
        private bool _sceneOn;

        public void Init(LocalPlayer player, NetSession session)
        {
            _player = player;
            _session = session;
            _settings = GetComponent<SettingsMenu>();
            _address = PlayerPrefs.GetString(JoinKey, "");
            _locker = LockerPick.Load();
            _mode = (Mode)Mathf.Clamp(PlayerPrefs.GetInt(ModeKey, 0), 0, 4);
            if (GameBootstrap.ReplayError.Length > 0) { _mode = Mode.Replays; _error = GameBootstrap.ReplayError; }
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            ReadShotArgs();
        }

        public void Open()
        {
            _open = true;
            _replays = null; // re-read the replay folder (a match may just have been saved)
            _player.InputBlocked = true;
            LocalPlayer.LockCursor(false);
            SceneOn();
        }

        /// <summary>Frame the menu last closed (so the same Esc press doesn't also open the settings).</summary>
        public static int ClosedFrame = -10;

        private void Close()
        {
            ClosedFrame = Time.frameCount;
            _open = false;
            SceneOff();
            _player.InputBlocked = false;
            LocalPlayer.LockCursor(true);
        }

        private void OnDestroy()
        {
            StopFinding();
            if (_gl != null) Destroy(_gl);
        }

        // ---------------------------------------------------------------- Background scene

        private void SceneOn()
        {
            if (_sceneOn) return;
            _sceneOn = true;
            _rig ??= FindFirstObjectByType<CameraRig>();
            if (_rig != null)
            {
                _prevCamera = _rig.Mode;
                _rig.Mode = new MainMenuScene.MenuCamera();
                // The gun and fist hang off the camera: hide them while it is a menu camera.
                foreach (Transform child in _rig.Camera.transform)
                    if (child.gameObject.activeSelf) { child.gameObject.SetActive(false); _hiddenObjects.Add(child.gameObject); }
            }
            // The in-game HUD (crosshair, health, match timer...) has no place on the home screen. The FPS and latency
            // overlay stays if the player turned it on.
            foreach (MonoBehaviour b in GetComponents<MonoBehaviour>())
            {
                string n = b.GetType().Name;
                if (b.enabled && n.EndsWith("Hud") && n != "PerfHud") { b.enabled = false; _hidden.Add(b); }
            }
            if (_session.Mode == NetMode.Offline && GameBootstrap.CurrentMap == GameBootstrap.MapChoice.Duel)
                lock (_session.ServerLock) _blasted = MainMenuScene.Blast(_session.Server?.Walls);
        }

        private void SceneOff()
        {
            if (!_sceneOn) return;
            _sceneOn = false;
            if (_rig != null && _prevCamera != null) _rig.Mode = _prevCamera;
            foreach (GameObject g in _hiddenObjects) if (g != null) g.SetActive(true);
            _hiddenObjects.Clear();
            foreach (Behaviour b in _hidden) if (b != null) b.enabled = true;
            _hidden.Clear();
            lock (_session.ServerLock) MainMenuScene.Restore(_session.Server?.Walls, _blasted);
            _blasted = null;
        }

        // ---------------------------------------------------------------- Update and input

        private void Update()
        {
            if (_session == null) return;
            // A failed join drops back offline: bring the menu back with the reason.
            if (_session.JoinError.Length > 0 && _session.Mode != NetMode.Client && _error != _session.JoinError)
            {
                _error = _session.JoinError;
                _mode = Mode.Duel;
                _duel = DuelPick.Join;
                Open();
            }
            bool joining = _session.Mode == NetMode.Client && !_session.Client.Joined;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (_open && !joining && kb != null && kb.escapeKey.wasPressedThisFrame && _settings != null && !_settings.IsOpen
                && Time.frameCount != SettingsMenu.ClosedFrame)
                _settings.Open();
            if (_shotPath != null) TakeShot();
            PollFinder();
        }

        // ---------------------------------------------------------------- Room codes

        /// <summary>
        /// The join box takes a room code or an address. A code is looked up on the local network first; whatever
        /// answers is joined at its own address and port, on the map it says it runs.
        /// </summary>
        private void StartFinding(string code, Mode mode)
        {
            StopFinding();
            _error = "";
            _finder = new LanRoomFinder(code);
            _finderMode = mode;
        }

        private void StopFinding()
        {
            _finder?.Dispose();
            _finder = null;
        }

        private void PollFinder()
        {
            if (_finder == null) return;
            _finder.Poll();
            switch (_finder.Now)
            {
                case LanRoomFinder.State.Looking: return;
                case LanRoomFinder.State.Found:
                {
                    LanRoomReply r = _finder.Reply;
                    // By address: join the port the player typed (the host's game port says the same unless they differ).
                    string addr = _finder.ByAddress ? _fallbackAddress
                        : _finder.Host.ToString() + (r.GamePort != FishNetTransport.DefaultPort ? ":" + r.GamePort : "");
                    var map = r.Map == LanRoomMap.BattleRoyale ? GameBootstrap.MapChoice.BattleRoyale
                        : r.Map == LanRoomMap.Breach ? GameBootstrap.MapChoice.Breach : GameBootstrap.MapChoice.Duel;
                    if (r.Map == LanRoomMap.BattleRoyale && r.BrMode != BrMode.None) GameBootstrap.NextBrMode = r.BrMode;
                    string what = r.Map == LanRoomMap.BattleRoyale ? "Battle royale " + r.BrMode : r.Map == LanRoomMap.Breach ? "Breach test map" : "1v1";
                    string note = _finder.ByAddress ? $"{what}  ·  {addr}  ·  {_finder.PingMs} ms" : $"Room {r.Code}  ·  {what}  ·  {addr}  ·  {_finder.PingMs} ms";
                    bool watch = _finderMode == Mode.Replays;
                    StopFinding();
                    PlayerPrefs.SetInt(ModeKey, (int)_mode);
                    PlayerPrefs.Save();
                    GameBootstrap.Relaunch(watch ? GameBootstrap.Launch.Spectate : GameBootstrap.Launch.Join, map, addr, note);
                    return;
                }
                case LanRoomFinder.State.NotFound when _finder.ByAddress:
                case LanRoomFinder.State.Failed when _finder.ByAddress:
                {
                    // No answer about what it runs (a firewall on the discovery port, or an older build): join as
                    // before, on the tab's map, and say so on the loading card.
                    string addr = _fallbackAddress;
                    var launch = _fallbackLaunch;
                    var map = _fallbackMap;
                    StopFinding();
                    PlayerPrefs.SetInt(ModeKey, (int)_mode);
                    PlayerPrefs.Save();
                    string what = map == GameBootstrap.MapChoice.BattleRoyale ? "the battle royale" : map == GameBootstrap.MapChoice.Breach ? "the breach test map" : "the 1v1";
                    GameBootstrap.Relaunch(launch, map, addr, $"{addr} didn't say what it hosts: joining {what}");
                    return;
                }
                default:
                    _error = _finder.Error;
                    StopFinding();
                    return;
            }
        }

        /// <summary>The address part of what was typed ("192.168.1.20", "192.168.1.20:7770", "localhost"), or null.</summary>
        private static System.Net.IPAddress ParseHost(string typed)
        {
            string host = typed;
            int colon = typed.LastIndexOf(':');
            if (colon > 0 && typed.IndexOf(':') == colon) host = typed.Substring(0, colon);
            if (string.Equals(host, "localhost", System.StringComparison.OrdinalIgnoreCase)) return System.Net.IPAddress.Loopback;
            return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ip : null;
        }

        /// <summary>Join (or watch) what the box says: a room code is looked up first, anything else is an address.</summary>
        private void JoinTyped(Mode mode, GameBootstrap.Launch launch, GameBootstrap.MapChoice map)
        {
            string a = _address.Trim();
            if (a.Length == 0) { _focusJoin = true; return; }
            PlayerPrefs.SetString(JoinKey, a);
            if (LanRoom.IsCode(a)) { StartFinding(a, mode); return; }
            // An address: ask it what it hosts first, so the right map loads whichever tab the address was typed in.
            System.Net.IPAddress ip = ParseHost(a);
            if (ip == null) { Launch(launch, map, a); return; }
            StopFinding();
            _error = "";
            _finder = new LanRoomFinder(ip);
            _finderMode = mode;
            _fallbackLaunch = launch;
            _fallbackMap = map;
            _fallbackAddress = a;
        }

        private void Launch(GameBootstrap.Launch launch, GameBootstrap.MapChoice map, string address = "")
        {
            PlayerPrefs.SetInt(ModeKey, (int)_mode);
            PlayerPrefs.Save();
            GameBootstrap.Relaunch(launch, map, address);
        }

        /// <summary>The big button: whatever the panel shows.</summary>
        private void Play()
        {
            // The big button cancels a room code lookup under way.
            if (_finder != null) { StopFinding(); return; }
            switch (_mode)
            {
                case Mode.Duel when _duel == DuelPick.Host:
                    Launch(GameBootstrap.Launch.Host, GameBootstrap.MapChoice.Duel);
                    break;
                case Mode.Duel:
                    JoinTyped(Mode.Duel, GameBootstrap.Launch.Join, GameBootstrap.MapChoice.Duel);
                    break;
                case Mode.Bot:
                    Launch(GameBootstrap.Launch.VsBot, GameBootstrap.MapChoice.Duel);
                    break;
                case Mode.Br when _brNet == BrNet.Join:
                    JoinTyped(Mode.Br, GameBootstrap.Launch.Join, GameBootstrap.MapChoice.BattleRoyale);
                    break;
                case Mode.Br:
                    Launch(_brNet == BrNet.Host ? GameBootstrap.Launch.Host : GameBootstrap.Launch.Offline, GameBootstrap.MapChoice.BattleRoyale);
                    break;
                case Mode.Replays when _watch == WatchPick.Saved:
                {
                    ReplayLibrary.Entry? r = SelectedReplay;
                    if (r == null) return;
                    if (r.Value.Problem != null) { _error = "That replay can't be played: " + r.Value.Problem + "."; return; }
                    PlayerPrefs.SetInt(ModeKey, (int)_mode);
                    PlayerPrefs.Save();
                    GameBootstrap.RelaunchReplay(r.Value.Path, r.Value.Map);
                    break;
                }
                case Mode.Replays:
                    JoinTyped(Mode.Replays, GameBootstrap.Launch.Spectate, GameBootstrap.MapChoice.Duel);
                    break;
                case Mode.Practice when _practice == PracticePick.FreeRoam:
                    // Already offline on the 1v1 map behind the menu: just step in.
                    if (_session.Mode == NetMode.Offline && GameBootstrap.CurrentMap == GameBootstrap.MapChoice.Duel)
                    {
                        _error = "";
                        PlayerPrefs.SetInt(ModeKey, (int)_mode);
                        Close();
                    }
                    else Launch(GameBootstrap.Launch.Offline, GameBootstrap.MapChoice.Duel);
                    break;
                default:
                    Launch(GameBootstrap.Launch.Offline, GameBootstrap.MapChoice.Breach);
                    break;
            }
        }

        // ---------------------------------------------------------------- Drawing helpers (design space: 1080 tall)

        private enum Phase { Shapes, Text }
        private Phase _phase;
        private float _k, _w;
        private Vector2 _mouse;

        private static readonly Color Ink = new Color(0.07f, 0.06f, 0.05f, 1f);
        private static readonly Color PanelBg = new Color(0.07f, 0.09f, 0.11f, 0.74f);
        private static readonly Color CardBg = new Color(0f, 0f, 0f, 0.32f);
        private static readonly Color CardHover = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Edge = new Color(1f, 1f, 1f, 0.09f);
        private static readonly Color Soft = new Color(0.96f, 0.97f, 0.98f, 0.72f);
        private static readonly Color Faint = new Color(0.96f, 0.97f, 0.98f, 0.42f);
        private static readonly Color AmberHi = new Color(1f, 0.79f, 0.36f, 1f);
        private static readonly Color AmberLo = new Color(0.96f, 0.58f, 0.17f, 1f);

        private Rect G(float x, float y, float w, float h) => new Rect(x * _k, y * _k, w * _k, h * _k);
        private bool Hot(float x, float y, float w, float h) => G(x, y, w, h).Contains(_mouse);

        private void Fill(float x, float y, float w, float h, float r, Color c)
        {
            if (_phase != Phase.Shapes) return;
            HudDraw.RoundRect(x * _k, Screen.height - (y + h) * _k, w * _k, h * _k, r * _k, c);
        }

        /// <summary>Rounded outline: four edges and four corner arcs.</summary>
        private void Outline(float x, float y, float w, float h, float r, float t, Color c)
        {
            if (_phase != Phase.Shapes) return;
            float X = x * _k, Y = Screen.height - (y + h) * _k, Wd = w * _k, Hd = h * _k, R = r * _k, T = Mathf.Max(1f, t * _k);
            HudDraw.Quad(X + R, Y, Wd - 2 * R, T, c);
            HudDraw.Quad(X + R, Y + Hd - T, Wd - 2 * R, T, c);
            HudDraw.Quad(X, Y + R, T, Hd - 2 * R, c);
            HudDraw.Quad(X + Wd - T, Y + R, T, Hd - 2 * R, c);
            float m = R - T * 0.5f;
            HudDraw.Arc(X + R, Y + Hd - R, m, T, 270f, 90f, c);
            HudDraw.Arc(X + Wd - R, Y + Hd - R, m, T, 0f, 90f, c);
            HudDraw.Arc(X + Wd - R, Y + R, m, T, 90f, 90f, c);
            HudDraw.Arc(X + R, Y + R, m, T, 180f, 90f, c);
        }

        /// <summary>Rectangle shaded from left to right.</summary>
        private void GradH(float x, float y, float w, float h, Color left, Color right)
        {
            if (_phase != Phase.Shapes) return;
            float x0 = x * _k, x1 = (x + w) * _k, y0 = Screen.height - (y + h) * _k, y1 = Screen.height - y * _k;
            HudDraw.Quad(new Vector2(x0, y0), new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(x1, y0), left, right);
        }

        /// <summary>Rectangle shaded from top to bottom.</summary>
        private void GradV(float x, float y, float w, float h, Color top, Color bottom)
        {
            if (_phase != Phase.Shapes) return;
            float x0 = x * _k, x1 = (x + w) * _k, y0 = Screen.height - (y + h) * _k, y1 = Screen.height - y * _k;
            HudDraw.Quad(new Vector2(x0, y1), new Vector2(x1, y1), new Vector2(x1, y0), new Vector2(x0, y0), top, bottom);
        }

        private void Text(float x, float y, float w, float h, string s, float size, Color c, TextAnchor a = TextAnchor.MiddleLeft, bool bold = false)
        {
            if (_phase != Phase.Text) return;
            HudDraw.Label(G(x, y, w, h), s, Mathf.RoundToInt(size * _k), c, a, bold);
        }

        // Letter-spaced capitals (logo, small headings, the big button): one glyph at a time, measured from the built-in font.
        private static Font _font;
        private static readonly string[] Chars = new string[128];

        private static readonly Dictionary<char, string> Wide = new Dictionary<char, string>();
        private static string One(char ch)
        {
            if (ch < 128) return Chars[ch] ??= ch.ToString();
            if (!Wide.TryGetValue(ch, out string s)) Wide[ch] = s = ch.ToString();
            return s;
        }

        private static float Advance(char ch, int px, bool bold)
        {
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null) return px * 0.6f;
            FontStyle fs = bold ? FontStyle.Bold : FontStyle.Normal;
            if (!_font.GetCharacterInfo(ch, out CharacterInfo ci, px, fs))
            {
                _font.RequestCharactersInTexture(One(ch), px, fs);
                if (!_font.GetCharacterInfo(ch, out ci, px, fs)) return px * 0.6f;
            }
            return ci.advance;
        }

        /// <summary>Width in design units of letter-spaced text.</summary>
        private float SpacedWidth(string s, float size, bool bold, float spacing)
        {
            int px = Mathf.Max(1, Mathf.RoundToInt(size * _k));
            float wpx = 0;
            for (int i = 0; i < s.Length; i++) wpx += Advance(s[i], px, bold);
            return wpx / _k + spacing * Mathf.Max(0, s.Length - 1);
        }

        private void Spaced(float x, float y, float h, string s, float size, Color c, bool bold, float spacing, int amberIndex = -1)
        {
            if (_phase != Phase.Text || Event.current.type != EventType.Repaint) return;
            int px = Mathf.Max(1, Mathf.RoundToInt(size * _k));
            float pen = x * _k;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch != ' ') HudDraw.Label(new Rect(pen, y * _k, px * 2f, h * _k), One(ch), px, i == amberIndex ? HudDraw.Amber : c, TextAnchor.MiddleLeft, bold);
                pen += Advance(ch, px, bold) + spacing * _k;
            }
        }

        /// <summary>A click area. Draws nothing; returns true on click.</summary>
        private bool Click(float x, float y, float w, float h)
        {
            if (_phase != Phase.Text) return false;
            return GUI.Button(G(x, y, w, h), GUIContent.none, GUIStyle.none);
        }

        // ---------------------------------------------------------------- OnGUI

        private void OnGUI()
        {
            if (_session == null) return;
            bool joining = _session.Mode == NetMode.Client && !_session.Client.Joined;
            if (!_open && !joining) return;
            if (_settings != null && _settings.IsOpen) return; // the settings draw on their own over the scene
            Event e = Event.current;
            _k = Screen.height / 1080f;
            _w = Screen.width / _k;
            _mouse = e.mousePosition;
            _ips ??= LanAddresses();
            if (_ipText == null)
            {
                _ipText = _ips.Count > 0 ? _ips[0] : "No network";
                _ipHint = _ips.Count > 0
                    ? "Your friend picks Join by IP and types your address. Allow the firewall if asked."
                    : "No network address found: connect to Wi-Fi or Ethernet to host.";
            }

            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (e.type == EventType.Repaint)
            {
                _phase = Phase.Shapes;
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    HudDraw.BeginBatch();
                    if (joining) DrawJoining(); else DrawHome();
                }
                finally
                {
                    HudDraw.EndBatch();
                    GL.PopMatrix();
                }
            }
            _phase = Phase.Text;
            if (joining) DrawJoining(); else DrawHome();
            if (enter && !joining && _open)
            {
                e.Use();
                Play();
            }
        }

        // ---------------------------------------------------------------- Connecting card

        private void DrawJoining()
        {
            Fill(0, 0, _w, 1080, 0, new Color(0, 0, 0, 0.35f));
            float bw = 660, bh = 230, bx = _w / 2 - bw / 2, by = 540 - bh / 2;
            Fill(bx, by, bw, bh, 14, PanelBg);
            Outline(bx, by, bw, bh, 14, 1, Edge);
            Spaced(bx + 32, by + 22, 20, "CONNECTING TO", 14, Faint, true, 2.5f);
            Text(bx + 32, by + 46, bw - 64, 44, _session.JoinAddress, 30, HudDraw.Text, TextAnchor.MiddleLeft, true);
            if (_session.JoinWaitSeconds > 4)
            {
                Text(bx + 32, by + 100, bw - 64, 22, "Still trying. Check both PCs are on the same network, the address is the", 16, Soft);
                Text(bx + 32, by + 122, bw - 64, 22, "host's (shown on their screen) and SCULPT is allowed through their firewall.", 16, Soft);
            }
            float cx = bx + bw - 32 - 150, cy = by + bh - 28 - 48;
            bool hot = Hot(cx, cy, 150, 48);
            Fill(cx, cy, 150, 48, 10, hot ? new Color(1, 1, 1, 0.2f) : new Color(1, 1, 1, 0.1f));
            Text(cx, cy, 150, 48, "Cancel", 18, HudDraw.Text, TextAnchor.MiddleCenter, true);
            if (Click(cx, cy, 150, 48)) GameBootstrap.Relaunch(GameBootstrap.Launch.Menu, GameBootstrap.MapChoice.Duel);
        }

        // ---------------------------------------------------------------- Home screen

        private void DrawHome()
        {
            // Shade: dark on the left behind the panel, darker bands under the top bar and the footer.
            GradH(0, 0, _w * 0.62f, 1080, new Color(0.02f, 0.03f, 0.05f, 0.72f), new Color(0.02f, 0.03f, 0.05f, 0f));
            GradV(0, 0, _w, 150, new Color(0.02f, 0.03f, 0.05f, 0.5f), new Color(0.02f, 0.03f, 0.05f, 0f));
            GradV(0, 960, _w, 120, new Color(0.02f, 0.03f, 0.05f, 0f), new Color(0.02f, 0.03f, 0.05f, 0.6f));

            DrawTopBar();
            DrawPlay();
            DrawNews();
            DrawFooter();
        }

        private void DrawTopBar()
        {
            // Logo: an amber disc with a cracked S, then the word mark.
            float lx = 76, ly = 82, lr = 27;
            if (_phase == Phase.Shapes)
            {
                HudDraw.Arc(lx * _k, Screen.height - ly * _k, lr * 0.5f * _k, lr * _k, 0, 360, HudDraw.Amber);
                HudDraw.Arc(lx * _k, Screen.height - ly * _k, (lr - 1.5f) * _k, 3f * _k, 0, 360, AmberLo);
            }
            Text(lx - lr, ly - lr - 1, lr * 2, lr * 2, "S", 40, Ink, TextAnchor.MiddleCenter, true);
            if (_phase == Phase.Text && Event.current.type == EventType.Repaint) Crack(lx, ly);
            Spaced(lx + 44, ly - 26, 52, "SCULPT", 42, HudDraw.Text, true, 7f, 4);

            // Right: settings and quit.
            float gx = _w - 48 - 56, gy = 54;
            bool gh = Hot(gx, gy, 56, 56);
            Fill(gx, gy, 56, 56, 12, gh ? new Color(1, 1, 1, 0.22f) : new Color(0.07f, 0.09f, 0.11f, 0.7f));
            Gear(gx + 28, gy + 28);
            if (Click(gx, gy, 56, 56) && _settings != null) _settings.Open();

            float qw = SpacedWidth("QUIT", 16, true, 2f) + 44, qx = gx - 12 - qw;
            bool qh = Hot(qx, gy, qw, 56);
            Fill(qx, gy, qw, 56, 12, qh ? new Color(1f, 0.27f, 0.22f, 0.55f) : new Color(0.07f, 0.09f, 0.11f, 0.7f));
            Spaced(qx + 22, gy, 56, "QUIT", 16, HudDraw.Text, true, 2f);
            if (Click(qx, gy, qw, 56)) Application.Quit();
        }

        /// <summary>The crack through the logo's S: a jagged amber line over the dark letter.</summary>
        private void Crack(float cx, float cy)
        {
            _gl.SetPass(0);
            GL.PushMatrix();
            HudDraw.LoadPixels();
            HudDraw.BeginBatch();
            Vector2 P(float dx, float dy) => new Vector2((cx + dx) * _k, Screen.height - (cy + dy) * _k);
            Seg(P(-17, -14), P(-5, -4), 2.6f);
            Seg(P(-5, -4), P(-8, 3), 2.6f);
            Seg(P(-8, 3), P(6, 9), 2.4f);
            Seg(P(6, 9), P(17, 18), 2.0f);
            Seg(P(-5, -4), P(3, -9), 1.6f);
            HudDraw.EndBatch();
            GL.PopMatrix();
        }

        private void Seg(Vector2 a, Vector2 b, float thick)
        {
            Vector2 n = (b - a).normalized;
            Vector2 o = new Vector2(-n.y, n.x) * (thick * 0.5f * _k);
            HudDraw.Quad(a - o, a + o, b + o, b - o, HudDraw.Amber);
        }

        private void Gear(float cx, float cy)
        {
            if (_phase != Phase.Shapes) return;
            float x = cx * _k, y = Screen.height - cy * _k;
            HudDraw.Arc(x, y, 8f * _k, 5f * _k, 0, 360, HudDraw.Text);
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                Vector2 d = new Vector2(Mathf.Sin(a), Mathf.Cos(a)), s = new Vector2(d.y, -d.x);
                Vector2 c0 = new Vector2(x, y) + d * (9.5f * _k), c1 = new Vector2(x, y) + d * (14f * _k);
                float hw = 2.6f * _k;
                HudDraw.Quad(c0 - s * hw, c0 + s * hw, c1 + s * hw, c1 - s * hw, HudDraw.Text);
            }
        }

        // ---------------------------------------------------------------- Play panel

        private const float LX = 76, PanelW = 630;

        private void DrawPlay()
        {
            Spaced(LX, 150, 22, "FEATURED  ·  1V1 DUEL", 15, Soft, true, 3f);
            Text(LX - 4, 168, 900, 110, "BREAK", 112, HudDraw.Text, TextAnchor.MiddleLeft, true);
            Text(LX - 4, 252, 900, 110, "THROUGH.", 112, HudDraw.Text, TextAnchor.MiddleLeft, true);
            Text(LX, 368, 720, 28, "Fast 1v1s where every wall can be broken. Blast your own way in,", 21, Soft);
            Text(LX, 398, 720, 28, "heal walls shut, and keep your speed through the gaps.", 21, Soft);

            float px = LX, py = 440, ph = 348;
            Fill(px + 2, py + 4, PanelW, ph, 16, new Color(0, 0, 0, 0.25f));
            Fill(px, py, PanelW, ph, 16, PanelBg);
            Outline(px, py, PanelW, ph, 16, 1, Edge);
            float ix = px + 24, iw = PanelW - 48;

            // Header: mode and map.
            Spaced(ix, py + 20, 18, "MODE", 13, Faint, true, 2.5f);
            Text(ix, py + 38, 400, 36, ModeName, 28, HudDraw.Text, TextAnchor.MiddleLeft, true);
            float mw = SpacedWidth("MAP", 13, true, 2.5f);
            Spaced(ix + iw - mw, py + 20, 18, "MAP", 13, Faint, true, 2.5f);
            Text(ix + iw - 300, py + 38, 300, 36, MapName, 20, HudDraw.Text, TextAnchor.MiddleRight, true);

            // Segments.
            float sy = py + 88, sh = 50;
            Fill(ix, sy, iw, sh, 10, new Color(0, 0, 0, 0.35f));
            switch (_mode)
            {
                case Mode.Duel:
                    if (Segment(ix, sy, iw, 2, 0, "Host", _duel == DuelPick.Host)) _duel = DuelPick.Host;
                    if (Segment(ix, sy, iw, 2, 1, "Join", _duel == DuelPick.Join)) { _duel = DuelPick.Join; _focusJoin = true; }
                    break;
                case Mode.Bot:
                    for (int i = 0; i < 3; i++)
                        if (Segment(ix, sy, iw, 3, i, LevelNames[i], GameBootstrap.NextBotLevel == Levels[i])) GameBootstrap.NextBotLevel = Levels[i];
                    break;
                case Mode.Br:
                    for (int i = 0; i < 3; i++)
                        if (Segment(ix, sy, iw, 3, i, BrNames[i], GameBootstrap.NextBrMode == BrModes[i])) GameBootstrap.NextBrMode = BrModes[i];
                    break;
                case Mode.Replays:
                    if (Segment(ix, sy, iw, 2, 0, "Saved replays", _watch == WatchPick.Saved)) _watch = WatchPick.Saved;
                    if (Segment(ix, sy, iw, 2, 1, "Watch live", _watch == WatchPick.Live)) { _watch = WatchPick.Live; _focusJoin = true; }
                    break;
                default:
                    if (Segment(ix, sy, iw, 2, 0, "Free roam", _practice == PracticePick.FreeRoam)) _practice = PracticePick.FreeRoam;
                    if (Segment(ix, sy, iw, 2, 1, "Breach test map", _practice == PracticePick.Breach)) _practice = PracticePick.Breach;
                    break;
            }

            // Option cards.
            float oy = sy + sh + 14, oh = 66, gap = 12, ow = (iw - 2 * gap) / 3;
            DrawOptions(ix, oy, ow, oh, gap);

            // The big button.
            float by = oy + oh + 18, bh = 76;
            bool bhot = Hot(ix, by, iw, bh);
            Fill(ix, by + 4, iw, bh, 14, new Color(0.55f, 0.28f, 0.05f, 0.9f));
            Fill(ix, by, iw, bh, 14, bhot ? AmberHi : AmberLo);
            GradV(ix + 6, by + 2, iw - 12, bh * 0.55f, new Color(1f, 0.86f, 0.5f, bhot ? 0.55f : 0.45f), new Color(1f, 0.8f, 0.36f, 0f));
            string label = PlayLabel;
            float lw = SpacedWidth(label, 32, true, 7f);
            Spaced(ix + iw / 2 - lw / 2, by, bh, label, 32, Ink, true, 7f);
            if (Click(ix, by, iw, bh)) Play();

            // Status line: a join error in red, else a one-line hint for the choice.
            float ly = by + bh + 10;
            if (_error.Length > 0 && (_mode == Mode.Duel || _mode == Mode.Replays || _mode == Mode.Br)) Text(ix, ly, iw, 24, _error, 15, HudDraw.Red, TextAnchor.MiddleLeft, true);
            else Text(ix, ly, iw, 24, Hint, 15, Faint);

            // Mode tiles.
            float ty = py + ph + 16, th = 74, tgap = 10, tw = (PanelW - 4 * tgap) / 5;
            ModeTile(LX, ty, tw, th, Mode.Br, "Royale", "32 · bots fill");
            ModeTile(LX + (tw + tgap), ty, tw, th, Mode.Duel, "1v1 Duel", "Host · Join");
            ModeTile(LX + 2 * (tw + tgap), ty, tw, th, Mode.Bot, "vs Bot", "3 levels");
            ModeTile(LX + 3 * (tw + tgap), ty, tw, th, Mode.Practice, "Practice", "Free · Test map");
            ModeTile(LX + 4 * (tw + tgap), ty, tw, th, Mode.Replays, "Replays", "Saved · Live");
        }

        private string ModeName => _mode switch
        {
            Mode.Duel => "1v1 Duel",
            Mode.Bot => "1v1 vs Bot",
            Mode.Br => "Battle royale",
            Mode.Replays => _watch == WatchPick.Saved ? "Replays" : "Watch live",
            _ => "Practice",
        };

        private string MapName => _mode == Mode.Br ? "Royale map" : _mode == Mode.Replays ? (_watch == WatchPick.Live ? "Host's map" : SelectedReplay?.Map == "Breach" ? "Breach test map" : SelectedReplay?.Map == "BattleRoyale" ? "Royale map" : "1v1 map")
            : _mode == Mode.Practice && _practice == PracticePick.Breach ? "Breach test map" : "1v1 map";

        private string PlayLabel => _finder != null ? "SEARCHING" : _mode == Mode.Br ? (_brNet == BrNet.Host ? "HOST" : _brNet == BrNet.Join ? "JOIN" : "PLAY") : _mode == Mode.Duel ? (_duel == DuelPick.Host ? "HOST" : "JOIN") : _mode == Mode.Replays ? "WATCH" : "PLAY";

        /// <summary>The replay picked in the list (newest first), or null when there are none.</summary>
        private ReplayLibrary.Entry? SelectedReplay
        {
            get
            {
                EnsureReplays();
                return _replays.Count == 0 ? (ReplayLibrary.Entry?)null : _replays[_replayAt];
            }
        }

        private void EnsureReplays()
        {
            if (_replays != null) return;
            try { _replays = ReplayLibrary.List(); }
            catch (System.Exception e) { _replays = new List<ReplayLibrary.Entry>(); Debug.LogWarning("[SCULPT] Replay list: " + e.Message); }
            _replayAt = Mathf.Clamp(_replayAt, 0, Mathf.Max(0, _replays.Count - 1));
        }

        private string Hint => _mode switch
        {
            Mode.Duel when _finder != null => FinderHint,
            Mode.Br when _finder != null => FinderHint,
            Mode.Replays when _finder != null => FinderHint,
            Mode.Duel => _duel == DuelPick.Host ? HostHint : "Same network as the host: type the room code on their screen (or their address).",
            Mode.Bot => "The same 1v1 match against a bot, offline: most kills in 5 minutes.",
            Mode.Br => _brNet == BrNet.Offline ? "32 players, bots take the empty places. Loot chests, stay in the circle, last team standing wins."
                : _brNet == BrNet.Host ? HostHint : "Same network as the host: type the room code on their screen (or their address).",
            Mode.Replays => _watch == WatchPick.Saved
                ? (SelectedReplay == null ? "No replays yet: every match you play or host is saved (newest 20, star one to keep it)." : "Your last 20 matches are kept; star one to keep it for good.")
                : "Type the room code (or address) of a match on your network: you watch a little behind.",
            _ => _practice == PracticePick.FreeRoam ? "Offline on the 1v1 map with nobody else: try walls, guns and movement."
                : "Practice dummy, test walls and the movement course (F6 races the TAS in dev builds).",
        };

        private string _hostHint, _finderHint, _finderHintCode;
        private string HostHint => LanRoomBeacon.Problem.Length > 0 ? LanRoomBeacon.Problem
            : _hostHint ??= _ips.Count > 0 ? $"Friends pick Join and type room code {LanRoomBeacon.Code} (or your address). Allow the firewall if asked." : _ipHint;
        private string FinderHint
        {
            get
            {
                if (_finderHintCode != _finder.Code) { _finderHintCode = _finder.Code; _finderHint = _finder.ByAddress ? "Asking the host what it runs...  (click to cancel)" : $"Looking for room {_finder.Code} on your network...  (click to cancel)"; }
                return _finderHint;
            }
        }

        /// <summary>One of n equal segments in the row; returns true on click.</summary>
        private bool Segment(float x, float y, float w, int n, int i, string label, bool on)
        {
            float sw = (w - 8) / n, sx = x + 4 + i * sw;
            bool hot = Hot(sx, y + 4, sw, 42);
            if (on) Fill(sx + 2, y + 4, sw - 4, 42, 8, HudDraw.Text);
            else if (hot) Fill(sx + 2, y + 4, sw - 4, 42, 8, CardHover);
            Text(sx, y + 4, sw, 42, label, 17, on ? Ink : Soft, TextAnchor.MiddleCenter, true);
            return Click(sx, y + 4, sw, 42);
        }

        private void Card(float x, float y, float w, float h, string label, string value)
        {
            Fill(x, y, w, h, 10, CardBg);
            Text(x + 16, y + 10, w - 24, 20, label, 14, Soft);
            Text(x + 16, y + 30, w - 24, 28, value, 19, HudDraw.Text, TextAnchor.MiddleLeft, true);
        }

        private static readonly BotLevel[] Levels = { BotLevel.Easy, BotLevel.Normal, BotLevel.Hard };
        private static readonly BrMode[] BrModes = { BrMode.Solos, BrMode.Duos, BrMode.Squads };
        private static readonly string[] BrNames = { "Solos", "Duos", "Squads" };
        private static readonly string[] BrTeams = { "32 solo", "16 duos", "8 squads of 4" };
        private static readonly string[] LevelNames = { "Easy", "Normal", "Hard" };

        private void DrawOptions(float x, float y, float w, float h, float gap)
        {
            float x2 = x + w + gap, x3 = x + 2 * (w + gap);
            switch (_mode)
            {
                case Mode.Duel when _duel == DuelPick.Host:
                    Card(x, y, w, h, "Rules", "Most kills · 5 min");
                    Card(x2, y, w, h, "Room code", LanRoomBeacon.Code);
                    Card(x3, y, w, h, "Your address", _ipText);
                    break;
                case Mode.Duel:
                {
                    JoinCard(x, y, 2 * w + gap, h);
                    Card(x3, y, w, h, "Port (UDP)", PortText);
                    break;
                }
                case Mode.Br:
                {
                    if (_brNet == BrNet.Join)
                    {
                        JoinCard(x, y, 2 * w + gap, h);
                    }
                    else
                    {
                        int bi = GameBootstrap.NextBrMode == BrMode.Squads ? 2 : GameBootstrap.NextBrMode == BrMode.Duos ? 1 : 0;
                        Card(x, y, w, h, "Teams", BrTeams[bi]);
                        // The locker gun: a common pump plus this one at the start of every match (Ben's BR design).
                        bool hl = Hot(x2, y, w, h);
                        Fill(x2, y, w, h, 10, hl ? CardHover : CardBg);
                        Text(x2 + 16, y + 10, w - 24, 20, "Locker gun (click)", 14, Soft);
                        Text(x2 + 16, y + 30, w - 24, 28, "Pump + " + LockerPick.Name(_locker), 19, HudDraw.Text, TextAnchor.MiddleLeft, true);
                        if (Click(x2, y, w, h)) { _locker = LockerPick.Next(_locker); LockerPick.Save(_locker); }
                    }
                    bool hn = Hot(x3, y, w, h);
                    Fill(x3, y, w, h, 10, hn ? CardHover : CardBg);
                    Text(x3 + 16, y + 10, w - 24, 20, "Network (click)", 14, Soft);
                    Text(x3 + 16, y + 30, w - 24, 28, _brNet == BrNet.Offline ? "Offline" : _brNet == BrNet.Host ? "Host on LAN" : "Join", 19, HudDraw.Text, TextAnchor.MiddleLeft, true);
                    if (Click(x3, y, w, h)) { _brNet = (BrNet)(((int)_brNet + 1) % 3); _error = ""; if (_brNet == BrNet.Join) _focusJoin = true; }
                    break;
                }
                case Mode.Bot:
                {
                    int i = GameBootstrap.NextBotLevel == BotLevel.Easy ? 0 : GameBootstrap.NextBotLevel == BotLevel.Hard ? 2 : 1;
                    Card(x, y, w, h, "Bot level", LevelNames[i]);
                    Card(x2, y, w, h, "Rules", "Most kills · 5 min");
                    Card(x3, y, w, h, "Network", "Offline");
                    break;
                }
                case Mode.Replays when _watch == WatchPick.Saved:
                {
                    float fw = 2 * w + gap;
                    ReplayLibrary.Entry? r = SelectedReplay;
                    int n = _replays.Count;
                    Fill(x, y, fw, h, 10, CardBg);
                    Text(x + 16, y + 10, fw - 120, 20, n == 0 ? "Replay" : $"Replay {_replayAt + 1} of {n}", 14, Soft);
                    Text(x + 16, y + 30, fw - 110, 28, r == null ? "None yet" : r.Value.Label, 17, HudDraw.Text, TextAnchor.MiddleLeft, true);
                    // Arrows: older and newer.
                    float ax = x + fw - 92;
                    bool hl = Hot(ax, y + 14, 40, 40), hr = Hot(ax + 46, y + 14, 40, 40);
                    Fill(ax, y + 14, 40, 40, 8, hl ? CardHover : new Color(1, 1, 1, 0.06f));
                    Fill(ax + 46, y + 14, 40, 40, 8, hr ? CardHover : new Color(1, 1, 1, 0.06f));
                    Text(ax, y + 14, 40, 40, "<", 20, HudDraw.Text, TextAnchor.MiddleCenter, true);
                    Text(ax + 46, y + 14, 40, 40, ">", 20, HudDraw.Text, TextAnchor.MiddleCenter, true);
                    if (n > 0 && Click(ax, y + 14, 40, 40)) _replayAt = (_replayAt + n - 1) % n;
                    if (n > 0 && Click(ax + 46, y + 14, 40, 40)) _replayAt = (_replayAt + 1) % n;
                    bool kept = r != null && r.Value.Kept;
                    bool hk = Hot(x3, y, w, h);
                    Fill(x3, y, w, h, 10, hk && r != null ? CardHover : CardBg);
                    Text(x3 + 16, y + 10, w - 24, 20, "Keep for good", 14, Soft);
                    Text(x3 + 16, y + 30, w - 24, 28, r == null ? "-" : kept ? "Starred" : "No (click)", 19, kept ? HudDraw.Amber : HudDraw.Text, TextAnchor.MiddleLeft, true);
                    if (r != null && Click(x3, y, w, h))
                    {
                        try { ReplayLibrary.SetKept(r.Value.Path, !kept); }
                        catch (System.Exception e) { _error = "Couldn't move the replay: " + e.Message; }
                        string path = System.IO.Path.GetFileName(r.Value.Path);
                        _replays = null;
                        EnsureReplays(); // re-read, then find it again
                        for (int i = 0; i < _replays.Count; i++) if (System.IO.Path.GetFileName(_replays[i].Path) == path) _replayAt = i;
                    }
                    break;
                }
                case Mode.Replays:
                {
                    JoinCard(x, y, 2 * w + gap, h);
                    Card(x3, y, w, h, "Delay", "Set by host");
                    break;
                }
                case Mode.Practice when _practice == PracticePick.FreeRoam:
                    Card(x, y, w, h, "Map", "1v1 map");
                    Card(x2, y, w, h, "Opponent", "None");
                    Card(x3, y, w, h, "Settings", "Esc in game");
                    break;
                default:
                    Card(x, y, w, h, "Dummy", "Heals walls");
                    Card(x2, y, w, h, "Course", "Movement tech");
                    Card(x3, y, w, h, "Walls", "Every state");
                    break;
            }
        }

        /// <summary>The join box: a room code or the host's address.</summary>
        private void JoinCard(float x, float y, float fw, float h)
        {
            bool focused = GUI.GetNameOfFocusedControl() == "joinField";
            Fill(x, y, fw, h, 10, focused ? new Color(1, 1, 1, 0.1f) : CardBg);
            if (focused) Outline(x, y, fw, h, 10, 1.5f, HudDraw.Amber);
            Text(x + 16, y + 10, fw - 24, 20, "Room code or host's address", 14, Soft);
            if (_address.Length == 0 && !focused) Text(x + 16, y + 30, fw - 24, 28, "Click and type the code on their screen, e.g. K7Q2", 19, Faint);
            if (_phase == Phase.Text) JoinField(x + 12, y + 28, fw - 24, 32);
        }

        private static string _portText;
        private static string PortText => _portText ??= FishNetTransport.DefaultPort.ToString();
        private GUIStyle _fieldStyle;

        private void JoinField(float x, float y, float w, float h)
        {
            if (_fieldStyle == null)
            {
                _fieldStyle = new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                _fieldStyle.normal.background = _fieldStyle.focused.background = _fieldStyle.hover.background = _fieldStyle.active.background = null;
                _fieldStyle.normal.textColor = _fieldStyle.focused.textColor = _fieldStyle.hover.textColor = HudDraw.Text;
                _fieldStyle.border = new RectOffset(0, 0, 0, 0);
            }
            _fieldStyle.fontSize = Mathf.RoundToInt(19 * _k);
            _fieldStyle.padding = new RectOffset(Mathf.RoundToInt(4 * _k), 0, 0, 0);
            GUI.SetNextControlName("joinField");
            _address = GUI.TextField(G(x, y, w, h), _address, 64, _fieldStyle);
            if (_focusJoin && Event.current.type == EventType.Repaint)
            {
                _focusJoin = false;
                GUI.FocusControl("joinField");
            }
        }

        private void ModeTile(float x, float y, float w, float h, Mode m, string title, string sub)
        {
            bool on = _mode == m, hot = Hot(x, y, w, h);
            Fill(x, y, w, h, 12, on ? new Color(0.07f, 0.09f, 0.11f, 0.88f) : hot ? new Color(0.13f, 0.15f, 0.18f, 0.82f) : new Color(0.07f, 0.09f, 0.11f, 0.66f));
            Outline(x, y, w, h, 12, on ? 2f : 1f, on ? HudDraw.Amber : Edge);
            Text(x + 18, y + 12, w - 24, 26, title, 19, HudDraw.Text, TextAnchor.MiddleLeft, true);
            Text(x + 18, y + 40, w - 24, 22, sub, 14, Faint);
            if (Click(x, y, w, h))
            {
                _mode = m;
                if (m == Mode.Duel && _duel == DuelPick.Join) _focusJoin = true;
            }
        }

        // ---------------------------------------------------------------- What's new and footer

        private void DrawNews()
        {
            float cw = 350, cx = _w - 48 - cw, ny = 818, nh = 172;
            Fill(cx, ny, cw, nh, 14, PanelBg);
            Outline(cx, ny, cw, nh, 14, 1, Edge);
            Spaced(cx + 22, ny + 18, 18, "PRE-ALPHA  ·  WHAT'S NEW", 13, Faint, true, 2.5f);
            Text(cx + 22, ny + 42, cw - 44, 30, "LAN 1v1 and a practice bot", 20, HudDraw.Text, TextAnchor.MiddleLeft, true);
            Text(cx + 22, ny + 76, cw - 44, 22, "Host on your network or fight the bot on", 15, Soft);
            Text(cx + 22, ny + 98, cw - 44, 22, "three levels. Walls, floors, stairs and doors", 15, Soft);
            Text(cx + 22, ny + 120, cw - 44, 22, "all break; heal them shut in sculpt mode.", 15, Soft);
        }

        private void DrawFooter()
        {
            float y = 1030, x = 48;
            x = KeyHint(x, y, "ENTER", "Play");
            x = KeyHint(x, y, "ESC", "Settings");
            if (GameInput.DevBuild) KeyHint(x, y, "F3", "Latency panel");
            Text(_w - 48 - 700, y, 700, 30, ServerInfo, 14, Faint, TextAnchor.MiddleRight);
        }

        private static string _serverInfo;
        /// <summary>
        /// The server info footer (Ben's roadmap item 22: always shown, small: region, ping, tick rate, version). There
        /// are no online servers yet, so the region says where you can play today and there is no ping to show here; a
        /// room code lookup shows its ping on the loading card.
        /// </summary>
        private static string ServerInfo => _serverInfo ??= "Offline and LAN  ·  " + SimTime.TicksPerSecond + " Hz tick  ·  Build "
            + Application.version + "  ·  protocol " + Protocol.Version + "  ·  pre-alpha";

        private float KeyHint(float x, float y, string key, string what)
        {
            float kw = SpacedWidth(key, 12, true, 1f) + 18;
            Outline(x, y + 2, kw, 26, 6, 1, new Color(1, 1, 1, 0.35f));
            Spaced(x + 9, y + 2, 26, key, 12, Soft, true, 1f);
            Text(x + kw + 9, y, 200, 30, what, 15, Soft);
            return x + kw + 9 + SpacedWidth(what, 15, false, 0f) + 26;
        }

        // ---------------------------------------------------------------- Screenshot (dev)

        // "-menushot path [mode]" (dev builds and the Editor): saves one PNG of the home screen after it has settled,
        // then quits. For checking the menu without a person at the PC. Mode: 0 duel, 1 bot, 2 practice.
        private string _shotPath;
        private int _shotFrame;

        private void ReadShotArgs()
        {
            if (!(GameInput.DevBuild || Application.isEditor)) return;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "-menushot")
                {
                    _shotPath = args[i + 1];
                    if (i + 2 < args.Length && int.TryParse(args[i + 2], out int m)) _mode = (Mode)Mathf.Clamp(m, 0, 2);
                }
        }

        private void TakeShot()
        {
            if (++_shotFrame == 240) StartCoroutine(Shot());
        }

        private System.Collections.IEnumerator Shot()
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes(_shotPath, tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[SCULPT] Menu shot saved: " + _shotPath);
            Application.Quit();
        }

        // ---------------------------------------------------------------- Network addresses

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
