using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The match on screen: time left and both scores under the compass, "waiting for an opponent" with your LAN address
    /// while hosting alone, a big 3-2-1 countdown, and the result at the end. Everything comes from the server's match
    /// message; nothing here is predicted.
    /// </summary>
    public sealed class MatchHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private NetSession _session;
        private Material _gl;
        private string _ipLine;
        private MatchPhase _lastPhase;
        private bool _facedBr;
        // Texts shown every frame are rebuilt only when their number changes (a string per frame is garbage per frame).
        private int _timeSecs = -1, _youKills = -1, _themKills = -1;
        private string _timeText = "", _youText = "", _themText = "";
        private long _endKey = long.MinValue;
        private int _nextSecs = -1;
        private string _endLine = "", _nextText = "";
        // The match length comes from the server (MatchState.PlayTicks); its rules line is rebuilt only when it changes.
        private int _rulesTicks = -1;
        private string _rulesText = "";

        public void Init(LocalPlayer player, NetSession session)
        {
            _player = player;
            _session = session;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            MatchState m = _session.Client.Match;
            // A new match puts you back at your spawn: face the way the spawn faces.
            if (m.Phase == MatchPhase.Countdown && _lastPhase != MatchPhase.Countdown && !m.IsBr) _player.FaceSpawnNow();
            // Battle royale: once the deal has moved you to your pad, look toward the middle of the map.
            if (m.IsBr && m.Phase == MatchPhase.Countdown && !_facedBr && _session.Client.ServerTimeEstimate - m.PhaseStartTick > 30 * SimTime.Per60)
            {
                _facedBr = true;
                _player.FaceTowards(new Vec3d(BrMap.Size / 2, 0, BrMap.Size / 2));
            }
            if (m.Phase != MatchPhase.Countdown) _facedBr = false;
            if (m.Phase != _lastPhase) Debug.Log($"[SCULPT] Match: {m.Phase}" + (m.Phase == MatchPhase.Ended ? $" (you {m.ScoreOf(_session.Client.PlayerId).Kills} kills, leader {m.Leader()})" : "")
                + $" (you: player {_session.Client.PlayerId}, team {_session.Client.Team}, {(_session.Client.Predicted.Dead ? "dead" : "alive")})");
            _lastPhase = m.Phase;
        }

        private int SecondsLeft(MatchState m)
        {
            double now = _session.Client.ServerTimeEstimate;
            return Mathf.Max(0, Mathf.CeilToInt((float)((m.PhaseEndTick - now) / SimTime.TicksPerSecond)));
        }

        private void OnGUI()
        {
            if (_session == null || _player == null) return;
            NetClient net = _session.Client;
            if (!net.Joined || net.IsSpectator) return; // spectators get the watch HUD's clock and scores
            MatchState m = net.Match;
            if (m.Phase == MatchPhase.Off || m.IsBr) return; // the battle royale has its own HUD (BrHud)
            if (Event.current.type != EventType.Repaint) return; // draw-only: nothing here reacts to input events
            float w = Screen.width, h = Screen.height;
            float s = Mathf.Max(0.75f, h / 1080f);

            MatchScore me = m.ScoreOf(net.PlayerId), them = default;
            bool hasThem = false;
            foreach (MatchScore x in m.Scores) if (x.PlayerId != net.PlayerId) { them = x; hasThem = true; break; }

            // Panel under the compass: time, your kills, their kills.
            float pw = 300 * s, ph = 46 * s, px = w / 2 - pw / 2, py = h - 70 * s - ph;
            {
                _gl.SetPass(0);
                GL.PushMatrix();
                try
                {
                    HudDraw.LoadPixels();
                    HudDraw.PanelRect(px, py, pw, ph);
                    if (m.Phase == MatchPhase.Ended || m.Phase == MatchPhase.Waiting)
                        HudDraw.PanelRect(w / 2 - 330 * s, h * 0.62f - 70 * s, 660 * s, 140 * s);
                }
                finally { GL.PopMatrix(); }
            }
            Rect panel = HudDraw.ToGui(px, py, pw, ph);
            int playSecs = Mathf.CeilToInt((float)m.PlayTicks / SimTime.TicksPerSecond);
            int secs = m.Phase == MatchPhase.Playing ? SecondsLeft(m) : m.Phase == MatchPhase.Countdown ? playSecs : 0;
            if (secs != _timeSecs) { _timeSecs = secs; _timeText = $"{secs / 60}:{secs % 60:00}"; }
            if (me.Kills != _youKills) { _youKills = me.Kills; _youText = $"YOU  {me.Kills}"; }
            if (hasThem && them.Kills != _themKills) { _themKills = them.Kills; _themText = $"{them.Kills}  THEM"; }
            string time = m.Phase == MatchPhase.Waiting ? "--:--" : _timeText;
            Color timeColor = m.Phase == MatchPhase.Playing && secs <= 30 ? HudDraw.Amber : HudDraw.Text;
            HudDraw.Label(new Rect(panel.x, panel.y, panel.width, panel.height), time, Mathf.RoundToInt(24 * s), timeColor, TextAnchor.MiddleCenter, true);
            HudDraw.Label(new Rect(panel.x + 14 * s, panel.y, 100 * s, panel.height), _youText, Mathf.RoundToInt(18 * s), TeamColor(net.Team), TextAnchor.MiddleLeft, true);
            HudDraw.Label(new Rect(panel.xMax - 114 * s, panel.y, 100 * s, panel.height), hasThem ? _themText : "-  THEM", Mathf.RoundToInt(18 * s), TeamColor(hasThem ? them.Team : 3 - net.Team), TextAnchor.MiddleRight, true);

            Rect mid = new Rect(w / 2 - 330 * s, h * 0.38f - 70 * s, 660 * s, 140 * s);
            switch (m.Phase)
            {
                case MatchPhase.Waiting:
                    HudDraw.Label(new Rect(mid.x, mid.y + 18 * s, mid.width, 44 * s), "Waiting for an opponent", Mathf.RoundToInt(30 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                    if (_session.Mode == NetMode.Host)
                    {
                        _ipLine ??= MainMenu.LanAddresses().Count > 0
                            ? (LanRoomBeacon.Problem.Length > 0 ? "They join with " : "They join with room code " + LanRoomBeacon.Code + "  or  ") + string.Join("  or  ", MainMenu.LanAddresses())
                            : "No network address found";
                        HudDraw.Label(new Rect(mid.x, mid.y + 72 * s, mid.width, 34 * s), _ipLine, Mathf.RoundToInt(20 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    }
                    break;
                case MatchPhase.Countdown:
                {
                    int c = Mathf.Max(1, SecondsLeft(m));
                    HudDraw.Label(new Rect(mid.x, mid.y, mid.width, mid.height), HudDraw.Num(c), Mathf.RoundToInt(96 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                    if (m.PlayTicks != _rulesTicks)
                    {
                        _rulesTicks = m.PlayTicks;
                        _rulesText = playSecs % 60 == 0
                            ? $"Most kills in {playSecs / 60} minute{(playSecs == 60 ? "" : "s")} wins"
                            : $"Most kills in {playSecs / 60}:{playSecs % 60:00} wins";
                    }
                    HudDraw.Label(new Rect(mid.x, mid.yMax, mid.width, 34 * s), _rulesText, Mathf.RoundToInt(20 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    break;
                }
                case MatchPhase.Playing:
                    if (_session.Client.ServerTimeEstimate - m.PhaseStartTick < 90 * SimTime.Per60) // the first moments: GO
                        HudDraw.Label(new Rect(mid.x, mid.y, mid.width, mid.height), "GO", Mathf.RoundToInt(80 * s), HudDraw.Amber, TextAnchor.MiddleCenter, true);
                    break;
                case MatchPhase.Ended:
                {
                    int leader = m.Leader();
                    string result = leader == 0 ? "DRAW" : leader == net.PlayerId ? "YOU WIN" : "YOU LOSE";
                    Color c = leader == 0 ? HudDraw.Text : leader == net.PlayerId ? HudDraw.Amber : HudDraw.Red;
                    HudDraw.Label(new Rect(mid.x, mid.y + 10 * s, mid.width, 64 * s), result, Mathf.RoundToInt(54 * s), c, TextAnchor.MiddleCenter, true);
                    long lineKey = hasThem ? ((long)me.Kills << 48) | ((long)me.Deaths << 32) | ((long)(them.Kills & 0xFFFF) << 16) | (long)(them.Deaths & 0xFFFF) : -1L - (((long)me.Kills << 16) | (long)(me.Deaths & 0xFFFF));
                    if (lineKey != _endKey) { _endKey = lineKey; _endLine = $"{me.Kills} kills, {me.Deaths} deaths" + (hasThem ? $"   vs   {them.Kills} kills, {them.Deaths} deaths" : ""); }
                    int next = SecondsLeft(m);
                    if (next != _nextSecs) { _nextSecs = next; _nextText = $"Next match in {next}"; }
                    HudDraw.Label(new Rect(mid.x, mid.y + 76 * s, mid.width, 28 * s), _endLine, Mathf.RoundToInt(19 * s), HudDraw.Text, TextAnchor.MiddleCenter);
                    HudDraw.Label(new Rect(mid.x, mid.y + 104 * s, mid.width, 26 * s), _nextText, Mathf.RoundToInt(16 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    break;
                }
            }
        }

        /// <summary>Same team colours as the player boxes (blue, orange; never purple).</summary>
        private static Color TeamColor(int team) => team == 1 ? new Color(0.45f, 0.7f, 1f) : new Color(1f, 0.62f, 0.3f);
    }
}
