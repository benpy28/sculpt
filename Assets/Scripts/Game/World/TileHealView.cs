using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Cameras;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Sculpting;
using UnityEngine;

namespace Sculpt.Game.World
{
    /// <summary>
    /// Healing on the map (Ben 2026-11-25: no grid, no outlines, heal by area). Purple means "healable" only:
    /// - aiming at a hole or bad cracks within range shows a faint purple ghost of exactly what Q would fill (the
    ///   simulation's plan: the chunk round the aim, or the whole hole if it is small), shaped like the wall's own holes
    ///   (diagonal edges, never squares); a quieter one while the bar is short or the spot is out of sight;
    /// - a heal in progress fills its ghost over the heal time with a crackling edge in the healer's team colour and a hum
    ///   everyone near hears, so attackers can see the hole is about to close;
    /// - a finished heal thunks; a refused or cancelled one of yours flashes its ghost red.
    /// Stairs and ramps (healed whole) show their segment. Never drawn over players.
    /// </summary>
    public sealed class TileHealView : MonoBehaviour
    {
        private static readonly Color Ghost = new Color(0.70f, 0.50f, 1.00f, 0.30f);
        private static readonly Color ReinforceGhost = new Color(0.80f, 0.86f, 1.00f, 0.34f);
        private static readonly Color Red = new Color(1.00f, 0.22f, 0.18f, 0.55f);

        private LocalPlayer _player;
        private NetSession _session;
        private CameraRig _rig;
        private Material _lineMat, _ghostMat;
        private LineBatch _lines;
        private MaterialPropertyBlock _mpb;
        private readonly Dictionary<int, RoutedLoop> _hums = new Dictionary<int, RoutedLoop>();
        private readonly HashSet<int> _alive = new HashSet<int>();
        private readonly List<int> _dead = new List<int>();
        private NetClient _bound;
        private readonly Dictionary<int, ulong> _plan = new Dictionary<int, ulong>();

        /// <summary>One ghost shape, cached until the walls or the aim change.</summary>
        private sealed class Blob
        {
            public Mesh Mesh;
            public readonly List<Vector3> EdgeA = new List<Vector3>(), EdgeB = new List<Vector3>();
            public Vector3 Centre;
            public int Tile = -1, Cell = -1, Rev = -1;
            public HealKind Kind;
            public bool Any;
        }

        private readonly Blob _target = new Blob();
        private readonly Dictionary<int, Blob> _jobs = new Dictionary<int, Blob>();
        private readonly Blob _red = new Blob();
        private float _redUntil = -1f;

        public void Init(LocalPlayer player, NetSession session, CameraRig rig)
        {
            _player = player;
            _session = session;
            _rig = rig;
            Shader overlay = Shader.Find("SCULPT/Overlay");
            if (overlay == null) { Debug.LogError("[SCULPT] Shader SCULPT/Overlay not found."); overlay = Shader.Find("Universal Render Pipeline/Unlit"); }
            _lineMat = new Material(overlay) { name = "Heal lines" };
            _lineMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            _lineMat.renderQueue = 3000;
            _ghostMat = new Material(overlay) { name = "Heal ghost" };
            _ghostMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            _ghostMat.renderQueue = 2990;
            _lines = new LineBatch("Heal Lines", _lineMat);
            _lines.Object.transform.SetParent(transform, false);
            _mpb = new MaterialPropertyBlock();
        }

        private void LateUpdate()
        {
            if (_player == null || _rig == null || _rig.Camera == null || _lines == null) return;
            NetClient net = _session.Client;
            if (net != _bound) { ClearHums(); _jobs.Clear(); _target.Rev = -1; _redUntil = -1f; _bound = net; }
            _lines.Begin(_rig.Camera.transform.position);
            _alive.Clear();
            if (net.Playing && net.Heals != null && _player.Heals)
            {
                DrainEvents(net);
                DrawTarget(net);
                DrawHeals(net);
                DrawRed(net);
            }
            _lines.End();
            ReapHums();
        }

        // ---------------------------------------------------------------- what Q would do

        private void DrawTarget(NetClient net)
        {
            if (!_player.QuickHeal && !_player.InSculptMode) return;
            TileTarget t = _player.HealTarget;
            if (!t.Valid) return;
            HealResult r = _player.HealCheck;
            if (r == HealResult.NothingToHeal || r == HealResult.AlreadyReinforced || r == HealResult.NoTile || r == HealResult.OutOfRange) return;
            if (net.TryGetOwnHeal(out HealJob mine, out _) && mine.Tile == t.Tile) return; // the running heal is drawn instead
            HealKind kind = _player.HealKindNow;
            Build(net.Walls, _target, t.Tile, t.Cell, kind);
            if (!_target.Any) return;
            bool ok = r == HealResult.Ok;
            Color c = kind == HealKind.Reinforce ? ReinforceGhost : Ghost;
            if (!ok) c.a *= 0.4f; // can't right now (bar, sight): a quiet hint only, never red until you press
            DrawBlob(_target, c, ok ? 0.018f : 0.01f, new Color(SculptView.HoverEdge.r, SculptView.HoverEdge.g, SculptView.HoverEdge.b, ok ? 0.7f : 0.3f), 0f);
        }

        // ---------------------------------------------------------------- heals in progress

        private void DrawHeals(NetClient net)
        {
            double serverNow = net.RemoteRenderTick + net.Config.InterpDelayTicks;
            if (net.TryGetOwnHeal(out HealJob mine, out _))
            {
                float p = Progress(mine, net.Tick + net.Alpha);
                DrawJob(net, mine, TeamColour(net.Team), p, 0.35f);
            }
            foreach (HealJob j in net.Heals.Jobs)
            {
                if (j.Player == net.PlayerId) continue;
                int team = net.TryGetRemote(j.Player, out RemoteView v) ? v.Team : 0;
                float p = Progress(j, serverNow);
                DrawJob(net, j, TeamColour(team), p, team != net.Team ? 1f : 0.6f);
            }
        }

        private static float Progress(in HealJob j, double now) =>
            Mathf.Clamp01((float)((now - j.StartTick) / Mathf.Max(1, j.EndTick - j.StartTick)));

        private void DrawJob(NetClient net, in HealJob j, Color team, float progress, float volume)
        {
            if (!_jobs.TryGetValue(j.Player, out Blob b)) _jobs[j.Player] = b = new Blob();
            Build(net.Walls, b, j.Tile, j.Cell, j.Kind);
            _alive.Add(j.Player);
            Color g = j.Kind == HealKind.Reinforce ? ReinforceGhost : Ghost;
            if (b.Any) DrawBlob(b, new Color(g.r, g.g, g.b, 0.12f + 0.5f * progress), 0.025f + 0.015f * progress, new Color(team.r, team.g, team.b, 0.95f), 0.01f + 0.03f * progress);
            Hum(j.Player, b.Any ? b.Centre : Centre(net.Walls.BoundsOf(j.Tile)), progress, volume, j.Kind == HealKind.Reinforce);
        }

        private void Hum(int player, Vector3 at, float progress, float volume, bool reinforce)
        {
            if (!_hums.TryGetValue(player, out RoutedLoop s) || s == null)
            {
                s = RoutedLoop.Create("Heal Hum", transform, SculptAudio.HumRaise, 3f, 40f);
                s.Source.time = Random.Range(0f, s.Source.clip.length * 0.9f);
                _hums[player] = s;
            }
            // Routed: heard through the doorway or hole it comes from, or muffled through the wall.
            s.At = at;
            s.Volume = volume * (0.45f + 0.55f * progress);
            s.Pitch = (reinforce ? 0.8f : 1f) * (0.9f + 0.25f * progress);
        }

        private void ReapHums()
        {
            _dead.Clear();
            foreach (var kv in _hums) if (!_alive.Contains(kv.Key)) _dead.Add(kv.Key);
            foreach (int k in _dead)
            {
                if (_hums[k] != null) Destroy(_hums[k].gameObject);
                _hums.Remove(k);
                _jobs.Remove(k);
            }
        }

        private void ClearHums()
        {
            foreach (var kv in _hums) if (kv.Value != null) Destroy(kv.Value.gameObject);
            _hums.Clear();
        }

        // ---------------------------------------------------------------- events

        private void DrainEvents(NetClient net)
        {
            foreach (HealEvent e in net.HealEvents)
            {
                if (e.Tile < 0 || e.Tile >= net.Walls.Count) continue;
                Vector3 at = Centre(net.Walls.BoundsOf(e.Tile));
                bool mine = e.Player == net.PlayerId;
                switch (e.Type)
                {
                    case HealEventType.Completed:
                        SculptAudio.Play(e.Kind == HealKind.Reinforce ? SculptAudio.ThunkHeavy : SculptAudio.Thunk, at, mine ? 0.9f : 0.7f);
                        break;
                    case HealEventType.Cancelled:
                        if (mine && e.Result != HealResult.Replaced && e.Result != HealResult.Stopped) Refused(net, e);
                        break;
                    case HealEventType.Rejected:
                        if (mine) Refused(net, e);
                        break;
                    case HealEventType.Began:
                        if (mine) SculptAudio.Play(e.Kind == HealKind.Reinforce ? SculptAudio.ConfirmHeavy : SculptAudio.Confirm, Vector3.zero, 0.5f, 1f, false);
                        break;
                }
            }
            net.HealEvents.Clear();
        }

        private void Refused(NetClient net, in HealEvent e)
        {
            _player.HealRejectTime = Time.time;
            _player.LastHealReject = e.Result;
            _player.LastHealRejectTile = e.Tile;
            SculptAudio.Play(SculptAudio.Reject, Vector3.zero, 0.6f, 1f, false);
            Build(net.Walls, _red, e.Tile, e.Cell, e.Kind, force: true);
            _redUntil = Time.time + 0.45f;
        }

        private void DrawRed(NetClient net)
        {
            // A press the client refused itself (the HUD names the reason) flashes what it aimed at.
            if (_player.LastHealRejectTile >= 0 && Time.time - _player.HealRejectTime < 0.02f && _redUntil < Time.time)
            {
                TileTarget t = _player.HealTarget;
                if (t.Valid) { Build(net.Walls, _red, t.Tile, t.Cell, _player.HealKindNow, force: true); _redUntil = _player.HealRejectTime + 0.45f; }
            }
            float left = _redUntil - Time.time;
            if (left <= 0 || !_red.Any) return;
            float k = Mathf.Clamp01(left / 0.45f);
            DrawBlob(_red, new Color(Red.r, Red.g, Red.b, Red.a * k), 0.03f, new Color(Red.r, Red.g, Red.b, 0.95f * k), 0f);
        }

        // ---------------------------------------------------------------- the ghost shape

        private static Color TeamColour(int team) => team <= 0 ? SculptView.HoverEdge : SculptView.Team[Mathf.Abs(team - 1) % SculptView.Team.Length];

        private static Vector3 Centre(Aabb b) => ((b.Min + b.Max) * 0.5).ToUnity();

        private void DrawBlob(Blob b, Color fill, float width, Color edge, float jitter)
        {
            _mpb.SetColor("_Color", fill);
            Graphics.DrawMesh(b.Mesh, Matrix4x4.identity, _ghostMat, 0, null, 0, _mpb, false, false);
            float t = Time.time * 30f;
            for (int i = 0; i < b.EdgeA.Count; i++)
            {
                Vector3 a = b.EdgeA[i], c = b.EdgeB[i];
                if (jitter > 0)
                {
                    float n = Mathf.PerlinNoise(t + i * 1.7f, i * 3.1f) - 0.5f;
                    Vector3 off = new Vector3(n, -n, n) * jitter;
                    a += off;
                    c -= off;
                }
                _lines.Add(a, c, edge, width);
            }
        }

        private static Vector3 Axis(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;

        private bool InPlan(int tile, int cell) => tile >= 0 && _plan.TryGetValue(tile, out ulong m) && ((m >> cell) & 1) != 0;

        /// <summary>
        /// Builds (or keeps) the ghost of what a heal aimed at (tile, cell) covers: the plan's cells with corners cut on the
        /// diagonal like the wall's own hole edges, just proud of both faces, and its outline on both faces.
        /// </summary>
        private void Build(TileWalls w, Blob b, int tile, int cell, HealKind kind, bool force = false)
        {
            if (!force && b.Tile == tile && b.Cell == cell && b.Kind == kind && b.Rev == w.Revision) return;
            b.Tile = tile; b.Cell = cell; b.Kind = kind; b.Rev = w.Revision;
            b.EdgeA.Clear(); b.EdgeB.Clear();
            if (b.Mesh == null) { b.Mesh = new Mesh { name = "Heal ghost" }; b.Mesh.MarkDynamic(); }
            b.Mesh.Clear();
            b.Any = false;
            if (tile < 0 || tile >= w.Count) return;
            var v = new List<Vector3>();
            var tris = new List<int>();
            if (!w.HasCells(tile))
            {
                // A stair or ramp segment heals whole: its box.
                Aabb bb = w.BoundsOf(tile);
                AddBoxGhost(v, tris, bb.Min.ToUnity() - Vector3.one * 0.01f, bb.Max.ToUnity() + Vector3.one * 0.01f);
                b.Centre = Centre(bb);
                b.Any = true;
                Finish(b, v, tris);
                return;
            }
            w.HealPlan(tile, cell, kind, _plan);
            if (_plan.Count == 0) return;
            b.Any = true;
            b.Centre = w.CellBox(tile, Mathf.Clamp(cell, 0, 63)).Center.ToUnity();
            const int Qn = TileWalls.Quads, Nc = Brush.CellsPerSide;
            foreach (var kv in _plan)
            {
                int u = kv.Key;
                Brush br = w.ShapeOf(u);
                Vector3 a1 = Axis(br.CellA1), a2 = Axis(br.CellA2), n = Axis(3 - br.CellA1 - br.CellA2);
                Vector3 lo = br.Bounds.Min.ToUnity(), hi = br.Bounds.Max.ToUnity();
                float s1 = Vector3.Dot(hi - lo, a1) / Qn, s2 = Vector3.Dot(hi - lo, a2) / Qn, th = Vector3.Dot(hi - lo, n);
                Vector3 P(float x, float y, int side) => lo + a1 * (x * s1) + a2 * (y * s2) + n * (side > 0 ? th + 0.012f : -0.012f);
                for (int qj = 0; qj < Qn; qj++)
                    for (int qi = 0; qi < Qn; qi++)
                    {
                        int c = (qi >> 1) + Nc * (qj >> 1);
                        bool s = ((kv.Value >> c) & 1) != 0;
                        int di = (qi & 1) == 1 ? 0 : 1, dj = (qj & 1) == 1 ? 2 : 3;
                        int ta = w.CellNeighbour(u, c, di, out int ca), tb = w.CellNeighbour(u, c, dj, out int cb);
                        bool sa = ta < 0 ? s : InPlan(ta, ca), sb = tb < 0 ? s : InPlan(tb, cb);
                        bool sd;
                        if (ta >= 0) { int td = w.CellNeighbour(ta, ca, dj, out int cd); sd = td < 0 ? sa : InPlan(td, cd); }
                        else if (tb >= 0) { int td = w.CellNeighbour(tb, cb, di, out int cd); sd = td < 0 ? sb : InPlan(td, cd); }
                        else sd = s;
                        QuadFill f = s ? (!sa && !sb && !sd ? QuadFill.Inner : QuadFill.Full) : (sa && sb ? QuadFill.Outer : QuadFill.Empty);
                        if (f == QuadFill.Empty) continue;
                        float X(float uu) => (qi & 1) == 1 ? qi + uu : qi + 1 - uu;
                        float Y(float vv) => (qj & 1) == 1 ? qj + vv : qj + 1 - vv;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            if (f == QuadFill.Full) Quad(v, tris, P(qi, qj, side), P(qi + 1, qj, side), P(qi + 1, qj + 1, side), P(qi, qj + 1, side));
                            else if (f == QuadFill.Inner) Tri(v, tris, P(X(0), Y(0), side), P(X(1), Y(0), side), P(X(0), Y(1), side));
                            else Tri(v, tris, P(X(1), Y(0), side), P(X(1), Y(1), side), P(X(0), Y(1), side));
                        }
                        // Outline: the cut diagonal, and straight sides where the next quarter cell is outside the ghost.
                        if (f != QuadFill.Full)
                            for (int side = -1; side <= 1; side += 2) { b.EdgeA.Add(P(X(1), Y(0), side)); b.EdgeB.Add(P(X(0), Y(1), side)); }
                        bool inner = f == QuadFill.Inner, outer = f == QuadFill.Outer;
                        // Sides toward the cell centre (u = 0 / v = 0) are covered by Full and Inner; the corner sides by Full and Outer.
                        bool uCentre = !outer, uCorner = !inner;
                        // u = 0 side is at x = X(0), running y from Y(0) to Y(1); u = 1 side at x = X(1).
                        OutlineSide(w, b, u, qi, qj, true, false, uCentre, X(0), Y(0), Y(1), P);
                        OutlineSide(w, b, u, qi, qj, true, true, uCorner, X(1), Y(0), Y(1), P);
                        OutlineSide(w, b, u, qi, qj, false, false, uCentre, Y(0), X(0), X(1), P);
                        OutlineSide(w, b, u, qi, qj, false, true, uCorner, Y(1), X(0), X(1), P);
                    }
            }
            Finish(b, v, tris);
        }

        /// <summary>An outline edge on the side of a quarter cell at x (or y) = at, if covered here and the quarter cell
        /// across it is not part of the ghost (checked by its cell; within a cell the halves always match).</summary>
        private void OutlineSide(TileWalls w, Blob b, int u, int qi, int qj, bool alongU, bool cornerSide, bool covered, float at, float from, float to,
            System.Func<float, float, int, Vector3> P)
        {
            if (!covered || !cornerSide) return; // only cell borders (the corner sides) can face outside the ghost
            int c = (qi >> 1) + Brush.CellsPerSide * (qj >> 1);
            int dir = alongU ? ((qi & 1) == 1 ? 0 : 1) : ((qj & 1) == 1 ? 2 : 3);
            int t2 = w.CellNeighbour(u, c, dir, out int c2);
            if (t2 >= 0 && InPlan(t2, c2)) return;
            for (int side = -1; side <= 1; side += 2)
            {
                if (alongU) { b.EdgeA.Add(P(at, from, side)); b.EdgeB.Add(P(at, to, side)); }
                else { b.EdgeA.Add(P(from, at, side)); b.EdgeB.Add(P(to, at, side)); }
            }
        }

        private static void Finish(Blob b, List<Vector3> v, List<int> tris)
        {
            b.Mesh.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            b.Mesh.SetVertices(v);
            var col = new Color[v.Count];
            for (int i = 0; i < col.Length; i++) col[i] = Color.white;
            b.Mesh.colors = col;
            b.Mesh.SetTriangles(tris, 0);
            b.Mesh.RecalculateBounds();
        }

        private static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        private static void Tri(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }

        private static void AddBoxGhost(List<Vector3> v, List<int> t, Vector3 lo, Vector3 hi)
        {
            Vector3 p000 = lo, p111 = hi;
            Vector3 p100 = new Vector3(hi.x, lo.y, lo.z), p010 = new Vector3(lo.x, hi.y, lo.z), p001 = new Vector3(lo.x, lo.y, hi.z);
            Vector3 p110 = new Vector3(hi.x, hi.y, lo.z), p101 = new Vector3(hi.x, lo.y, hi.z), p011 = new Vector3(lo.x, hi.y, hi.z);
            Quad(v, t, p010, p011, p111, p110); Quad(v, t, p000, p100, p101, p001); Quad(v, t, p000, p010, p110, p100);
            Quad(v, t, p101, p111, p011, p001); Quad(v, t, p001, p011, p010, p000); Quad(v, t, p100, p110, p111, p101);
        }
    }
}
