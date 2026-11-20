using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Cameras;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.World;
using UnityEngine;

namespace Sculpt.Game.Sculpting
{
    /// <summary>
    /// Everything you see and hear of sculpting, local to this client:
    /// - your hover: the piece outline in bright white-purple, a translucent ghost where it would go (red if invalid) and
    ///   an arrow for the direction (purple only ever means "sculptable");
    /// - windup tells: the piece's edges crackle in the sculptor's team colour with marks showing the action type
    ///   (chevrons along a push, up for raise, down for sink; a throw shakes, cracks and shows its lane to enemies in it),
    ///   plus a positional hum (a rising grind for a throw) that enemies hear;
    /// - thrown pieces in flight, dust and a deep thunk when pieces land, debris and a crunch on impact.
    /// Overlays are transparent and drawn before players, so nothing is ever drawn over an enemy.
    /// The HUD (<see cref="Sculpt.Game.UI.SculptHud"/>) reads the feedback fields at the bottom.
    /// </summary>
    public sealed class SculptView : MonoBehaviour
    {
        // Colours. Purple is reserved for "sculptable". Red only for invalid.
        public static readonly Color HoverEdge = new Color(0.93f, 0.85f, 1.00f, 0.95f);
        public static readonly Color GhostOk = new Color(0.78f, 0.62f, 1.00f, 0.28f);
        public static readonly Color GhostBad = new Color(1.00f, 0.18f, 0.15f, 0.30f);
        public static readonly Color BadEdge = new Color(1.00f, 0.25f, 0.2f, 0.95f);
        public static readonly Color[] Team =
        {
            new Color(0.25f, 0.55f, 1.00f, 1f), // team 1 blue
            new Color(1.00f, 0.50f, 0.15f, 1f), // team 2 orange
        };

        private LocalPlayer _player;
        private NetSession _session;
        private CameraRig _rig;
        private WorldView _world;
        private Material _lineMat, _ghostMat, _rockMat, _particleMat, _chipMat;
        private LineBatch _lines;
        private GameObject _ghost;
        private Mesh _ghostMesh;
        private MeshRenderer _ghostRenderer;
        private long _ghostKey;
        private readonly PieceMesher _pieceMesher = new PieceMesher();
        private readonly Dictionary<long, List<Vector3>> _edgeCache = new Dictionary<long, List<Vector3>>();
        private readonly Dictionary<object, AudioSource> _hums = new Dictionary<object, AudioSource>();
        private readonly Dictionary<object, AudioSource> _crystals = new Dictionary<object, AudioSource>();
        private PieceSnap _snap;
        private CameraShake _shake;
        private readonly Dictionary<object, List<Vec3d>> _lanes = new Dictionary<object, List<Vec3d>>();
        private readonly Dictionary<FlyingPiece, FlyView> _flying = new Dictionary<FlyingPiece, FlyView>();
        private readonly HashSet<object> _alive = new HashSet<object>();
        private readonly List<object> _dead = new List<object>();
        private ParticleSystem _dust, _debris;
        private SculptClient _boundSculpt;

        private sealed class FlyView
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3 Origin;
            public Vector3 StartCentre;
        }

        // ---- Feedback for the HUD ----
        public float RejectTime { get; private set; } = -10f;
        public SculptResult LastReject { get; private set; }
        public float HitMarkerTime { get; private set; } = -10f;
        public float HurtTime { get; private set; } = -10f;
        public int LastHurt { get; private set; }
        public float ConfirmTime { get; private set; } = -10f;

        public struct Warning
        {
            public int Sculptor;
            public SculptActionType Action;
            public float Time;
            public bool Throw;
        }

        /// <summary>Recent "you are being sculpted / you are in a throw lane" warnings, newest last.</summary>
        public readonly List<Warning> Warnings = new List<Warning>();

        public void Init(LocalPlayer player, NetSession session, CameraRig rig, WorldView world, CameraShake shake = null)
        {
            _shake = shake;
            _player = player;
            _session = session;
            _rig = rig;
            _world = world;
            Shader overlay = Shader.Find("SCULPT/Overlay");
            if (overlay == null) { Debug.LogError("[SCULPT] Shader SCULPT/Overlay not found."); overlay = Shader.Find("Universal Render Pipeline/Unlit"); }
            _lineMat = new Material(overlay) { name = "Sculpt lines" };
            _lineMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            _lineMat.renderQueue = 3000;
            _ghostMat = new Material(overlay) { name = "Sculpt ghost" };
            _ghostMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            _ghostMat.renderQueue = 2990;
            _particleMat = new Material(overlay) { name = "Sculpt particles" };
            _particleMat.SetFloat("_Soft", 1f);
            _particleMat.renderQueue = 3010;
            _rockMat = world.Material;
            _chipMat = new Material(Shader.Find("SCULPT/Voxel")) { name = "Debris" };

            _lines = new LineBatch("Sculpt Lines", _lineMat);
            _lines.Object.transform.SetParent(transform, false);

            _ghost = new GameObject("Sculpt Ghost");
            _ghost.transform.SetParent(transform, false);
            _ghostMesh = new Mesh { name = "Ghost" };
            _ghost.AddComponent<MeshFilter>().sharedMesh = _ghostMesh;
            _ghostRenderer = _ghost.AddComponent<MeshRenderer>();
            _ghostRenderer.sharedMaterial = _ghostMat;
            _ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ghost.SetActive(false);

            _snap = new PieceSnap(world, transform);
            _dust = MakeParticles("Dust", _particleMat, false);
            _debris = MakeParticles("Debris", _chipMat, true);
        }

        private ParticleSystem MakeParticles(string name, Material mat, bool chips)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.maxParticles = 4000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = chips ? 1.6f : -0.05f;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      // Chips use the terrain shader, where vertex alpha means "reinforced": keep it at 1 (they just stop).
                      chips ? new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) }
                            : new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0, 1) });
            col.color = g;
            var size = ps.sizeOverLifetime;
            size.enabled = !chips;
            if (!chips) size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.6f, 1, 1.6f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            if (chips)
            {
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = ChipMesh();
                var rot = ps.rotationBySpeed;
                rot.enabled = false;
                main.startRotation3D = true;
            }
            ps.Play();
            return ps;
        }

        /// <summary>A small irregular rock chip (debris only, never collides with anything).</summary>
        private static Mesh ChipMesh()
        {
            var m = new Mesh { name = "Chip" };
            var v = new[]
            {
                new Vector3(0, 0.55f, 0), new Vector3(0.5f, 0, 0.1f), new Vector3(-0.2f, 0.05f, 0.5f),
                new Vector3(-0.45f, -0.05f, -0.25f), new Vector3(0.15f, -0.1f, -0.5f), new Vector3(0.05f, -0.45f, 0.05f),
            };
            m.vertices = v;
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 1, 4, 5, 1, 2, 5, 2, 3, 5, 3, 4, 5, 4, 1 };
            m.RecalculateNormals();
            var c = new Color[v.Length];
            for (int i = 0; i < c.Length; i++) c[i] = Color.white;
            m.colors = c;
            return m;
        }

        // ================================================================ Frame

        private void LateUpdate()
        {
            if (_player == null || _rig == null || _rig.Camera == null) return;
            NetClient net = _session.Client;
            SculptClient sc = net.Sculpt;
            if (sc != _boundSculpt) { ClearAll(); _boundSculpt = sc; }
            if (sc == null || !net.Joined) return;

            _world.SetSculptView(_player.SimEye.ToUnity(), _player.InSculptMode, (float)_player.SculptConfig.RangeMeters);
            DrainEvents(sc);

            Vector3 cam = _rig.Camera.transform.position;
            _lines.Begin(cam);
            _alive.Clear();
            DrawHover(sc);
            DrawLocalWindups(sc, net);
            DrawRemoteWindups(sc, net);
            _lines.End();
            UpdateFlights(sc, net);
            _snap.Update(Time.unscaledDeltaTime);
            ReapHums();
            Warnings.RemoveAll(w => Time.time - w.Time > 1.5f);
        }

        private void ClearAll()
        {
            foreach (var h in _hums.Values) if (h != null) Destroy(h.gameObject);
            _hums.Clear();
            foreach (var h in _crystals.Values) if (h != null) Destroy(h.gameObject);
            _crystals.Clear();
            _snap?.Clear();
            foreach (var f in _flying.Values) { Destroy(f.Go); Destroy(f.Mesh); }
            _flying.Clear();
            _lanes.Clear();
            Warnings.Clear();
        }

        // ================================================================ Hover

        private void DrawHover(SculptClient sc)
        {
            if (!_player.HasHover || !_player.InSculptMode)
            {
                _ghost.SetActive(false);
                return;
            }
            SculptCheck c = _player.HoverCheck;
            SculptTarget t = _player.Target;
            bool ok = c.IsValid;
            if (c.Cells == null || c.Cells.Length == 0) { _ghost.SetActive(false); return; }

            // Outline of the piece that would move (bright white-purple), red edges if the action is refused.
            AddOutline(c.Cells, Int3.Zero, ok ? HoverEdge : BadEdge, 0.022f, 0f, 0);

            // Ghost where it would go.
            long key = CellsKey(c.Cells, c.Delta);
            if (key != _ghostKey)
            {
                _ghostKey = key;
                if (_pieceMesher.Build(c.Cells, c.Delta, _ghostMesh)) _ghost.transform.position = _pieceMesher.Origin;
                else _ghostMesh.Clear();
            }
            _ghostMat.SetColor("_Color", ok ? GhostOk : GhostBad);
            _ghost.SetActive(true);

            // Direction arrow on top of the piece.
            Bounds b = CellBounds(c.Cells, Int3.Zero);
            Vector3 top = new Vector3(b.center.x, b.max.y + 0.04f, b.center.z);
            Vector3 d = t.PushDir.ToVec().ToUnity();
            Chevrons(top - d * 0.25f, d, Vector3.up, 0.32f, ok ? HoverEdge : BadEdge, 2, 0.03f);
        }

        // ================================================================ Windups

        private void DrawLocalWindups(SculptClient sc, NetClient net)
        {
            Color team = TeamColour(sc.LocalTeam);
            foreach (LocalSculpt s in sc.Local)
            {
                if (s.Completed) continue;
                float progress = Mathf.Clamp01((float)((net.Tick - s.BeginTick + net.Alpha) / Mathf.Max(1, s.Check.WindupTicks)));
                Int3[] cells = s.Check.Cells;
                DrawTell(s, cells, s.Action, team, progress, sc.LocalId, false, sc);
                Hum(s, cells, s.Action.Type, progress, 0.35f, s.Action.Reinforce || s.Check.EnemyReinforced);
            }
        }

        private void DrawRemoteWindups(SculptClient sc, NetClient net)
        {
            double serverNow = net.RemoteRenderTick + net.Config.InterpDelayTicks;
            foreach (var kv in sc.Windups)
            {
                RemoteWindup w = kv.Value;
                PieceInfo p = sc.Mirror.Pieces.Get(w.Piece);
                Int3[] cells = p != null ? p.Cells.ToArray() : w.Check.Cells;
                if (cells == null || cells.Length == 0) continue;
                float progress = Mathf.Clamp01((float)((serverNow - w.StartTick) / Mathf.Max(1, w.Check.WindupTicks)));
                bool enemy = w.Team != sc.LocalTeam;
                DrawTell(w, cells, w.Action, TeamColour(w.Team), progress, w.PlayerId, w.WarnsMe && enemy, sc);
                Hum(w, cells, w.Action.Type, progress, enemy ? 1f : 0.6f, w.Action.Reinforce || w.Check.EnemyReinforced);
            }
        }

        private void DrawTell(object key, Int3[] cells, in SculptAction a, Color team, float progress, int sculptor, bool showLane, SculptClient sc)
        {
            _alive.Add(key);
            bool isThrow = a.Type == SculptActionType.Throw;
            float jitter = isThrow ? 0.015f + 0.05f * progress : 0.012f;
            float flicker = 0.65f + 0.35f * Mathf.PerlinNoise(Time.time * 37f, cells.Length * 0.01f);
            Color edge = team * (0.8f + 0.6f * progress) * flicker;
            edge.a = 1f;
            AddOutline(cells, Int3.Zero, edge, 0.03f + 0.012f * progress, jitter, cells.Length);

            Bounds b = CellBounds(cells, Int3.Zero);
            Vector3 top = new Vector3(b.center.x, b.max.y + 0.06f, b.center.z);
            Vector3 camRight = _rig.Camera.transform.right;
            switch (a.Type)
            {
                case SculptActionType.Move:
                {
                    Vector3 d = a.Direction.ToVec().ToUnity();
                    float slide = Mathf.Repeat(Time.time * 2.2f, 1f) * 0.3f;
                    Chevrons(top - d * 0.45f + d * slide, d, Vector3.up, 0.34f, edge, 3, 0.035f);
                    // Also on the face it moves toward, so the tell reads from the side.
                    Vector3 face = b.center + Vector3.Scale(d, b.extents) + d * 0.05f;
                    Chevrons(face - d * 0.01f, d, Vector3.up, 0.3f, edge, 1, 0.03f);
                    break;
                }
                case SculptActionType.Raise:
                    Chevrons(top + Vector3.up * (0.1f + 0.25f * Mathf.Repeat(Time.time * 2f, 1f)), Vector3.up, camRight, 0.34f, edge, 2, 0.035f);
                    break;
                case SculptActionType.Sink:
                    Chevrons(top + Vector3.up * (0.6f - 0.25f * Mathf.Repeat(Time.time * 2f, 1f)), Vector3.down, camRight, 0.34f, edge, 2, 0.035f);
                    break;
                case SculptActionType.Throw:
                {
                    // Cracks across the top, growing with the windup.
                    var rng = new System.Random(cells.Length * 7919 + sculptor);
                    int cracks = 3 + (int)(progress * 6);
                    for (int k = 0; k < cracks; k++)
                    {
                        Vector3 p0 = new Vector3(Mathf.Lerp(b.min.x, b.max.x, (float)rng.NextDouble()), b.max.y + 0.02f, Mathf.Lerp(b.min.z, b.max.z, (float)rng.NextDouble()));
                        Vector3 dir = new Vector3((float)rng.NextDouble() - 0.5f, 0, (float)rng.NextDouble() - 0.5f).normalized;
                        _lines.Add(p0, p0 + dir * (0.2f + 0.4f * progress), edge, 0.02f);
                    }
                    Vector3 look = a.LookDir.ToUnity().normalized;
                    Chevrons(b.center + look * (b.extents.magnitude + 0.2f), look, Vector3.up, 0.4f, edge, 2, 0.04f);
                    if (showLane || sculptor == sc.LocalId) DrawLane(key, cells, a, edge, sc, sculptor);
                    break;
                }
            }
        }

        /// <summary>The throw's predicted path: shown to enemies in the lane (and to the thrower).</summary>
        private void DrawLane(object key, Int3[] cells, in SculptAction a, Color colour, SculptClient sc, int sculptor)
        {
            if (!_lanes.TryGetValue(key, out var path))
            {
                PlayerBody body = sc.BodyOf(sculptor);
                if (body == null) return;
                var check = new SculptCheck { Cells = cells, Result = SculptResult.Valid, Type = SculptActionType.Throw };
                path = sc.Mirror.PredictThrowPath(body, check, a.LookDir);
                _lanes[key] = path;
            }
            float t = Time.time * 3f;
            for (int i = 2; i + 2 < path.Count; i += 4)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin(i * 0.5f - t * 4f);
                Color c = colour * pulse;
                c.a = 0.9f;
                _lines.Add(path[i].ToUnity(), path[i + 2].ToUnity(), c, 0.05f);
            }
        }

        /// <summary>
        /// The positional windup sound (enemies hear it): its timbre says the action, its pitch bends with progress
        /// (a raise rises, a sink falls, a throw grinds upward), and a reinforced action adds a glassy layer.
        /// </summary>
        private void Hum(object key, Int3[] cells, SculptActionType type, float progress, float volume, bool reinforced)
        {
            _alive.Add(key);
            if (!_hums.TryGetValue(key, out AudioSource s) || s == null)
            {
                s = Synth.Loop("Sculpt Hum", transform, SculptAudio.HumFor(type), 3f, 40f);
                s.time = Random.Range(0f, s.clip.length * 0.9f); // two hums never phase together
                _hums[key] = s;
            }
            Vector3 at = CellBounds(cells, Int3.Zero).center;
            s.transform.position = at;
            s.volume = volume * (0.45f + 0.55f * progress);
            switch (type)
            {
                case SculptActionType.Throw: s.pitch = 0.7f + 0.9f * progress; break;
                case SculptActionType.Raise: s.pitch = 0.85f + 0.4f * progress; break;
                case SculptActionType.Sink: s.pitch = 1.1f - 0.35f * progress; break;
                default: s.pitch = 0.95f + 0.15f * progress; break;
            }
            if (!reinforced) return;
            if (!_crystals.TryGetValue(key, out AudioSource c) || c == null)
            {
                c = Synth.Loop("Sculpt Hum Crystal", transform, SculptAudio.HumCrystal, 3f, 40f);
                _crystals[key] = c;
            }
            c.transform.position = at;
            c.volume = volume * 0.8f * (0.6f + 0.4f * progress);
            c.pitch = 0.9f + 0.2f * progress;
        }

        private void ReapHums()
        {
            _dead.Clear();
            foreach (var kv in _hums) if (!_alive.Contains(kv.Key)) _dead.Add(kv.Key);
            foreach (var k in _dead)
            {
                if (_hums[k] != null) Destroy(_hums[k].gameObject);
                _hums.Remove(k);
                _lanes.Remove(k);
                if (_crystals.TryGetValue(k, out AudioSource c) && c != null) Destroy(c.gameObject);
                _crystals.Remove(k);
            }
        }

        // ================================================================ Flights

        private void UpdateFlights(SculptClient sc, NetClient net)
        {
            double renderTick = net.RemoteRenderTick;
            foreach (FlyingPiece f in sc.Flying)
            {
                if (f.State == null) continue;
                if (!_flying.TryGetValue(f, out FlyView v))
                {
                    v = new FlyView { Mesh = new Mesh { name = "Flying piece" } };
                    v.Go = new GameObject("Flying Piece");
                    v.Go.transform.SetParent(transform, false);
                    v.Go.AddComponent<MeshFilter>().sharedMesh = v.Mesh;
                    var r = v.Go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = _rockMat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (f.Cells != null && f.Cells.Length > 0 && _pieceMesher.Build(f.Cells, Int3.Zero, v.Mesh)) v.Origin = _pieceMesher.Origin;
                    v.StartCentre = CellBounds(f.Cells ?? new Int3[0], Int3.Zero).center;
                    _flying[f] = v;
                }
                Vector3 pos;
                bool own = f.Owner == sc.LocalId;
                if (own) pos = Vector3.Lerp(f.PrevPosition.ToUnity(), f.State.Position.ToUnity(), (float)net.Alpha);
                else
                {
                    int target = (int)System.Math.Floor(renderTick);
                    if (target < f.StateTick) { v.Go.SetActive(false); continue; }
                    sc.FlyTo(f, target);
                    pos = Vector3.Lerp(f.PrevPosition.ToUnity(), f.State.Position.ToUnity(), (float)(renderTick - target));
                }
                v.Go.SetActive(true);
                v.Go.transform.position = v.Origin + (pos - v.StartCentre);
            }
            _dead.Clear();
            foreach (var kv in _flying)
            {
                bool still = false;
                foreach (var f in sc.Flying) if (f == kv.Key) { still = true; break; }
                if (!still) _dead.Add(kv.Key);
            }
            foreach (var k in _dead)
            {
                var v = _flying[(FlyingPiece)k];
                Destroy(v.Go);
                Destroy(v.Mesh);
                _flying.Remove((FlyingPiece)k);
            }
        }

        // ================================================================ Events

        private void DrainEvents(SculptClient sc)
        {
            foreach (ClientSculptEvent e in sc.Events)
            {
                switch (e.Type)
                {
                    case ClientSculptEventType.LocalBegan:
                        ConfirmTime = Time.time;
                        SculptAudio.Play(e.Reinforce ? SculptAudio.ConfirmHeavy : SculptAudio.Confirm, Vector3.zero, 0.6f, 1f, false);
                        break;
                    case ClientSculptEventType.LocalLanded:
                        if (e.Action != SculptActionType.Throw) { Landed(e.Cells, true, e.Reinforce); _snap.Snap(e.Cells, e.Delta); }
                        break;
                    case ClientSculptEventType.Completed:
                        if (!e.Mine && e.Action != SculptActionType.Throw) { Landed(e.Cells, false, e.Reinforce); _snap.Snap(e.Cells, e.Delta); }
                        break;
                    case ClientSculptEventType.PieceLanded:
                        Landed(e.Cells, false, false);
                        break;
                    case ClientSculptEventType.Rejected:
                        RejectTime = Time.time;
                        LastReject = e.Result;
                        SculptAudio.Play(SculptAudio.Reject, Vector3.zero, 0.5f, 1f, false);
                        break;
                    case ClientSculptEventType.Warned:
                        Warnings.Add(new Warning { Sculptor = e.PlayerId, Action = e.Action, Time = Time.time, Throw = e.Action == SculptActionType.Throw });
                        // A distinct sound from below.
                        SculptAudio.Play(SculptAudio.Warn, _player.RenderFeet + Vector3.down * 0.6f, 0.9f, 1f, true);
                        break;
                    case ClientSculptEventType.ThrowImpact:
                        Impact(e.Point.ToUnity(), e.Value);
                        break;
                    case ClientSculptEventType.ThrowHit:
                        if (e.PlayerId == sc.LocalId) { HurtTime = Time.time; LastHurt = e.Value; }
                        if (e.Mine) HitMarkerTime = Time.time;
                        break;
                    case ClientSculptEventType.ThrowLaunched:
                        if (e.Cells != null && e.Cells.Length > 0) Dust(CellBounds(e.Cells, Int3.Zero), 0.6f);
                        break;
                }
            }
            sc.Events.Clear();
        }

        private void Landed(Int3[] cells, bool mine, bool reinforced)
        {
            if (cells == null || cells.Length == 0) return;
            Bounds b = CellBounds(cells, Int3.Zero);
            Dust(b, reinforced ? 1.3f : 1f);
            // Bigger pieces land deeper; a little random pitch so repeats never sound mechanical.
            float size = Mathf.Clamp01(cells.Length / 256f);
            float pitch = (1.12f - 0.3f * size) * Random.Range(0.96f, 1.04f);
            SculptAudio.Play(reinforced ? SculptAudio.ThunkHeavy : SculptAudio.Thunk, b.center, mine ? 0.95f : 1f, pitch, true);
            // Feel it when it is close: a short cosmetic camera roll (never pitch or yaw, so aim is untouched).
            float dist = Vector3.Distance(b.center, _rig.Camera.transform.position);
            if (dist < 8f && _shake != null) _shake.Roll += (Random.value < 0.5f ? -1f : 1f) * (reinforced ? 0.55f : 0.35f) * (1f - dist / 8f);
        }

        private void Dust(Bounds b, float amount)
        {
            int n = Mathf.Clamp((int)(b.size.x * b.size.z * 6f * amount), 8, 80);
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                // Along the base edges, puffing outward.
                float u = Random.value, side = Random.value;
                Vector3 p = side < 0.25f ? new Vector3(Mathf.Lerp(b.min.x, b.max.x, u), b.min.y, b.min.z)
                          : side < 0.5f ? new Vector3(Mathf.Lerp(b.min.x, b.max.x, u), b.min.y, b.max.z)
                          : side < 0.75f ? new Vector3(b.min.x, b.min.y, Mathf.Lerp(b.min.z, b.max.z, u))
                          : new Vector3(b.max.x, b.min.y, Mathf.Lerp(b.min.z, b.max.z, u));
                Vector3 outward = (p - b.center);
                outward.y = 0;
                ep.position = p + Vector3.up * Random.Range(0.05f, 0.4f);
                ep.velocity = outward.normalized * Random.Range(0.6f, 2.2f) + Vector3.up * Random.Range(0.2f, 0.8f);
                ep.startSize = Random.Range(0.35f, 0.8f);
                ep.startLifetime = Random.Range(0.5f, 1.1f);
                float g = Random.Range(0.62f, 0.78f);
                ep.startColor = new Color(g, g * 0.95f, g * 0.86f, 0.55f);
                _dust.Emit(ep, 1);
            }
        }

        private void Impact(Vector3 at, int subCells)
        {
            int chips = Mathf.Clamp(subCells / 6, 12, 90);
            var ep = new ParticleSystem.EmitParams();
            Color rock = ChunkMesher.RockHigh;
            for (int i = 0; i < chips; i++)
            {
                ep.position = at + Random.insideUnitSphere * 0.4f;
                ep.velocity = Random.insideUnitSphere * 5f + Vector3.up * Random.Range(1.5f, 5f);
                ep.startSize3D = new Vector3(Random.Range(0.05f, 0.18f), Random.Range(0.04f, 0.14f), Random.Range(0.05f, 0.18f));
                ep.rotation3D = Random.insideUnitSphere * 180f;
                ep.startLifetime = Random.Range(0.6f, 1.4f);
                float v = Random.Range(0.85f, 1.05f);
                ep.startColor = new Color(rock.r * v, rock.g * v, rock.b * v, 1f);
                _debris.Emit(ep, 1);
            }
            Dust(new Bounds(at, new Vector3(1.2f, 0.2f, 1.2f)), 1.4f);
            SculptAudio.Play(SculptAudio.Crunch, at, 1f, Random.Range(0.92f, 1.06f), true);
        }

        // ================================================================ Geometry helpers

        private static Color TeamColour(int team) => Team[Mathf.Abs(team - 1) % Team.Length];

        public static Bounds CellBounds(Int3[] cells, Int3 offset)
        {
            if (cells == null || cells.Length == 0) return new Bounds();
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c0 in cells)
            {
                Int3 c = c0 + offset;
                x0 = Mathf.Min(x0, c.X); y0 = Mathf.Min(y0, c.Y); z0 = Mathf.Min(z0, c.Z);
                x1 = Mathf.Max(x1, c.X); y1 = Mathf.Max(y1, c.Y); z1 = Mathf.Max(z1, c.Z);
            }
            float s = (float)SubGrid.SubSize;
            var b = new Bounds();
            b.SetMinMax(new Vector3(x0, y0, z0) * s, new Vector3(x1 + 1, y1 + 1, z1 + 1) * s);
            return b;
        }

        private static long CellsKey(Int3[] cells, Int3 delta)
        {
            unchecked
            {
                long h = cells.Length * 1000003L;
                for (int i = 0; i < cells.Length; i += System.Math.Max(1, cells.Length / 16))
                    h = h * 31 + cells[i].X * 73856093L + cells[i].Y * 19349663L + cells[i].Z * 83492791L;
                Int3 last = cells[cells.Length - 1];
                h = h * 31 + last.X * 73856093L + last.Y * 19349663L + last.Z * 83492791L;
                return h * 17 + delta.X * 3 + delta.Y * 5 + delta.Z * 7;
            }
        }

        private void AddOutline(Int3[] cells, Int3 offset, Color colour, float width, float jitter, int seed)
        {
            long key = CellsKey(cells, offset);
            if (!_edgeCache.TryGetValue(key, out var segs))
            {
                if (_edgeCache.Count > 64) _edgeCache.Clear();
                segs = FeatureEdges(cells, offset);
                _edgeCache[key] = segs;
            }
            float t = Time.time * 30f;
            for (int i = 0; i + 1 < segs.Count; i += 2)
            {
                Vector3 a = segs[i], b = segs[i + 1];
                if (jitter > 0)
                {
                    a += new Vector3(Mathf.PerlinNoise(t, i * 0.37f) - 0.5f, Mathf.PerlinNoise(i * 0.21f, t) - 0.5f, Mathf.PerlinNoise(t + i, seed * 0.1f) - 0.5f) * (2 * jitter);
                    b += new Vector3(Mathf.PerlinNoise(t + 3, i * 0.37f) - 0.5f, Mathf.PerlinNoise(i * 0.21f, t + 3) - 0.5f, Mathf.PerlinNoise(t + i + 3, seed * 0.1f) - 0.5f) * (2 * jitter);
                }
                _lines.Add(a, b, colour, width);
            }
        }

        /// <summary>
        /// The silhouette and crease edges of a set of sub-cells (no lines across flat faces), as segment pairs. An edge
        /// is drawn when the four cells around it are not a flat arrangement (one or three occupied, or two diagonal).
        /// </summary>
        public static List<Vector3> FeatureEdges(Int3[] cells, Int3 offset)
        {
            var set = new HashSet<Int3>(cells);
            var seen = new HashSet<long>();
            var segs = new List<Vector3>();
            float s = (float)SubGrid.SubSize;
            foreach (var c in cells)
                for (int axis = 0; axis < 3; axis++)
                {
                    Int3 ea = Unit(axis), eb = Unit((axis + 1) % 3), ec = Unit((axis + 2) % 3);
                    for (int i = 0; i <= 1; i++)
                        for (int j = 0; j <= 1; j++)
                        {
                            Int3 v = c + eb * i + ec * j; // edge from v to v + ea
                            long k = (((long)(v.X + 32768) * 65536 + (v.Y + 32768)) * 65536 + (v.Z + 32768)) * 3 + axis;
                            if (!seen.Add(k)) continue;
                            bool c11 = set.Contains(v), c01 = set.Contains(v - eb), c10 = set.Contains(v - ec), c00 = set.Contains(v - eb - ec);
                            int n = (c11 ? 1 : 0) + (c01 ? 1 : 0) + (c10 ? 1 : 0) + (c00 ? 1 : 0);
                            bool draw = n == 1 || n == 3 || (n == 2 && ((c11 && c00) || (c01 && c10)));
                            if (!draw) continue;
                            segs.Add((v + offset).ToUnity() * s);
                            segs.Add((v + ea + offset).ToUnity() * s);
                        }
                }
            return segs;
        }

        private static Int3 Unit(int axis) => axis == 0 ? new Int3(1, 0, 0) : axis == 1 ? new Int3(0, 1, 0) : new Int3(0, 0, 1);

        /// <summary>Chevrons pointing along <paramref name="dir"/>, opening in the plane of dir and <paramref name="side"/>'s normal.</summary>
        private void Chevrons(Vector3 at, Vector3 dir, Vector3 planeNormal, float size, Color colour, int count, float width)
        {
            Vector3 side = Vector3.Cross(planeNormal, dir);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(Vector3.forward, dir);
            side = side.normalized;
            for (int k = 0; k < count; k++)
            {
                Vector3 tip = at + dir * (size * (0.55f * k + 0.5f));
                Vector3 back = tip - dir * size * 0.45f;
                _lines.Add(back + side * size * 0.45f, tip, colour, width);
                _lines.Add(tip, back - side * size * 0.45f, colour, width);
            }
        }

        private void OnDestroy()
        {
            ClearAll();
            if (_ghostMesh != null) Destroy(_ghostMesh);
        }
    }
}
