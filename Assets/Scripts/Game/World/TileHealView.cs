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
    /// Healing on the map (Ben 2026-11-21, placeholder art). Purple means "sculptable" only:
    /// - a faint purple outline on every tile in sculpt range that can be healed (cracked, broken, chipped patch);
    /// - in sculpt mode, a bright outline on the tile the crosshair picks and a ghost of the panel a click would put
    ///   there (a quiet outline only, never red, when the click couldn't work: red is for a click that was refused);
    /// - a heal in progress fills its ghost panel over the heal time with crackling edges in the healer's team colour
    ///   and a hum everyone nearby hears, so attackers can see the hole is about to close;
    /// - a finished heal thunks; a refused or cancelled one of yours flashes the tile red.
    /// </summary>
    public sealed class TileHealView : MonoBehaviour
    {
        private static readonly Color Glow = new Color(0.62f, 0.33f, 1.0f, 0.22f);
        private static readonly Color ReinforceGhost = new Color(0.80f, 0.86f, 1.00f, 0.32f);
        private static readonly Color Red = new Color(1.00f, 0.22f, 0.18f, 0.95f);

        private LocalPlayer _player;
        private NetSession _session;
        private CameraRig _rig;
        private Material _lineMat, _ghostMat;
        private LineBatch _lines;
        private Mesh _cube;
        private MaterialPropertyBlock _mpb;
        private readonly List<int> _near = new List<int>();
        private readonly Dictionary<int, AudioSource> _hums = new Dictionary<int, AudioSource>();
        private readonly HashSet<int> _alive = new HashSet<int>();
        private readonly List<int> _dead = new List<int>();
        private readonly Dictionary<int, float> _redUntil = new Dictionary<int, float>();
        private NetClient _bound;

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
            _ghostMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            _ghostMat.renderQueue = 2990;
            _lines = new LineBatch("Heal Lines", _lineMat);
            _lines.Object.transform.SetParent(transform, false);
            _cube = UnitCube();
            _mpb = new MaterialPropertyBlock();
        }

        private void LateUpdate()
        {
            if (_player == null || _rig == null || _rig.Camera == null || _lines == null) return;
            NetClient net = _session.Client;
            if (net != _bound) { ClearHums(); _redUntil.Clear(); _bound = net; }
            _lines.Begin(_rig.Camera.transform.position);
            _alive.Clear();
            if (net.Joined && net.Heals != null && _player.Heals)
            {
                DrainEvents(net);
                DrawGlow(net);
                DrawTarget(net);
                DrawHeals(net);
                DrawRed(net);
            }
            _lines.End();
            ReapHums();
        }

        // ---------------------------------------------------------------- what can be healed

        private void DrawGlow(NetClient net)
        {
            TileWalls walls = net.Walls;
            Vec3d eye = _player.SimEye;
            double r = walls.Config.HealRange;
            walls.TilesNear(new Aabb(eye - new Vec3d(r, r, r), eye + new Vec3d(r, r, r)), _near);
            foreach (int t in _near)
            {
                if (TileHealing.StateCheck(walls[t], HealKind.Heal, walls.Config) != HealResult.Ok) continue;
                if ((walls.BoundsOf(t).ClosestPoint(eye) - eye).Length > r) continue;
                if (t == _player.HealTarget.Tile && _player.InSculptMode) continue; // drawn bright below
                Outline(walls.BoundsOf(t), Glow, 0.02f, 0f);
            }
        }

        private void DrawTarget(NetClient net)
        {
            if (!_player.InSculptMode) return;
            TileTarget t = _player.HealTarget;
            if (!t.Valid) return;
            if (net.TryGetOwnHeal(out HealJob mine, out _) && mine.Tile == t.Tile) return; // the running heal is drawn instead
            Aabb b = net.Walls.BoundsOf(t.Tile);
            bool ok = _player.HealCheck == HealResult.Ok || _player.HealCheck == HealResult.NotInSculptMode;
            Color edge = SculptView.HoverEdge;
            if (!ok)
            {
                Outline(b, new Color(edge.r, edge.g, edge.b, edge.a * 0.4f), 0.02f, 0f);
                return;
            }
            Outline(b, edge, 0.03f, 0f);
            bool reinforce = _player.HealKindNow == HealKind.Reinforce;
            Ghost(b, reinforce ? ReinforceGhost : SculptView.GhostOk, 0.5f);
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
            Aabb b = net.Walls.BoundsOf(j.Tile);
            float jitter = 0.01f + 0.03f * progress;
            Outline(b, new Color(team.r, team.g, team.b, 0.95f), 0.03f + 0.015f * progress, jitter);
            Color g = j.Kind == HealKind.Reinforce ? ReinforceGhost : SculptView.GhostOk;
            Ghost(b, new Color(g.r, g.g, g.b, 0.08f + 0.5f * progress), progress);
            Hum(j.Player, b, progress, volume, j.Kind == HealKind.Reinforce);
        }

        private void Hum(int player, Aabb b, float progress, float volume, bool reinforce)
        {
            _alive.Add(player);
            if (!_hums.TryGetValue(player, out AudioSource s) || s == null)
            {
                s = Synth.Loop("Heal Hum", transform, SculptAudio.HumRaise, 3f, 40f);
                s.time = Random.Range(0f, s.clip.length * 0.9f);
                _hums[player] = s;
            }
            s.transform.position = Centre(b);
            s.volume = volume * (0.45f + 0.55f * progress);
            s.pitch = (reinforce ? 0.8f : 1f) * (0.9f + 0.25f * progress);
        }

        private void ReapHums()
        {
            _dead.Clear();
            foreach (var kv in _hums) if (!_alive.Contains(kv.Key)) _dead.Add(kv.Key);
            foreach (int k in _dead)
            {
                if (_hums[k] != null) Destroy(_hums[k].gameObject);
                _hums.Remove(k);
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
                        if (mine && e.Result != HealResult.Replaced && e.Result != HealResult.Stopped)
                        {
                            _redUntil[e.Tile] = Time.time + 0.45f;
                            _player.HealRejectTime = Time.time;
                            _player.LastHealReject = e.Result;
                            _player.LastHealRejectTile = e.Tile;
                            SculptAudio.Play(SculptAudio.Reject, Vector3.zero, 0.6f, 1f, false);
                        }
                        break;
                    case HealEventType.Rejected:
                        if (mine)
                        {
                            _redUntil[e.Tile] = Time.time + 0.45f;
                            _player.HealRejectTime = Time.time;
                            _player.LastHealReject = e.Result;
                            _player.LastHealRejectTile = e.Tile;
                            SculptAudio.Play(SculptAudio.Reject, Vector3.zero, 0.6f, 1f, false);
                        }
                        break;
                    case HealEventType.Began:
                        if (mine) SculptAudio.Play(e.Kind == HealKind.Reinforce ? SculptAudio.ConfirmHeavy : SculptAudio.Confirm, Vector3.zero, 0.5f, 1f, false);
                        break;
                }
            }
            net.HealEvents.Clear();
        }

        private void DrawRed(NetClient net)
        {
            if (_player.LastHealRejectTile >= 0 && Time.time - _player.HealRejectTime < 0.45f) _redUntil[_player.LastHealRejectTile] = _player.HealRejectTime + 0.45f;
            _dead.Clear();
            foreach (var kv in _redUntil)
            {
                float left = kv.Value - Time.time;
                if (left <= 0 || kv.Key >= net.Walls.Count) { _dead.Add(kv.Key); continue; }
                Outline(net.Walls.BoundsOf(kv.Key), new Color(Red.r, Red.g, Red.b, Red.a * Mathf.Clamp01(left / 0.45f)), 0.035f, 0f);
            }
            foreach (int k in _dead) _redUntil.Remove(k);
        }

        // ---------------------------------------------------------------- drawing

        private static Color TeamColour(int team) => team <= 0 ? SculptView.HoverEdge : SculptView.Team[Mathf.Abs(team - 1) % SculptView.Team.Length];

        private static Vector3 Centre(Aabb b) => ((b.Min + b.Max) * 0.5).ToUnity();

        /// <summary>The twelve edges of a tile box, pushed out a hair so they never z-fight the brick.</summary>
        private void Outline(Aabb box, Color c, float width, float jitter)
        {
            Vector3 lo = box.Min.ToUnity() - Vector3.one * 0.01f, hi = box.Max.ToUnity() + Vector3.one * 0.01f;
            float t = Time.time * 30f;
            for (int e = 0; e < 12; e++)
            {
                int axis = e / 4, k = e % 4;
                Vector3 a = lo, b = lo;
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                a[u] = (k & 1) != 0 ? hi[u] : lo[u];
                a[v] = (k & 2) != 0 ? hi[v] : lo[v];
                b[u] = a[u];
                b[v] = a[v];
                b[axis] = hi[axis];
                if (jitter > 0)
                {
                    float n = Mathf.PerlinNoise(t + e * 1.7f, e * 3.1f) - 0.5f;
                    Vector3 off = new Vector3(n, -n, n) * jitter;
                    a += off;
                    b -= off;
                }
                _lines.Add(a, b, c, width);
            }
        }

        /// <summary>A translucent panel filling the tile (it grows in from the edges as <paramref name="fill"/> rises).</summary>
        private void Ghost(Aabb box, Color c, float fill)
        {
            Vector3 centre = Centre(box);
            Vector3 size = (box.Max - box.Min).ToUnity();
            float k = 0.6f + 0.4f * Mathf.Clamp01(fill);
            for (int axis = 0; axis < 3; axis++) if (size[axis] > 0.5f) size[axis] *= k; // shrink the face, not the thickness
            size += Vector3.one * 0.004f;
            _mpb.SetColor("_Color", c);
            Graphics.DrawMesh(_cube, Matrix4x4.TRS(centre, Quaternion.identity, size), _ghostMat, 0, null, 0, _mpb, false, false);
        }

        /// <summary>A unit cube centred on the origin with white vertex colours (the overlay shader multiplies them).</summary>
        private static Mesh UnitCube()
        {
            var m = new Mesh { name = "Heal ghost cube" };
            var v = new List<Vector3>();
            var tris = new List<int>();
            for (int f = 0; f < 6; f++)
            {
                int axis = f / 2;
                float s = (f & 1) == 0 ? -0.5f : 0.5f;
                int u = (axis + 1) % 3, w = (axis + 2) % 3;
                int i0 = v.Count;
                for (int q = 0; q < 4; q++)
                {
                    Vector3 p = Vector3.zero;
                    p[axis] = s;
                    p[u] = (q == 1 || q == 2) ? 0.5f : -0.5f;
                    p[w] = q >= 2 ? 0.5f : -0.5f;
                    v.Add(p);
                }
                // Outward winding: flip for the negative faces.
                if (s > 0) { tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2); tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3); }
                else { tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1); tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2); }
            }
            m.SetVertices(v);
            m.SetTriangles(tris, 0);
            var col = new Color[v.Count];
            for (int i = 0; i < col.Length; i++) col[i] = Color.white;
            m.colors = col;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
