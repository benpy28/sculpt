using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Cameras;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Sculpting;
using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Weapons
{
    /// <summary>
    /// Everything you see and hear of guns. Local shots come straight from prediction (same tick you clicked):
    /// muzzle flash, heavy viewmodel kick, camera roll, sound, tracers and impacts traced on your own copy of the world.
    /// Other players' shots come from the server's combat message. Hit markers, kills and damage taken are only shown
    /// when the server confirms them (never guessed), and feed the combat HUD.
    /// Frame order: <see cref="PreCamera"/> (consume events, springs, shake) then the camera, then <see cref="PostCamera"/>.
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        // ---- HUD state (read by CombatHud) ----
        public float HitTime = -10f, HeadHitTime = -10f, KillMarkTime = -10f;
        public int LastHitDamage;
        public float HurtTime = -10f;
        public int LastHurt;
        public readonly List<DamageArrow> Arrows = new List<DamageArrow>();
        public readonly List<FeedLine> Feed = new List<FeedLine>();
        public int KilledBy;
        public DamageSource KilledWith;
        /// <summary>Gets the server's grenade events (set by the bootstrap).</summary>
        public GrenadeView Grenades;

        public struct DamageArrow { public Vector3 From; public float Time; public int Damage; }
        public struct FeedLine { public float Time; public int Killer, Victim; public DamageSource Source; public bool Head; }

        private LocalPlayer _player;
        private NetSession _session;
        private CameraRig _rig;
        private FirstPersonMode _fp;
        private ClientSettings _settings;
        private MovementConfig _mc;

        private Transform _vmRoot;
        private readonly GunModel[] _guns = new GunModel[4];
        private GameObject _grenade;
        private GameObject _fist;
        private int _lastPunch = int.MinValue;
        private bool _punchHitDone = true;
        /// <summary>0..1 how far the gun is pushed aside by a punch this frame.</summary>
        private float _punchAside;
        private Material _vmMat, _lineMat, _flashMat, _puffMat;
        private LineBatch _lines, _flashLines;
        private ParticleSystem _puffs, _sparks;

        private struct Tracer { public Vector3 A, B; public float Born, Life; public Color Colour; public float Width; }
        private struct Flash { public Vector3 At, Fwd; public float Born; public float Size; public bool Local; }
        private struct PendingLocal { public WeaponShot Shot; public List<Vector3> Ends; }

        private readonly List<Tracer> _tracers = new List<Tracer>();
        private readonly List<Flash> _flashes = new List<Flash>();
        private readonly List<PendingLocal> _pendingLocal = new List<PendingLocal>();
        private readonly List<HitTarget> _targets = new List<HitTarget>();
        private readonly List<int> _ids = new List<int>();
        private readonly List<float> _rackAt = new List<float>();

        // Viewmodel springs (camera-local).
        private Vector3 _kickPos, _kickPosVel, _kickRot, _kickRotVel;
        private float _lower = 1f, _sprint, _bobPhase, _slideTilt;
        // Reload: pose blend, and what the last frame saw (to fire the sounds on the right beats).
        private float _reload, _shellBob;
        private bool _wasReloading;
        private readonly int[] _lastAmmo = { -1, -1, -1, -1 };
        private Vector2 _sway;
        private float _lastShotTime = -10f;
        private Weapon _shownWeapon;
        private float _rollVel;

        private static readonly Color TracerColour = new Color(1f, 0.86f, 0.55f, 0.9f);
        private static readonly Color FlashCore = new Color(1f, 0.93f, 0.7f, 1f);
        private static readonly Color FlashOuter = new Color(1f, 0.62f, 0.22f, 0.85f);

        /// <summary>Viewmodel feel per gun: kick back (m/s impulse), kick up (deg/s impulse), spring stiffness, damping,
        /// camera roll impulse (deg/s) and FOV punch (deg). Heavier guns kick harder and settle slower.</summary>
        private static readonly float[,] Feel =
        {
            // back,  up,   w,  zeta, roll, fov
            { 2.4f, 300f, 13f, 0.55f, 90f, 2.6f }, // pump
            { 0.75f, 60f, 27f, 0.7f, 18f, 0.35f }, // SMG
            { 1.1f, 100f, 22f, 0.65f, 30f, 0.7f }, // AR
            { 1.0f, 190f, 19f, 0.6f, 35f, 0.6f },  // pistol
        };

        public void Init(LocalPlayer player, NetSession session, CameraRig rig, FirstPersonMode fp, ClientSettings settings, MovementConfig mc)
        {
            _player = player;
            _session = session;
            _rig = rig;
            _fp = fp;
            _settings = settings;
            _mc = mc;

            Shader vm = Shader.Find("SCULPT/Viewmodel");
            if (vm == null) { Debug.LogError("[SCULPT] Shader SCULPT/Viewmodel not found."); vm = Shader.Find("Universal Render Pipeline/Unlit"); }
            _vmMat = new Material(vm) { name = "Viewmodel" };
            Shader overlay = Shader.Find("SCULPT/Overlay");
            _lineMat = new Material(overlay) { name = "Tracers" };
            _lineMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            _lineMat.renderQueue = 3020;
            _flashMat = new Material(overlay) { name = "Muzzle flash (own)" };
            _flashMat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
            _flashMat.renderQueue = 3150;
            _puffMat = new Material(overlay) { name = "Impact puffs" };
            _puffMat.SetFloat("_Soft", 1f);
            _puffMat.renderQueue = 3015;

            _lines = new LineBatch("Tracers", _lineMat);
            _lines.Object.transform.SetParent(transform, false);
            _flashLines = new LineBatch("Own muzzle flash", _flashMat);
            _flashLines.Object.transform.SetParent(transform, false);

            _vmRoot = new GameObject("Viewmodel").transform;
            _vmRoot.SetParent(rig.Camera.transform, false);
            for (int i = 0; i < 4; i++)
            {
                _guns[i] = GunModels.Build((Weapon)i, _vmMat);
                _guns[i].Root.transform.SetParent(_vmRoot, false);
                _guns[i].Root.SetActive(false);
            }
            _grenade = GunModels.BuildGrenade(_vmMat);
            _grenade.transform.SetParent(_vmRoot, false);
            _grenade.SetActive(false);
            _fist = GunModels.BuildFist(_vmMat);
            _fist.transform.SetParent(rig.Camera.transform, false);
            _fist.SetActive(false);
            _puffs = MakeParticles("Impact puffs", _puffMat, -0.1f);
            _sparks = MakeParticles("Impact sparks", _puffMat, 1.2f);
        }

        private ParticleSystem MakeParticles(string name, Material mat, float gravity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.maxParticles = 3000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = gravity;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.5f, 0.4f), new GradientAlphaKey(0, 1) });
            col.color = g;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.7f, 1, 1.5f));
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
            ps.Play();
            return ps;
        }

        // ================================================================ Frame

        /// <summary>Reload beats from the predicted gun state: mag out when it starts, mag in when the rounds arrive,
        /// one shell sound per pump shell. Instant from prediction, like your own shots.</summary>
        private void ReloadSounds()
        {
            MotorState s = _player.State;
            WeaponsConfig cfg = _mc.Weapons;
            Vector3 at = _rig.Camera.transform.position;
            bool reloading = s.Weapon.Reloading && !s.Dead;
            Weapon held = s.Hand.Held;
            bool hasGun = s.Hand.HasGun;
            if (reloading && hasGun && !_wasReloading && !cfg.Of(held).ShellReload)
                GunAudio.Play(GunAudio.MagOut, at, 0.7f, Random.Range(0.97f, 1.03f), false);
            _wasReloading = reloading;
            for (int i = 0; i < 4; i++)
            {
                int ammo = s.Weapon.AmmoOf((Weapon)i, cfg);
                if (_lastAmmo[i] >= 0 && ammo > _lastAmmo[i] && !s.Dead && hasGun && (int)held == i)
                {
                    if (cfg.Of((Weapon)i).ShellReload) { GunAudio.Play(GunAudio.ShellIn, at, 0.7f, Random.Range(0.95f, 1.05f), false); _shellBob = 1f; }
                    else GunAudio.Play(GunAudio.MagIn, at, 0.8f, Random.Range(0.97f, 1.03f), false);
                }
                _lastAmmo[i] = ammo;
            }
        }

        public void PreCamera(float dt)
        {
            NetClient net = _session.Client;
            float now = Time.time;
            if (net.Joined)
            {
                foreach (WeaponShot shot in net.LocalShots) OnLocalShot(shot, now);
                foreach (var kv in net.Combat) OnCombat(kv.Value, now);
            }
            net.LocalShots.Clear();
            net.Combat.Clear();
            foreach (MoveEvent e in _player.Events)
                if (e.Type == MoveEventType.ChargedThrough)
                {
                    _rollVel += Random.Range(0, 2) == 0 ? -70f : 70f; // a shoulder through brick: roll only, aim untouched
                    _fp.Shake.FovPunch = Mathf.Max(_fp.Shake.FovPunch, 2.5f);
                }

            for (int i = _rackAt.Count - 1; i >= 0; i--)
            {
                if (now < _rackAt[i]) continue;
                GunAudio.Play(GunAudio.Rack, _rig.Camera.transform.position, 0.7f, Random.Range(0.97f, 1.03f), false);
                _rackAt.RemoveAt(i);
            }

            ReloadSounds();

            // Springs (critically-ish damped), camera roll and FOV punch decay.
            Weapon w = _player.State.Hand.Held;
            float wn = Feel[(int)w, 2], z = Feel[(int)w, 3];
            Spring(ref _kickPos, ref _kickPosVel, wn, z, dt);
            Spring(ref _kickRot, ref _kickRotVel, wn, z, dt);
            CameraShake sh = _fp.Shake;
            float roll = sh.Roll;
            Spring1(ref roll, ref _rollVel, 16f, 0.6f, dt);
            sh.Roll = roll;
            sh.FovPunch = Mathf.MoveTowards(sh.FovPunch, 0f, dt * 14f);
            sh.Offset = Vector3.Lerp(sh.Offset, Vector3.zero, 1f - Mathf.Exp(-30f * dt));
        }

        public void PostCamera(float dt)
        {
            PosePunch();
            PoseViewmodel(dt);

            // Own shots: tracers start at the gun's muzzle as drawn this frame.
            foreach (PendingLocal p in _pendingLocal)
            {
                GunModel g = _guns[(int)p.Shot.Weapon];
                Vector3 muzzle = g.Root.activeInHierarchy ? g.Muzzle.position : p.Shot.Origin.ToUnity() + p.Shot.Aim.ToUnity() * 0.6f;
                _flashes.Add(new Flash { At = muzzle, Fwd = p.Shot.Aim.ToUnity(), Born = Time.time, Size = p.Shot.Weapon == Weapon.Pump ? 0.22f : 0.13f, Local = true });
                bool pellets = p.Ends.Count > 1;
                foreach (Vector3 end in p.Ends)
                    AddTracer(muzzle, end, pellets ? 0.012f : 0.018f, pellets ? 0.05f : 0.07f);
            }
            _pendingLocal.Clear();

            DrawLines();
        }

        // ================================================================ Shots

        private void OnLocalShot(WeaponShot shot, float now)
        {
            _lastShotTime = now;
            _shownWeapon = shot.Weapon;
            int k = (int)shot.Weapon;
            // Kick: back and up, with a small random sideways twitch so it never looks canned.
            _kickPosVel += new Vector3(Random.Range(-0.1f, 0.1f), 0.12f, -1f) * Feel[k, 0];
            _kickRotVel += new Vector3(-Feel[k, 1], Random.Range(-0.15f, 0.15f) * Feel[k, 1], Random.Range(-0.2f, 0.2f) * Feel[k, 1]);
            _rollVel += (Random.value < 0.5f ? -1f : 1f) * Feel[k, 4];
            _fp.Shake.FovPunch = Mathf.Max(_fp.Shake.FovPunch, Feel[k, 5]);

            Vector3 eye = shot.Origin.ToUnity();
            GunAudio.Play(GunAudio.Shot(shot.Weapon), eye, 1f, Random.Range(0.96f, 1.04f), false);
            if (shot.Weapon == Weapon.Pump) _rackAt.Add(now + 0.32f);

            var ends = new List<Vector3>();
            TraceShot(shot, includeLocal: false, ends);
            _pendingLocal.Add(new PendingLocal { Shot = shot, Ends = ends });
        }

        private void OnRemoteShot(in CombatEvent e, float now)
        {
            WeaponShot shot = e.ToShot(0);
            Vector3 eye = shot.Origin.ToUnity();
            Vector3 fwd = shot.Aim.ToUnity();
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 muzzle = eye + fwd * 0.65f + right * 0.2f - Vector3.up * 0.18f;
            _flashes.Add(new Flash { At = muzzle, Fwd = fwd, Born = now, Size = shot.Weapon == Weapon.Pump ? 0.3f : 0.2f });
            GunAudio.Play(GunAudio.Shot(shot.Weapon), muzzle, 1f, Random.Range(0.96f, 1.04f), true);
            if (shot.Weapon == Weapon.Pump) StartCoroutineRack(muzzle, now);
            var ends = new List<Vector3>();
            TraceShot(shot, includeLocal: true, ends);
            foreach (Vector3 end in ends) AddTracer(muzzle, end, 0.02f, 0.08f);
        }

        private readonly List<KeyValuePair<float, Vector3>> _remoteRacks = new List<KeyValuePair<float, Vector3>>();
        private void StartCoroutineRack(Vector3 at, float now) => _remoteRacks.Add(new KeyValuePair<float, Vector3>(now + 0.32f, at));

        /// <summary>Rebuilds the pellets (same code as the server) and traces them on this client's world for effects.</summary>
        private void TraceShot(WeaponShot shot, bool includeLocal, List<Vector3> ends)
        {
            NetClient net = _session.Client;
            WeaponsConfig cfg = _mc.Weapons;
            WeaponStats st = cfg.Of(shot.Weapon);
            BuildTargets(net, includeLocal, shot.ShooterId);
            for (int i = 0; i < System.Math.Max(1, st.Pellets); i++)
            {
                Vec3d dir = WeaponRules.PelletDir(shot, st, i);
                BulletHit h = Hitscan.Trace(net.Grid, shot.Origin, dir, st.Range, _targets, shot.ShooterId, 0, true);
                Vector3 p = h.Point.ToUnity();
                ends.Add(p);
                if (h.Kind == HitKind.Terrain)
                {
                    float amount = st.Pellets > 1 ? 0.5f : 1f;
                    // Buildings draw their own chips, dust and marks in the wall's colour; the gun adds the sound.
                    var wall = Sculpt.Game.World.WallImpacts.OnBullet;
                    bool drawn = h.Brush > 0 && wall != null && wall(p, h.Normal.ToUnity(), h.Brush - 1, amount);
                    RockImpact(p, h.Normal.ToUnity(), amount, visuals: !drawn);
                }
                // Player hits are drawn when the server confirms them (Hit events).
            }
        }

        private void BuildTargets(NetClient net, bool includeLocal, int shooter)
        {
            _targets.Clear();
            net.GetRemoteIds(_ids);
            foreach (int id in _ids)
            {
                if (id == shooter || !net.TryGetRemote(id, out RemoteView v) || v.Dead) continue;
                double h = v.Crouched ? _mc.CrouchHeight : _mc.StandHeight;
                _targets.Add(HitTarget.Of(id, v.Team, v.Position, _mc.HalfWidth, h, _mc.Weapons));
            }
            if (includeLocal && !net.Predicted.Dead)
                _targets.Add(HitTarget.Of(net.PlayerId, net.Team, net.Predicted.Position, _mc.HalfWidth, PlayerMovement.HeightOf(net.Predicted, _mc), _mc.Weapons));
        }

        private void OnCombat(in CombatEvent e, float now)
        {
            NetClient net = _session.Client;
            int me = net.PlayerId;
            switch (e.Type)
            {
                case CombatEventType.Shot:
                    if (e.Attacker != me) OnRemoteShot(e, now);
                    break;
                case CombatEventType.Grenade:
                    if (Grenades != null) Grenades.OnServer(e);
                    break;
                case CombatEventType.Hit:
                {
                    Vector3 at = e.Point.ToUnity();
                    if (e.Victim != me) BloodPuff(at, e.Headshot, e.Pellets);
                    if (e.Attacker == me && e.Victim != me && e.Source != DamageSource.Throw) // throws have their own marker
                    {
                        HitTime = now;
                        LastHitDamage = e.Damage;
                        if (e.Headshot) HeadHitTime = now;
                        if (!e.Killed) GunAudio.Play(e.Headshot ? GunAudio.HitHead : GunAudio.HitTick, Vector3.zero, e.Headshot ? 0.7f : 0.55f, 1f, false);
                    }
                    if (e.Victim == me)
                    {
                        HurtTime = now;
                        LastHurt = e.Damage;
                        Vector3 from = at;
                        if (net.TryGetRemote(e.Attacker, out RemoteView v)) from = v.Position.ToUnity() + Vector3.up * 1.4f;
                        Arrows.Add(new DamageArrow { From = from, Time = now, Damage = e.Damage });
                        GunAudio.Play(GunAudio.Hurt, Vector3.zero, 0.8f, Random.Range(0.95f, 1.05f), false);
                        _rollVel += Random.Range(-60f, 60f);
                    }
                    else GunAudio.Play(GunAudio.Flesh, at, 0.6f, Random.Range(0.9f, 1.1f), true, 40f, 2f);
                    break;
                }
                case CombatEventType.Kill:
                    Feed.Add(new FeedLine { Time = now, Killer = e.Attacker, Victim = e.Victim, Source = e.Source, Head = e.HeadPellets > 0 });
                    if (Feed.Count > 6) Feed.RemoveAt(0);
                    if (e.Attacker == me && e.Victim != me)
                    {
                        KillMarkTime = now;
                        GunAudio.Play(GunAudio.Kill, Vector3.zero, 0.8f, 1f, false);
                    }
                    if (e.Victim == me) { KilledBy = e.Attacker; KilledWith = e.Source; }
                    break;
            }
        }

        // ================================================================ Effects

        private void AddTracer(Vector3 a, Vector3 b, float width, float life)
        {
            _tracers.Add(new Tracer { A = a, B = b, Born = Time.time, Life = life, Colour = TracerColour, Width = width });
        }

        private void RockImpact(Vector3 at, Vector3 normal, float amount, bool visuals = true)
        {
            if (!visuals)
            {
                if (Random.value < 0.6f) GunAudio.Play(GunAudio.Impact, at, 0.45f * amount + 0.1f, Random.Range(0.85f, 1.15f), true, 35f, 1.5f);
                return;
            }
            var ep = new ParticleSystem.EmitParams();
            Color rock = Sculpt.Game.World.ChunkMesher.RockHigh;
            int puffs = Mathf.Max(2, Mathf.RoundToInt(5 * amount));
            for (int i = 0; i < puffs; i++)
            {
                ep.position = at + normal * 0.04f;
                ep.velocity = (normal * Random.Range(0.6f, 2.2f) + Random.insideUnitSphere * 0.7f);
                ep.startSize = Random.Range(0.12f, 0.3f);
                ep.startLifetime = Random.Range(0.35f, 0.8f);
                float v = Random.Range(0.8f, 1.0f);
                ep.startColor = new Color(rock.r * v, rock.g * v, rock.b * v, 0.7f);
                _puffs.Emit(ep, 1);
            }
            int sparks = Mathf.Max(2, Mathf.RoundToInt(6 * amount));
            for (int i = 0; i < sparks; i++)
            {
                ep.position = at + normal * 0.02f;
                ep.velocity = (normal * Random.Range(2f, 5f) + Random.insideUnitSphere * 3f);
                ep.startSize = Random.Range(0.02f, 0.05f);
                ep.startLifetime = Random.Range(0.15f, 0.4f);
                ep.startColor = new Color(rock.r * 0.6f, rock.g * 0.55f, rock.b * 0.5f, 1f); // grit, dark chips
                _sparks.Emit(ep, 1);
            }
            if (Random.value < 0.6f) GunAudio.Play(GunAudio.Impact, at, 0.45f * amount + 0.1f, Random.Range(0.85f, 1.15f), true, 35f, 1.5f);
        }

        private void BloodPuff(Vector3 at, bool head, int pellets)
        {
            var ep = new ParticleSystem.EmitParams();
            int n = Mathf.Clamp(pellets * 2, 3, 14);
            for (int i = 0; i < n; i++)
            {
                ep.position = at;
                ep.velocity = Random.insideUnitSphere * 1.6f + Vector3.up * 0.4f;
                ep.startSize = Random.Range(0.1f, head ? 0.32f : 0.22f);
                ep.startLifetime = Random.Range(0.25f, 0.5f);
                ep.startColor = head ? new Color(0.85f, 0.12f, 0.08f, 0.85f) : new Color(0.55f, 0.06f, 0.05f, 0.75f);
                _puffs.Emit(ep, 1);
            }
        }

        private void DrawLines()
        {
            float now = Time.time;
            Vector3 cam = _rig.Camera.transform.position;
            for (int i = _remoteRacks.Count - 1; i >= 0; i--)
            {
                if (now < _remoteRacks[i].Key) continue;
                GunAudio.Play(GunAudio.Rack, _remoteRacks[i].Value, 0.5f, 1f, true, 40f, 2f);
                _remoteRacks.RemoveAt(i);
            }

            _lines.Begin(cam);
            _flashLines.Begin(cam);
            _tracers.RemoveAll(t => now - t.Born > t.Life);
            foreach (Tracer t in _tracers)
            {
                float k = 1f - (now - t.Born) / t.Life;
                // A bright streak running down the line: reads as a fast round, not a laser.
                float head = Mathf.Clamp01((now - t.Born) / t.Life * 1.6f);
                Vector3 a = Vector3.Lerp(t.A, t.B, Mathf.Max(0, head - 0.45f));
                Vector3 b = Vector3.Lerp(t.A, t.B, head);
                Color c = new Color(t.Colour.r, t.Colour.g, t.Colour.b, t.Colour.a * k);
                _lines.Add(a, b, c, t.Width);
            }
            _flashes.RemoveAll(f => now - f.Born > 0.045f);
            foreach (Flash f in _flashes)
            {
                LineBatch lb = f.Local ? _flashLines : _lines;
                float s = f.Size * Random.Range(0.85f, 1.15f);
                Vector3 up = Vector3.Cross(f.Fwd, Vector3.right).normalized;
                if (up.sqrMagnitude < 0.5f) up = Vector3.up;
                Vector3 side = Vector3.Cross(up, f.Fwd).normalized;
                lb.Add(f.At, f.At + f.Fwd * s * 1.8f, FlashOuter, s * 0.9f);
                lb.Add(f.At - f.Fwd * s * 0.1f, f.At + f.Fwd * s * 0.9f, FlashCore, s * 0.5f);
                lb.Add(f.At - side * s * 0.6f, f.At + side * s * 0.6f, FlashOuter, s * 0.25f);
                lb.Add(f.At - up * s * 0.6f, f.At + up * s * 0.6f, FlashOuter, s * 0.25f);
            }
            _lines.End();
            _flashLines.End();

            Arrows.RemoveAll(a => now - a.Time > 1.4f);
        }

        // ================================================================ Punch

        /// <summary>
        /// The punch from the predicted state: the gun dips aside, the left fist comes up during the wind-up, jabs out to
        /// full reach on the hit tick and pulls back. A whoosh on the press; a thud if it lands on a wall (players get the
        /// server's hit marker).
        /// </summary>
        private void PosePunch()
        {
            MotorState s = _player.State;
            NetClient net = _session.Client;
            PunchConfig pc = _mc.Punch;
            float t = net.Tick - 1 + (float)net.Alpha - s.PunchTick; // ticks since the punch started, smooth
            bool on = !s.Dead && s.PunchTick > int.MinValue / 2 && t >= 0 && t < pc.TotalTicks;
            Vector3 cam = _rig.Camera.transform.position;
            if (s.PunchTick != _lastPunch)
            {
                if (_lastPunch != int.MinValue && on && t < 4f)
                {
                    GunAudio.Play(Whoosh, cam, 0.55f, Random.Range(0.93f, 1.07f), false);
                    _punchHitDone = false;
                }
                _lastPunch = s.PunchTick;
            }
            if (!_punchHitDone && t >= pc.HitTicks)
            {
                _punchHitDone = true;
                Transform ct = _rig.Camera.transform;
                TerrainHit h = net.Grid.Raycast(ct.position.ToCore(), ct.forward.ToCore(), pc.Reach);
                if (h.Hit)
                {
                    Vector3 at = h.Point.ToUnity();
                    GunAudio.Play(GunAudio.Impact, at, 0.9f, Random.Range(0.6f, 0.7f), true, 40f, 2f);
                    _rollVel += Random.Range(-25f, 25f);
                }
            }
            if (_fist.activeSelf != on) _fist.SetActive(on);
            float hitT = pc.HitTicks, endT = pc.TotalTicks;
            // aside: in fast, hold, out over the last third.
            _punchAside = !on ? 0f : t < hitT ? Smooth(t / hitT) : t > endT * 0.66f ? 1f - Smooth((t - endT * 0.66f) / (endT * 0.34f)) : 1f;
            if (!on) return;
            Vector3 low = new Vector3(-0.2f, -0.32f, 0.12f), cocked = new Vector3(-0.15f, -0.13f, 0.2f), out_ = new Vector3(-0.05f, -0.05f, 0.58f);
            Vector3 p;
            Vector3 r;
            if (t < hitT * 0.6f) { float k = Smooth(t / (hitT * 0.6f)); p = Vector3.Lerp(low, cocked, k); r = new Vector3(20f, 25f, -10f); }
            else if (t < hitT) { float k = (t - hitT * 0.6f) / (hitT * 0.4f); k *= k; p = Vector3.Lerp(cocked, out_, k); r = Vector3.Lerp(new Vector3(20f, 25f, -10f), new Vector3(0f, 4f, -80f), k); }
            else { float k = Smooth((t - hitT) / (endT - hitT)); p = Vector3.Lerp(out_, low, k); r = Vector3.Lerp(new Vector3(0f, 4f, -80f), new Vector3(20f, 25f, -10f), k); }
            float size = Mathf.Clamp(_settings.ViewmodelSize, 0.7f, 1.2f);
            _fist.transform.localPosition = p * size;
            _fist.transform.localRotation = Quaternion.Euler(r);
            _fist.transform.localScale = Vector3.one * size;
        }

        private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }

        private static AudioClip _whoosh;
        private static AudioClip Whoosh => _whoosh ? _whoosh : _whoosh = Audio.Synth.Make("punch-whoosh", 0.2f, false, (t, n) =>
            Audio.Synth.Noise(n) * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.18f)) * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.18f)) * 0.5f);

        // ================================================================ Viewmodel

        private void PoseViewmodel(float dt)
        {
            MotorState s = _player.State;
            HandState hand = s.Hand;
            int tick = _session.Client.Tick;
            Weapon held = hand.Held;
            bool hasGun = hand.HasGun;
            Item item = hand.HeldItem;

            for (int i = 0; i < 4; i++)
            {
                bool show = hasGun && i == (int)held && !s.Dead;
                if (_guns[i].Root.activeSelf != show) _guns[i].Root.SetActive(show);
            }
            bool nade = item == Item.Grenade && !s.Dead;
            if (_grenade.activeSelf != nade) _grenade.SetActive(nade);
            if (s.Dead || item == Item.None) return; // empty hands: no viewmodel

            // Lowered: drawing from below, sculpt mode (gun out of the way), disarmed.
            float lowerTarget;
            switch (hand.Mode)
            {
                case HandMode.Drawing:
                    lowerTarget = Mathf.Clamp01((hand.ReadyTick - tick) / (float)Mathf.Max(1, _mc.Hand.Draw(item)));
                    break;
                case HandMode.SculptExit:
                    lowerTarget = Mathf.Clamp01((hand.ReadyTick - tick) / (float)Mathf.Max(1, _mc.Hand.Exit(item)));
                    break;
                case HandMode.SculptEnter:
                case HandMode.Sculpt:
                case HandMode.Disarmed:
                    lowerTarget = 1f;
                    break;
                default: lowerTarget = 0f; break;
            }
            // Raising tracks the real timer exactly (ready = up); lowering is quick but not a pop.
            _lower = lowerTarget < _lower && (hand.Mode == HandMode.Drawing || hand.Mode == HandMode.SculptExit)
                ? lowerTarget
                : Mathf.MoveTowards(_lower, lowerTarget, dt * 9f);

            _sprint = Mathf.MoveTowards(_sprint, s.Sprinting ? 1f : 0f, dt * 7f);
            _slideTilt = Mathf.MoveTowards(_slideTilt, s.Mode == MoveMode.Slide ? 1f : 0f, dt * 6f);
            float speed = _player.RenderSpeed;
            if (s.Mode == MoveMode.Ground) _bobPhase += dt * speed * 1.9f;
            float bobAmt = s.Mode == MoveMode.Ground ? Mathf.Clamp01(speed / 6f) : 0f;
            Vector2 look = _player.LookDelta;
            _sway = Vector2.Lerp(_sway, new Vector2(Mathf.Clamp(-look.x * 0.5f, -5f, 5f), Mathf.Clamp(-look.y * 0.5f, -5f, 5f)), 1f - Mathf.Exp(-14f * dt));

            float size = Mathf.Clamp(_settings.ViewmodelSize, 0.7f, 1.2f);
            Vector3 basePos = !hasGun ? new Vector3(0.15f, -0.14f, 0.3f) : held == Weapon.Pistol ? new Vector3(0.13f, -0.135f, 0.29f) : new Vector3(0.15f, -0.155f, 0.31f);
            Vector3 bob = new Vector3(Mathf.Cos(_bobPhase) * 0.006f, Mathf.Abs(Mathf.Sin(_bobPhase)) * 0.008f, 0) * bobAmt;
            float ease = _lower * _lower * (3 - 2 * _lower);
            Vector3 pos = basePos + _kickPos + bob + new Vector3(0, -0.3f, -0.06f) * ease + new Vector3(-0.04f, -0.035f, -0.03f) * _sprint
                          + new Vector3(0.05f, -0.09f, -0.04f) * _punchAside;
            Vector3 rot = _kickRot + new Vector3(32f * ease + 14f * _sprint + _sway.y, -24f * _sprint + _sway.x, 7f * _slideTilt - _sway.x * 0.4f)
                          + new Vector3(14f, 10f, -18f) * _punchAside;
            if (hand.Mode == HandMode.Disarmed) rot += new Vector3(Mathf.Sin(Time.time * 37f) * 3f, Mathf.Sin(Time.time * 29f) * 3f, 0);

            // Reload: the gun tips toward you and rolls (magazine guns), or dips to the loading port per shell (pump).
            bool shells = hasGun && _mc.Weapons.Of(held).ShellReload;
            _reload = Mathf.MoveTowards(_reload, s.Weapon.Reloading ? 1f : 0f, dt * 7f);
            _shellBob = Mathf.MoveTowards(_shellBob, 0f, dt * 6f);
            float re = _reload * _reload * (3 - 2 * _reload);
            if (shells)
            {
                pos += new Vector3(-0.02f, -0.03f, 0) * re + new Vector3(0, -0.015f, 0.01f) * _shellBob;
                rot += new Vector3(8f, 0, -28f) * re + new Vector3(-4f, 0, 0) * _shellBob;
            }
            else
            {
                pos += new Vector3(-0.03f, -0.05f, -0.02f) * re;
                rot += new Vector3(18f, 10f, 32f) * re;
            }

            _vmRoot.localPosition = pos * size;
            _vmRoot.localRotation = Quaternion.Euler(rot);
            _vmRoot.localScale = Vector3.one * size;

            // Cycling parts: pump grip racks back and forward; the pistol slide snaps back.
            if (!hasGun) return;
            GunModel g = _guns[(int)held];
            if (g.Cycler != null)
            {
                float t = Time.time - _lastShotTime;
                float back = 0;
                if (_shownWeapon == held)
                {
                    if (held == Weapon.Pump)
                    {
                        if (t > 0.3f && t < 0.45f) back = Mathf.SmoothStep(0, 1, (t - 0.3f) / 0.15f);
                        else if (t >= 0.45f && t < 0.62f) back = 1f - Mathf.SmoothStep(0, 1, (t - 0.45f) / 0.17f);
                        g.Cycler.localPosition = g.CyclerRest + new Vector3(0, 0, -0.1f * back);
                    }
                    else
                    {
                        if (t < 0.025f) back = t / 0.025f;
                        else if (t < 0.09f) back = 1f - (t - 0.025f) / 0.065f;
                        g.Cycler.localPosition = g.CyclerRest + new Vector3(0, 0, -0.035f * back);
                    }
                }
                else g.Cycler.localPosition = g.CyclerRest;
            }
        }

        private static void Spring(ref Vector3 x, ref Vector3 v, float w, float zeta, float dt)
        {
            // Semi-implicit Euler in small steps: stable at any frame rate.
            int n = Mathf.Max(1, Mathf.CeilToInt(dt / 0.004f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = -w * w * x - 2f * zeta * w * v;
                v += a * h;
                x += v * h;
            }
        }

        private static void Spring1(ref float x, ref float v, float w, float zeta, float dt)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(dt / 0.004f));
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                float a = -w * w * x - 2f * zeta * w * v;
                v += a * h;
                x += v * h;
            }
        }
    }
}
