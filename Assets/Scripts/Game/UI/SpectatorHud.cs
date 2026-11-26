using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Core.Replay;
using Sculpt.Game.Cameras;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// HUD while watching (live or a replay): what you're watching and how far behind, the match clock and scores, the
    /// watched player's health, item and ammo, the camera view, and for replays a timeline with every kill marked and
    /// the replay keys (pause, speed, 5 s back and forward, previous and next kill). With the cursor free (Esc), click
    /// or drag the timeline to jump. H hides it all.
    /// </summary>
    public sealed class SpectatorHud : MonoBehaviour
    {
        private LocalPlayer _player;
        private NetSession _session;
        private SpectatorCamera _cam;
        private GameInput _input;
        private Material _gl;
        private bool _hidden;
        private bool _dragging;

        // Text rebuilt only when what it says changes.
        private readonly System.Collections.Generic.List<MatchScore> _top = new System.Collections.Generic.List<MatchScore>();
        private string _topText = "", _timeText = "", _whoText = "", _hpText = "", _itemText = "", _scoreText = "", _clockText = "", _hint;
        private int _topKey = -1, _timeKey = -1, _whoKey = -1, _hpKey = -1, _itemKey = -1, _scoreKey = -1, _clockKey = -1;

        public void Init(LocalPlayer player, NetSession session, SpectatorCamera cam, GameInput input)
        {
            _player = player;
            _session = session;
            _cam = cam;
            _input = input;
            _gl = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _gl.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _gl.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _gl.SetInt("_Cull", 0);
            _gl.SetInt("_ZWrite", 0);
            _gl.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void Update()
        {
            if (_session == null || !_session.Client.IsSpectator) return;
            bool active = _player.CursorLocked && !_player.InputBlocked;
            if (!active) return;
            if (_input.WatchHud.WasPressedThisFrame()) _hidden = !_hidden;
            ReplayPlayer r = _session.Replay;
            if (r == null) return;
            if (_input.WatchPause.WasPressedThisFrame()) r.Paused = !r.Paused;
            if (_input.WatchFaster.WasPressedThisFrame()) r.Speed = Mathf.Min((float)ReplayPlayer.MaxSpeed, (float)r.Speed * 2f);
            if (_input.WatchSlower.WasPressedThisFrame()) r.Speed = Mathf.Max((float)ReplayPlayer.MinSpeed, (float)r.Speed * 0.5f);
            if (_input.WatchBack.WasPressedThisFrame()) r.SeekSeconds(-5);
            if (_input.WatchForward.WasPressedThisFrame()) r.SeekSeconds(5);
            if (_input.WatchNextKill.WasPressedThisFrame()) r.JumpToKill(r.NextKill());
            if (_input.WatchPrevKill.WasPressedThisFrame()) r.JumpToKill(r.PreviousKill());
        }

        // Layout (pixels, y up for shapes): the timeline along the bottom.
        private const float Margin = 24f, BarH = 8f;

        private void OnGUI()
        {
            if (_session == null) return;
            NetClient net = _session.Client;
            if (!net.IsSpectator || _hidden) return;
            ReplayPlayer replay = _session.Replay;
            float w = Screen.width, h = Screen.height;
            float bx = Margin * 2, bw = w - Margin * 4, by = Margin + 34;

            // Timeline clicks and drags (cursor free only: with it locked the mouse looks around).
            Event e = Event.current;
            if (replay != null && !_player.CursorLocked && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag || e.type == EventType.MouseUp))
            {
                Vector2 mp = e.mousePosition;
                float my = h - mp.y;
                bool over = mp.x >= bx - 6 && mp.x <= bx + bw + 6 && my >= by - 10 && my <= by + BarH + 10;
                if (e.type == EventType.MouseDown && over) _dragging = true;
                if (_dragging)
                {
                    float k = Mathf.Clamp01((mp.x - bx) / bw);
                    replay.Seek(replay.StartTick + k * (replay.EndTick - replay.StartTick));
                    e.Use();
                }
                if (e.type == EventType.MouseUp) _dragging = false;
            }
            if (e.type != EventType.Repaint) return;

            bool hasTarget = _cam.TryGetTarget(out RemoteView v, out SpecPlayer info);
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.BeginBatch();
                HudDraw.PanelRect(Margin, h - Margin - 40, 640, 40);
                HudDraw.PanelRect(w / 2 - 170, h - Margin - 40, 340, 40);
                if (hasTarget) HudDraw.PanelRect(w / 2 - 230, by + BarH + 22, 460, 66);
                if (replay != null)
                {
                    HudDraw.RoundRect(bx, by, bw, BarH, 3, new Color(1, 1, 1, 0.18f));
                    float k = (float)((replay.Time - replay.StartTick) / System.Math.Max(1, replay.EndTick - replay.StartTick));
                    HudDraw.RoundRect(bx, by, bw * k, BarH, 3, HudDraw.Amber);
                    foreach (ReplayMarker mk in replay.File.Markers)
                    {
                        float mx = bx + bw * (float)((mk.Tick - replay.StartTick) / System.Math.Max(1, replay.EndTick - replay.StartTick));
                        HudDraw.Quad(mx - 1.5f, by - 5, 3, BarH + 10, HudDraw.Red);
                    }
                    HudDraw.RoundRect(bx + bw * k - 6, by - 4, 12, BarH + 8, 6, HudDraw.Text);
                }
                if (hasTarget && !info.Dead)
                {
                    float hk = Mathf.Clamp01(info.Health / (float)_player.Config.Weapons.MaxHealth);
                    HudDraw.Bar(w / 2 - 210, by + BarH + 32, 180, 8, hk, hk < 0.34f ? HudDraw.Red : HudDraw.Text);
                }
            }
            finally
            {
                HudDraw.EndBatch();
                GL.PopMatrix();
            }

            // Top left: what you're watching.
            int top = replay != null ? (replay.Paused ? 1 : 2) * 1000 + Mathf.RoundToInt((float)replay.Speed * 8) : (net.SpectatorSynced ? 4000 : 3000) + net.SpectatorDelayTicks / SimTime.TicksPerSecond;
            if (top != _topKey)
            {
                _topKey = top;
                _topText = replay != null ? $"REPLAY  ·  {Speed(replay.Speed)}{(replay.Paused ? "  ·  PAUSED" : "")}"
                    : !net.SpectatorSynced ? $"WATCHING  ·  the host shows the match {Delay(net)} behind: it starts once that much has been played"
                    : $"WATCHING LIVE  ·  {Delay(net)} behind";
            }
            HudDraw.Label(HudDraw.ToGui(Margin + 14, h - Margin - 40, 620, 40), _topText, 15, HudDraw.Text, TextAnchor.MiddleLeft, true);

            // Top centre: match clock and scores.
            MatchState m = net.Match;
            int secs = m.IsBr ? m.AlivePlayers : m.Phase == MatchPhase.Playing ? Mathf.Max(0, Mathf.CeilToInt((float)((m.PhaseEndTick - net.ServerTimeEstimate) / SimTime.TicksPerSecond))) : -1;
            int clockKey = (int)m.Phase * 100000 + secs;
            if (clockKey != _clockKey)
            {
                _clockKey = clockKey;
                _clockText = m.IsBr && m.Phase == MatchPhase.Playing ? $"{secs} alive" // the battle royale has no match clock
                    : m.Phase == MatchPhase.Playing ? $"{secs / 60}:{secs % 60:00}" : m.Phase == MatchPhase.Off ? "Free play" : m.Phase.ToString();
            }
            int scoreKey = m.Version;
            if (scoreKey != _scoreKey)
            {
                _scoreKey = scoreKey;
                var sb = new System.Text.StringBuilder();
                if (m.IsBr)
                {
                    // 32 players don't fit: the three with the most kills.
                    _top.Clear();
                    foreach (MatchScore s in m.Scores) if (s.Kills > 0) _top.Add(s);
                    _top.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : a.PlayerId.CompareTo(b.PlayerId));
                    for (int i = 0; i < _top.Count && i < 3; i++) { if (sb.Length > 0) sb.Append("   "); sb.Append('P').Append(_top[i].PlayerId).Append(' ').Append(_top[i].Kills); }
                }
                else foreach (MatchScore s in m.Scores) { if (sb.Length > 0) sb.Append("   "); sb.Append('P').Append(s.PlayerId).Append(' ').Append(s.Kills); }
                _scoreText = sb.ToString();
            }
            HudDraw.Label(HudDraw.ToGui(w / 2 - 160, h - Margin - 40, 110, 40), _clockText, 18, HudDraw.Text, TextAnchor.MiddleLeft, true);
            HudDraw.Label(HudDraw.ToGui(w / 2 - 50, h - Margin - 40, 210, 40), _scoreText, 15, HudDraw.Dim, TextAnchor.MiddleRight, true);

            // The watched player.
            if (hasTarget)
            {
                int whoKey = info.Id * 64 + info.Team * 8 + (int)_cam.Mode * 2 + (info.Bot ? 1 : 0);
                if (whoKey != _whoKey)
                {
                    _whoKey = whoKey;
                    _whoText = $"P{info.Id}{(info.Bot ? " (bot)" : "")}  ·  {(info.Team == 1 ? "Blue" : info.Team == 2 ? "Orange" : "Team " + info.Team)}  ·  {SpectatorCamera.ViewName(_cam.Mode)}";
                }
                int hpKey = info.Dead ? -2 : info.Health;
                if (hpKey != _hpKey) { _hpKey = hpKey; _hpText = info.Dead ? "DEAD" : info.Health + " HP"; }
                int itemKey = (int)info.Held * 4096 + info.Ammo * 8 + (info.Reloading ? 1 : 0) + (info.Hand == HandMode.Sculpt ? 2 : 0);
                if (itemKey != _itemKey)
                {
                    _itemKey = itemKey;
                    string name = info.Held == Item.None ? "Hands" : Items.Name(info.Held);
                    _itemText = info.Hand == HandMode.Sculpt ? "Healing walls" : Items.IsGun(info.Held) ? $"{name}  {info.Ammo}{(info.Reloading ? "  reloading" : "")}" : name;
                }
                float py = by + BarH + 22;
                HudDraw.Label(HudDraw.ToGui(w / 2 - 216, py + 36, 440, 26), _whoText, 15, HudDraw.Text, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(w / 2 - 24, py + 6, 90, 26), _hpText, 15, info.Dead ? HudDraw.Red : HudDraw.Text, TextAnchor.MiddleLeft, true);
                HudDraw.Label(HudDraw.ToGui(w / 2 + 60, py + 6, 160, 26), _itemText, 15, HudDraw.Dim, TextAnchor.MiddleRight);
            }

            // Bottom: time and keys.
            if (replay != null)
            {
                int now = (int)((replay.Time - replay.StartTick) / SimTime.TicksPerSecond), total = (int)((replay.EndTick - replay.StartTick) / SimTime.TicksPerSecond);
                int timeKey = now * 100000 + total;
                if (timeKey != _timeKey) { _timeKey = timeKey; _timeText = $"{now / 60}:{now % 60:00} / {total / 60}:{total % 60:00}"; }
                HudDraw.Label(HudDraw.ToGui(bx, by + BarH + 2, 200, 22), _timeText, 13, HudDraw.Text, TextAnchor.MiddleLeft, true);
            }
            _hint ??= replay != null
                ? "Mouse: next/previous player   V: camera   P: pause   - / =: speed   Arrows: 5 s   , / .: kills   H: hide   Esc: menu"
                : "Mouse: next/previous player   V: camera   H: hide   Esc: menu";
            HudDraw.Label(HudDraw.ToGui(bx, Margin - 8, bw, 22), _hint, 12, HudDraw.Dim, TextAnchor.MiddleCenter);
        }

        private static string Speed(double s) => s >= 1 ? $"{s:0.#}x" : s >= 0.25 ? $"{s:0.##}x" : $"{s:0.###}x";

        private static string Delay(NetClient net)
        {
            int s = net.SpectatorDelayTicks / SimTime.TicksPerSecond;
            return s >= 60 ? $"{s / 60} min{(s % 60 != 0 ? $" {s % 60} s" : "")}" : $"{s} s";
        }
    }
}
