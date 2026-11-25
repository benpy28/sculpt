using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Cameras;
using Sculpt.Game.Net;
using Sculpt.Game.World;
using UnityEngine;

namespace Sculpt.Game.Weapons
{
    /// <summary>
    /// Draws grenades, C4 charges and launcher rounds out in the world, and their blasts. Your own come from the
    /// predicted state (instant, exact); everyone else's fly here from the server's throw event with the same Core flight
    /// rules, snap to the server's stick point and stop at the server's blast or fizzle. Blasts are a short light flash,
    /// dust and grit, a generated boom and a small camera roll if you are close. A stuck C4 blinks red and beeps once a
    /// second, faster over its last two seconds, so anyone near it can read it. Cosmetic only: damage, push and broken
    /// tiles come from Core.
    /// </summary>
    public sealed class GrenadeView : MonoBehaviour
    {
        private struct Remote
        {
            public int Player;
            public byte Seq;
            public ThrowKind Kind;
            public Vec3d Pos, Vel;
            public double Travel;
            public float Clock;
            public float Age;
            public bool Landed;
            public float StuckAt;
            public float Beep;
            public Transform View;
        }

        private struct Own
        {
            public ThrowKind Kind;
            public Transform View;
            public float NextBeep;
        }

        private NetSession _session;
        private CameraShake _shake;
        private Material _mat;
        private TileDebris _debris;
        private readonly Own[] _own = new Own[GrenadeRules.Flights];
        private readonly List<Remote> _remote = new List<Remote>();
        private readonly Stack<Transform>[] _pool = { new Stack<Transform>(), new Stack<Transform>(), new Stack<Transform>() };
        private Light _flash;
        private float _flashAge = 10f, _flashPeak = 14f;
        private static AudioClip _boom, _toss, _thump, _stick, _beep, _dud, _c4Boom;

        /// <summary>Your own launcher fired this frame (the weapon view kicks the viewmodel).</summary>
        public System.Action<ThrowKind> LocalThrown;

        public void Init(NetSession session, CameraShake shake)
        {
            _session = session;
            _shake = shake;
            Shader s = Shader.Find("SCULPT/Viewmodel");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            _mat = new Material(s) { name = "Thrown grenades" };
            _debris = new GameObject("Blast Debris").AddComponent<TileDebris>();
            _debris.transform.SetParent(transform, false);
            for (int i = 0; i < _own.Length; i++) _own[i] = new Own { Kind = ThrowKind.Grenade, View = Take(ThrowKind.Grenade) };
            var lg = new GameObject("Blast Flash");
            lg.transform.SetParent(transform, false);
            _flash = lg.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.72f, 0.4f);
            _flash.range = 9f;
            _flash.intensity = 0f;
            _flash.enabled = false;
        }

        private Transform Take(ThrowKind k)
        {
            Stack<Transform> pool = _pool[(int)k];
            Transform t;
            if (pool.Count > 0) t = pool.Pop();
            else
            {
                GameObject go = k == ThrowKind.C4 ? GunModels.BuildC4(_mat) : k == ThrowKind.Launcher ? GunModels.BuildRound(_mat) : GunModels.BuildGrenade(_mat);
                t = go.transform;
                t.localScale = Vector3.one * (k == ThrowKind.Grenade ? 1.3f : 1.15f);
            }
            t.SetParent(transform, false);
            t.gameObject.SetActive(false);
            return t;
        }

        private void Give(ThrowKind k, Transform t)
        {
            t.gameObject.SetActive(false);
            _pool[(int)k].Push(t);
        }

        private static ThrowKind KindOf(in CombatEvent e) => NetServer.KindOf(e.Source);

        /// <summary>A grenade event from the server (never your own: the server leaves those out of your copy).</summary>
        public void OnServer(in CombatEvent e)
        {
            NetClient net = _session.Client;
            if (e.Attacker == net.PlayerId) return;
            ThrowKind kind = KindOf(e);
            if (e.Nade == GrenadeEventType.Thrown)
            {
                _remote.Add(new Remote { Player = e.Attacker, Seq = e.NadeSeq, Kind = kind, Pos = e.Origin, Vel = e.Aim, View = Take(kind) });
                Launched(kind, e.Origin.ToUnity(), true);
                return;
            }
            int i = -1;
            for (int k = 0; k < _remote.Count && i < 0; k++) if (_remote[k].Player == e.Attacker && _remote[k].Seq == e.NadeSeq) i = k;
            if (e.Nade == GrenadeEventType.Stuck)
            {
                if (i < 0) // joined late or the throw was missed: show it from here on
                {
                    _remote.Add(new Remote { Player = e.Attacker, Seq = e.NadeSeq, Kind = kind, View = Take(kind) });
                    i = _remote.Count - 1;
                }
                Remote r = _remote[i];
                bool first = !r.Landed || r.StuckAt <= 0;
                r.Pos = e.Point;
                r.Vel = Vec3d.Zero;
                r.Landed = true;
                if (r.StuckAt <= 0) r.StuckAt = Time.time;
                r.View.rotation = Lie(net.Grid, e.Point);
                _remote[i] = r;
                if (first) Stick(e.Point.ToUnity());
                return;
            }
            if (i >= 0)
            {
                Give(_remote[i].Kind, _remote[i].View);
                _remote.RemoveAt(i);
            }
            if (e.Nade == GrenadeEventType.Exploded) Blast(kind, e.Point.ToUnity());
            else if (kind == ThrowKind.Launcher) Dud(e.Point.ToUnity());
        }

        private void LateUpdate()
        {
            NetClient net = _session.Client;
            if (net == null) return;

            // Your own: predicted events and the predicted flights.
            foreach (GrenadeEvent g in net.LocalGrenades)
            {
                Vector3 at = g.Position.ToUnity();
                switch (g.Type)
                {
                    case GrenadeEventType.Thrown: Launched(g.Kind, at, false); LocalThrown?.Invoke(g.Kind); break;
                    case GrenadeEventType.Exploded: Blast(g.Kind, at); break;
                    case GrenadeEventType.Stuck: Stick(at); break;
                    case GrenadeEventType.Fizzled: if (g.Kind == ThrowKind.Launcher) Dud(at); break;
                }
            }
            net.LocalGrenades.Clear();
            float alpha = (float)net.Alpha;
            MotorState prev = net.PreviousPredicted, cur = net.Predicted;
            float tickNow = net.Tick - 1 + alpha;
            for (int i = 0; i < _own.Length; i++)
                ShowOwn(ref _own[i], GrenadeRules.FlightAt(prev, i), GrenadeRules.FlightAt(cur, i), alpha, tickNow, net);

            // Others: fly them here with the Core flight numbers; stop on terrain and wait for the server.
            double dt = net.Movement.Dt;
            float now = Time.deltaTime;
            for (int i = _remote.Count - 1; i >= 0; i--)
            {
                Remote r = _remote[i];
                GrenadeConfig c = net.Movement.Items.Of(r.Kind);
                r.Clock += now;
                r.Age += now;
                while (!r.Landed && r.Clock >= dt)
                {
                    r.Clock -= (float)dt;
                    if (GrenadeRules.ObserveStep(net.Grid, c, dt, ref r.Pos, ref r.Vel, ref r.Travel)) r.Landed = true;
                }
                float limit = r.Kind == ThrowKind.C4 ? c.StuckFuseTicks / 60f + c.MaxFuseTicks / 60f + 2f : 4f;
                if (r.Age > limit) { Give(r.Kind, r.View); _remote.RemoveAt(i); continue; } // the server's word never came
                r.View.gameObject.SetActive(true);
                r.View.position = r.Pos.ToUnity();
                if (!r.Landed || r.StuckAt <= 0) r.View.rotation = Spin(r.Kind, r.Vel, r.Seq);
                if (r.Kind == ThrowKind.C4 && r.StuckAt > 0)
                {
                    float left = c.StuckFuseTicks / 60f - (Time.time - r.StuckAt);
                    BlinkAndBeep(r.View, left, ref r.Beep);
                }
                _remote[i] = r;
            }

            if (_flash.enabled)
            {
                _flashAge += Time.deltaTime;
                float k = Mathf.Clamp01(1f - _flashAge / 0.18f);
                _flash.intensity = _flashPeak * k * k;
                if (k <= 0) _flash.enabled = false;
            }
        }

        private void ShowOwn(ref Own o, in GrenadeFlight a, in GrenadeFlight b, float alpha, float tickNow, NetClient net)
        {
            if (!b.Active) { o.View.gameObject.SetActive(false); return; }
            if (o.Kind != b.Kind)
            {
                Give(o.Kind, o.View);
                o.Kind = b.Kind;
                o.View = Take(b.Kind);
            }
            o.View.gameObject.SetActive(true);
            Vector3 p = b.Position.ToUnity();
            if (a.Active && a.Seq == b.Seq && !b.Stuck) p = Vector3.Lerp(a.Position.ToUnity(), p, alpha);
            o.View.position = p;
            o.View.rotation = b.Stuck ? Quaternion.FromToRotation(Vector3.up, b.Normal.ToUnity()) : Spin(b.Kind, b.Velocity, b.Seq);
            if (b.Kind == ThrowKind.C4 && b.Landed)
            {
                float left = (net.Movement.Items.C4.StuckFuseTicks - (tickNow - b.StuckTick)) / 60f;
                BlinkAndBeep(o.View, left, ref o.NextBeep);
            }
        }

        private static Quaternion Spin(ThrowKind k, Vec3d vel, byte seq)
        {
            if (k == ThrowKind.Launcher && vel.LengthSquared > 1e-6) return Quaternion.LookRotation(vel.ToUnity());
            float rate = k == ThrowKind.C4 ? 0.35f : 1f; // the C4 brick tumbles slowly: it reads as a heavy lob
            return Quaternion.Euler(Time.time * 540f * rate, Time.time * 200f * rate + seq * 40f, 0);
        }

        /// <summary>Lays a remote charge flat against the nearest surface around its stick point.</summary>
        private static Quaternion Lie(ITerrain g, Vec3d at)
        {
            Vec3d best = new Vec3d(0, 1, 0);
            double bestD = double.MaxValue;
            Vec3d[] dirs = { new Vec3d(0, -1, 0), new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), new Vec3d(-1, 0, 0), new Vec3d(0, 0, 1), new Vec3d(0, 0, -1) };
            foreach (Vec3d d in dirs)
            {
                TerrainHit h = g.Raycast(at, d, 0.3);
                if (h.Hit && h.Distance < bestD) { bestD = h.Distance; best = h.Normal; }
            }
            return Quaternion.FromToRotation(Vector3.up, best.ToUnity());
        }

        /// <summary>
        /// The C4 tell: the red light blinks once a second (four times a second over the last two) with a short beep you
        /// hear within about 18 m. <paramref name="state"/> holds the time the next blink starts (per charge).
        /// </summary>
        private void BlinkAndBeep(Transform view, float secondsLeft, ref float state)
        {
            Transform light = view.Find("Light");
            float period = secondsLeft < 2f ? 0.25f : 1f;
            float phase = Mathf.Repeat(-secondsLeft, period) / period;
            bool on = phase < 0.35f;
            if (light != null && light.gameObject.activeSelf != on) light.gameObject.SetActive(on);
            int blink = Mathf.FloorToInt(-secondsLeft / period);
            if (blink != (int)state)
            {
                state = blink;
                Synth.Play(Beep, view.position, secondsLeft < 2f ? 0.5f : 0.35f, secondsLeft < 2f ? 1.25f : 1f, true, 2f, 18f);
            }
        }

        private void Launched(ThrowKind k, Vector3 at, bool remote)
        {
            if (k == ThrowKind.Launcher) Synth.Play(Thump, at, remote ? 1f : 0.9f, Random.Range(0.95f, 1.04f), remote, 3f, 80f, true, 40);
            else Synth.Play(Toss_, at, 0.45f, Random.Range(0.95f, 1.08f) * (k == ThrowKind.C4 ? 0.8f : 1f), true, 2f, 30f);
        }

        private void Stick(Vector3 at) => Synth.Play(StickClip, at, 0.6f, Random.Range(0.95f, 1.05f), true, 2f, 25f);

        /// <summary>A launcher round that hit before it armed: a dull knock and a little smoke, nothing else.</summary>
        private void Dud(Vector3 at)
        {
            Synth.Play(DudClip, at, 0.7f, Random.Range(0.9f, 1.1f), true, 2f, 35f);
            var box = new Aabb(new Vec3d(at.x - 0.15, at.y - 0.1, at.z - 0.15), new Vec3d(at.x + 0.15, at.y + 0.15, at.z + 0.15));
            _debris.Burst(box, Dir.North, new Color(0.42f, 0.42f, 0.40f), 4, true);
        }

        private void Blast(ThrowKind kind, Vector3 at)
        {
            _flash.transform.position = at;
            _flash.enabled = true;
            _flashAge = 0;
            _flashPeak = kind == ThrowKind.C4 ? 20f : 14f;
            float s = kind == ThrowKind.C4 ? 0.8f : kind == ThrowKind.Launcher ? 0.55f : 0.6f;
            var box = new Aabb(new Vec3d(at.x - s, at.y - 0.4, at.z - s), new Vec3d(at.x + s, at.y + s, at.z + s));
            int n = kind == ThrowKind.C4 ? 20 : 14;
            _debris.Burst(box, Dir.North, new Color(0.30f, 0.28f, 0.25f), n, true);
            _debris.Burst(box, Dir.East, new Color(0.36f, 0.33f, 0.30f), n * 3 / 4, true);
            if (kind == ThrowKind.C4) Synth.Play(C4Boom, at, 1f, Random.Range(0.94f, 1.02f), true, 7f, 190f, true, 30);
            else Synth.Play(Boom, at, 1f, Random.Range(0.94f, 1.04f) * (kind == ThrowKind.Launcher ? 1.06f : 1f), true, 6f, 160f, true, 32);

            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null || _shake == null) return;
            float d = Vector3.Distance(cam.transform.position, at);
            float reach = kind == ThrowKind.C4 ? 18f : 14f;
            float k = Mathf.Clamp01(1f - d / reach);
            if (k <= 0) return;
            _shake.Roll += Random.Range(-1f, 1f) * (kind == ThrowKind.C4 ? 6f : 4f) * k; // roll only: aim is never moved by effects
            _shake.FovPunch += (kind == ThrowKind.C4 ? 4f : 3f) * k;
        }

        // ---------------------------------------------------------------- Sounds (generated)

        private static AudioClip Toss_ => _toss ? _toss : _toss = Synth.Make("grenade-toss", 0.22f, false, (t, n) =>
        {
            float swish = Synth.Noise(n) * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.2f)) * 0.35f;
            float tick = t < 0.012f ? Synth.Noise(n + 77) * (1f - t / 0.012f) * 0.6f : 0f; // spoon flick
            return swish + tick;
        });

        /// <summary>Launcher shot: a hollow, low "thoonk" (a big tube, not a gunshot), with a short metal clank.</summary>
        private static AudioClip Thump => _thump ? _thump : _thump = MakeThump();

        private static AudioClip MakeThump()
        {
            float phase = 0, lp = 0;
            return Synth.Make("launcher-thump", 0.5f, false, (t, n) =>
            {
                float f = 70f + 120f * Mathf.Exp(-t * 22f);
                phase += 2 * Mathf.PI * f / Synth.Rate;
                float body = Mathf.Sin(phase) * Mathf.Exp(-t * 9f) * 1.1f;
                lp += (Synth.Noise(n) - lp) * 0.12f;
                float pop = lp * 3f * Mathf.Exp(-t * 45f);
                float clank = Mathf.Sin(2 * Mathf.PI * 1850f * t) * Mathf.Exp(-t * 30f) * 0.12f;
                return Synth.Soft(body + pop + clank);
            });
        }

        /// <summary>C4 sticking: a wet slap and a plastic click.</summary>
        private static AudioClip StickClip => _stick ? _stick : _stick = Synth.Make("c4-stick", 0.16f, false, (t, n) =>
        {
            float slap = Synth.Noise(n) * Mathf.Exp(-t * 60f) * 0.7f;
            float click = t > 0.06f && t < 0.075f ? Synth.Noise(n + 13) * 0.6f : 0f;
            return slap + click;
        });

        private static AudioClip Beep => _beep ? _beep : _beep = Synth.Make("c4-beep", 0.07f, false, (t, n) =>
            Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 2400f * t)) * 0.25f * Mathf.Min(1f, t * 400f));

        private static AudioClip DudClip => _dud ? _dud : _dud = Synth.Make("launcher-dud", 0.25f, false, (t, n) =>
            Synth.Soft(Mathf.Sin(2 * Mathf.PI * (180f - t * 300f) * t) * Mathf.Exp(-t * 25f) + Synth.Noise(n) * Mathf.Exp(-t * 50f) * 0.4f));

        /// <summary>Generated boom: a hard crack, a deep pitch-dropping body and a long rumbling tail.</summary>
        private static AudioClip Boom => _boom ? _boom : _boom = MakeBoom("grenade-boom", 1f);

        /// <summary>C4: a sharper, heavier crack and more rubble in the tail (it is breaking walls).</summary>
        private static AudioClip C4Boom => _c4Boom ? _c4Boom : _c4Boom = MakeBoom("c4-boom", 1.5f);

        private static AudioClip MakeBoom(string name, float heavy)
        {
            float phase = 0, lp = 0, lp2 = 0;
            return Synth.Make(name, 1.8f, false, (t, n) =>
            {
                float crack = Synth.Noise(n) * Mathf.Exp(-t * 38f * heavy) * 0.9f;
                float f = 34f + 90f * Mathf.Exp(-t * 9f);
                phase += 2 * Mathf.PI * f / 44100f;
                float body = Mathf.Sin(phase) * Mathf.Exp(-t * 3.2f) * 1.1f;
                lp += (Synth.Noise(n + 4111) - lp) * 0.03f;
                lp2 += (lp - lp2) * 0.08f;
                float rumble = lp2 * 9f * Mathf.Exp(-t * 1.8f) * Mathf.Min(1f, t * 60f);
                float grit = t > 0.08f ? Synth.Noise(n + 99) * 0.12f * heavy * Mathf.Exp(-(t - 0.08f) * 5f) * (Synth.Noise(n / 300 + 5) > 0.5f ? 1f : 0.2f) : 0f;
                return Synth.Soft(crack + body + rumble + grit);
            });
        }
    }
}
