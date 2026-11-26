using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using UnityEngine;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The battle royale on screen: players and teams left and your kills under the compass, the circle timer, a red
    /// warning while you are outside the circle, a minimap (top right) with both circles, you and your teammates, the
    /// lobby and countdown, "you're out" with your place, and the result. Everything comes from the server's match
    /// message; nothing here is predicted. Strings are rebuilt only when their number changes.
    /// </summary>
    public sealed class BrHud : MonoBehaviour
    {
        private string _roomLine;
        private LocalPlayer _player;
        private NetSession _session;
        private Material _gl;
        private readonly List<int> _ids = new List<int>();

        private int _aliveKey = -1, _stormKey = -1, _placeKey = -1, _countKey = -1, _outKey = -1;
        private string _aliveText = "", _stormText = "", _placeText = "", _countText = "", _outText = "";

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

        private void OnDestroy()
        {
            if (_gl != null) Destroy(_gl);
        }

        public static readonly Color Teammate = new Color(0.45f, 0.75f, 1f);
        private static readonly Color StormEdge = new Color(0.62f, 0.86f, 1f, 0.95f);
        private static readonly Color NextEdge = new Color(1f, 1f, 1f, 0.9f);

        private int Secs(double tick) => Mathf.Max(0, Mathf.CeilToInt((float)((tick - _session.Client.ServerTimeEstimate) / SimTime.TicksPerSecond)));

        private static string Clock(int secs) => $"{secs / 60}:{secs % 60:00}";

        private void OnGUI()
        {
            if (_session == null || _player == null) return;
            NetClient net = _session.Client;
            if (!net.Joined || net.IsSpectator) return;
            MatchState m = net.Match;
            if (!m.IsBr) return;
            if (Event.current.type != EventType.Repaint) return;
            float w = Screen.width, h = Screen.height;
            float s = Mathf.Max(0.75f, h / 1080f);
            double now = net.ServerTimeEstimate;
            MatchScore me = m.ScoreOf(net.PlayerId);
            StormState st = m.Storm;
            Vec3d pos = net.Predicted.Position;
            bool dead = net.Predicted.Dead;
            bool outside = m.Phase == MatchPhase.Playing && st.Active && !dead && st.OutsideBy(pos, now) > 0;

            // ---- Shapes: the top panel, the minimap, the middle card.
            float pw = 420 * s, ph = 46 * s, px = w / 2 - pw / 2, py = h - 70 * s - ph;
            float mm = 230 * s, mx = w - mm - 24 * s, my = h - mm - 24 * s;
            Rect mid = new Rect(w / 2 - 330 * s, h * 0.38f - 70 * s, 660 * s, 140 * s);
            bool card = m.Phase != MatchPhase.Playing || dead;
            _gl.SetPass(0);
            GL.PushMatrix();
            try
            {
                HudDraw.LoadPixels();
                HudDraw.BeginBatch();
                HudDraw.PanelRect(px, py, pw, ph);
                if (card) HudDraw.PanelRect(mid.x, h - mid.yMax, mid.width, mid.height);
                if (outside) HudDraw.PanelRect(w / 2 - 260 * s, py - 52 * s, 520 * s, 42 * s);
                DrawMinimap(net, m, mx, my, mm, now, s);
                HudDraw.EndBatch();
            }
            finally { GL.PopMatrix(); }

            // ---- Text.
            Rect panel = HudDraw.ToGui(px, py, pw, ph);
            int aliveKey = m.AlivePlayers * 10000 + m.AliveTeams * 100 + Mathf.Min(99, me.Kills);
            if (aliveKey != _aliveKey)
            {
                _aliveKey = aliveKey;
                _aliveText = m.TeamSize > 1 ? $"{m.AlivePlayers} ALIVE  ·  {m.AliveTeams} TEAMS  ·  {me.Kills} KILLS" : $"{m.AlivePlayers} ALIVE  ·  {me.Kills} KILLS";
            }
            if (m.Phase == MatchPhase.Playing)
            {
                HudDraw.Label(new Rect(panel.x, panel.y, panel.width, panel.height * 0.55f), _aliveText, Mathf.RoundToInt(19 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                int key, secs;
                string what;
                if (!st.Active) { key = -2; secs = 0; what = ""; }
                else if (now < st.ShrinkStart) { secs = Secs(st.ShrinkStart); key = st.Phase * 10000 + secs; what = "CIRCLE CLOSES IN "; }
                else if (now < st.ShrinkEnd) { secs = Secs(st.ShrinkEnd); key = 1000000 + st.Phase * 10000 + secs; what = "CIRCLE CLOSING "; }
                else { secs = 0; key = 2000000 + st.Phase; what = "FINAL CIRCLE"; }
                if (key != _stormKey)
                {
                    _stormKey = key;
                    _stormText = what.Length == 0 ? "" : what + (secs > 0 ? Clock(secs) : "") + $"   ·   {st.Phase + 1}/{st.PhaseCount}";
                }
                bool closing = st.Active && now >= st.ShrinkStart && now < st.ShrinkEnd;
                HudDraw.Label(new Rect(panel.x, panel.y + panel.height * 0.5f, panel.width, panel.height * 0.5f), _stormText, Mathf.RoundToInt(14 * s), closing ? HudDraw.Amber : HudDraw.Dim, TextAnchor.MiddleCenter, true);
            }
            else
            {
                string mode = m.Mode == BrMode.Squads ? "SQUADS" : m.Mode == BrMode.Duos ? "DUOS" : "SOLOS";
                HudDraw.Label(panel, "BATTLE ROYALE  ·  " + mode, Mathf.RoundToInt(19 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
            }

            if (outside)
            {
                int d = Mathf.CeilToInt((float)st.OutsideBy(pos, now));
                if (d != _outKey) { _outKey = d; _outText = $"OUTSIDE THE CIRCLE  ·  {d} m  ·  {st.Dps} a second"; }
                Rect r = HudDraw.ToGui(w / 2 - 260 * s, py - 52 * s, 520 * s, 42 * s);
                HudDraw.Label(r, _outText, Mathf.RoundToInt(18 * s), HudDraw.Red, TextAnchor.MiddleCenter, true);
            }

            if (!card) return;
            switch (m.Phase)
            {
                case MatchPhase.Waiting:
                case MatchPhase.Off:
                    HudDraw.Label(new Rect(mid.x, mid.y + 18 * s, mid.width, 44 * s), "Filling the lobby", Mathf.RoundToInt(30 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                    HudDraw.Label(new Rect(mid.x, mid.y + 72 * s, mid.width, 34 * s), "32 players every match: bots take the empty places", Mathf.RoundToInt(19 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    if (_session.Mode == NetMode.Host && LanRoomBeacon.Problem.Length == 0)
                    {
                        _roomLine ??= "Friends on your network join with room code " + LanRoomBeacon.Code;
                        HudDraw.Label(new Rect(mid.x, mid.y + 110 * s, mid.width, 34 * s), _roomLine, Mathf.RoundToInt(19 * s), HudDraw.Amber, TextAnchor.MiddleCenter, true);
                    }
                    break;
                case MatchPhase.Countdown:
                {
                    int c = Mathf.Max(1, Secs(m.PhaseEndTick));
                    if (c != _countKey) { _countKey = c; _countText = HudDraw.Num(c); }
                    HudDraw.Label(new Rect(mid.x, mid.y, mid.width, mid.height), _countText, Mathf.RoundToInt(96 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                    HudDraw.Label(new Rect(mid.x, mid.yMax, mid.width, 34 * s), "Last team standing wins. Loot the chests, stay in the circle.", Mathf.RoundToInt(20 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    break;
                }
                case MatchPhase.Playing:
                {
                    // Out: your team's place once it is decided, else "your team is still in".
                    int place = me.Place;
                    int key = place * 1000 + Mathf.Min(99, me.Kills);
                    if (key != _placeKey)
                    {
                        _placeKey = key;
                        _placeText = place > 0 ? $"#{place}  ·  {me.Kills} kills" : $"Your team is still in  ·  {me.Kills} kills";
                    }
                    HudDraw.Label(new Rect(mid.x, mid.y + 14 * s, mid.width, 60 * s), "YOU'RE OUT", Mathf.RoundToInt(48 * s), HudDraw.Red, TextAnchor.MiddleCenter, true);
                    HudDraw.Label(new Rect(mid.x, mid.y + 76 * s, mid.width, 30 * s), _placeText, Mathf.RoundToInt(21 * s), HudDraw.Text, TextAnchor.MiddleCenter, true);
                    HudDraw.Label(new Rect(mid.x, mid.y + 106 * s, mid.width, 26 * s), "Watching until the next match  ·  left/right mouse: other players", Mathf.RoundToInt(15 * s), HudDraw.Dim, TextAnchor.MiddleCenter);
                    break;
                }
                case MatchPhase.Ended:
                {
                    // From your own place, not your team number: someone who sat the match out has no place (and
                    // their team number may be a dealt team's), so they never see VICTORY or "#1".
                    bool won = m.WinnerTeam != 0 && me.Place == 1;
                    string title = won ? "VICTORY" : m.WinnerTeam == 0 ? "NOBODY SURVIVED" : me.Place > 0 ? $"#{me.Place}" : "MATCH OVER";
                    HudDraw.Label(new Rect(mid.x, mid.y + 10 * s, mid.width, 64 * s), title, Mathf.RoundToInt(54 * s), won ? HudDraw.Amber : HudDraw.Text, TextAnchor.MiddleCenter, true);
                    int key = 100000 + me.Kills * 100 + Secs(m.PhaseEndTick);
                    if (key != _placeKey) { _placeKey = key; _placeText = $"{me.Kills} kills   ·   next match in {Secs(m.PhaseEndTick)}"; }
                    HudDraw.Label(new Rect(mid.x, mid.y + 80 * s, mid.width, 30 * s), _placeText, Mathf.RoundToInt(20 * s), HudDraw.Text, TextAnchor.MiddleCenter);
                    break;
                }
            }
        }

        private void DrawMinimap(NetClient net, MatchState m, float x, float y, float size, double now, float s)
        {
            // The whole square map, north up. Map metres to pixels.
            HudDraw.RoundRect(x - 4 * s, y - 4 * s, size + 8 * s, size + 8 * s, 10 * s, new Color(0.05f, 0.06f, 0.08f, 0.62f));
            HudDraw.Quad(x, y, size, size, new Color(0.30f, 0.40f, 0.26f, 0.55f));
            float k = size / (float)BrMap.Size;
            Vector2 P(double wx, double wz) => new Vector2(x + (float)wx * k, y + (float)wz * k);
            // Buildings as dark blocks.
            foreach (Aabb b in BuildingsOnMap()) HudDraw.Quad(x + (float)b.Min.X * k, y + (float)b.Min.Z * k, (float)(b.Max.X - b.Min.X) * k, (float)(b.Max.Z - b.Min.Z) * k, new Color(0.12f, 0.12f, 0.13f, 0.8f));
            StormState st = m.Storm;
            if (m.Phase == MatchPhase.Playing && st.Active)
            {
                Vector2 c = P(st.CentreXAt(now), st.CentreZAt(now));
                float r = (float)st.RadiusAt(now) * k;
                if (r > 0.5f) HudDraw.Arc(c.x, c.y, r, 2.2f * s, 0, 360, StormEdge);
                Vector2 n = P(st.ToX, st.ToZ);
                float nr = (float)st.ToRadius * k;
                if (nr > 0.5f && now < st.ShrinkEnd) HudDraw.Arc(n.x, n.y, nr, 1.4f * s, 0, 360, NextEdge);
            }
            // Teammates.
            _ids.Clear();
            net.GetRemoteIds(_ids);
            foreach (int id in _ids)
            {
                if (!net.TryGetRemote(id, out RemoteView v) || v.Team != net.Team || v.Dead) continue;
                Vector2 p = P(v.Position.X, v.Position.Z);
                HudDraw.Quad(p.x - 3 * s, p.y - 3 * s, 6 * s, 6 * s, Teammate);
            }
            // You: an arrow the way you face.
            if (!net.Predicted.Dead)
            {
                Vector2 me = P(net.Predicted.Position.X, net.Predicted.Position.Z);
                float yaw = _player.Yaw * Mathf.Deg2Rad;
                Vector2 f = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw)), r = new Vector2(f.y, -f.x);
                HudDraw.Tri(me + f * 8 * s, me - f * 4 * s + r * 5 * s, me - f * 4 * s - r * 5 * s, HudDraw.Amber);
            }
        }

        private static List<Aabb> _buildings;

        /// <summary>The map's building footprints (built once from the same code the world uses).</summary>
        private static List<Aabb> BuildingsOnMap()
        {
            if (_buildings != null) return _buildings;
            _buildings = new List<Aabb>();
            BrMap.BuildBrushes(out BreachMapInfo info, out _);
            if (info.Block != null) _buildings.AddRange(info.Block.Buildings);
            return _buildings;
        }
    }

    /// <summary>
    /// Battle royale rarity colours (Claude's call, 2026-11-26): grey, green, blue, red, gold. Never purple: purple is
    /// reserved for sculptable. Settings > Accessibility > Colourblind mode swaps in a set made for that eye
    /// (<see cref="Sculpt.Core.Settings.ColourVision"/>).
    /// </summary>
    public static class RarityColours
    {
        /// <summary>The mode the colours are in now (anything that bakes them, like the ground plates, rebuilds when it changes).</summary>
        public static Sculpt.Core.Settings.ColourMode Mode => Sculpt.Game.Settings.ClientSettings.Current.Colours;

        public static Color Of(Rarity r)
        {
            Sculpt.Core.Settings.Rgb c = Sculpt.Core.Settings.ColourVision.Rarity(Mode, (int)r);
            return new Color(c.R, c.G, c.B);
        }
    }
}
