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
    /// Draws grenades in the air and their blasts. Your own come from the predicted state (instant, exact); everyone
    /// else's fly here from the server's throw event with the same flight rules and stop at the server's blast. Blasts
    /// are a short light flash, a burst of dust and grit, a generated boom and a small camera roll if you are close.
    /// Cosmetic only: damage, push and broken tiles come from Core.
    /// </summary>
    public sealed class GrenadeView : MonoBehaviour
    {
        private struct Remote
        {
            public int Player;
            public byte Seq;
            public Vec3d Pos, Vel;
            public float Clock;
            public bool Landed;
            public Transform View;
        }

        private NetSession _session;
        private CameraShake _shake;
        private Material _mat;
        private TileDebris _debris;
        private Transform _own0, _own1;
        private readonly List<Remote> _remote = new List<Remote>();
        private readonly Stack<Transform> _pool = new Stack<Transform>();
        private Light _flash;
        private float _flashAge = 10f;
        private static AudioClip _boom, _toss;

        public void Init(NetSession session, CameraShake shake)
        {
            _session = session;
            _shake = shake;
            Shader s = Shader.Find("SCULPT/Viewmodel");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            _mat = new Material(s) { name = "Thrown grenades" };
            _debris = new GameObject("Blast Debris").AddComponent<TileDebris>();
            _debris.transform.SetParent(transform, false);
            _own0 = Take();
            _own1 = Take();
            var lg = new GameObject("Blast Flash");
            lg.transform.SetParent(transform, false);
            _flash = lg.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.72f, 0.4f);
            _flash.range = 9f;
            _flash.intensity = 0f;
            _flash.enabled = false;
        }

        private Transform Take()
        {
            Transform t = _pool.Count > 0 ? _pool.Pop() : GunModels.BuildGrenade(_mat).transform;
            t.SetParent(transform, false);
            t.localScale = Vector3.one * 1.3f;
            t.gameObject.SetActive(false);
            return t;
        }

        private void Give(Transform t)
        {
            t.gameObject.SetActive(false);
            _pool.Push(t);
        }

        /// <summary>A grenade event from the server (never your own: the server leaves those out of your copy).</summary>
        public void OnServer(in CombatEvent e)
        {
            NetClient net = _session.Client;
            if (e.Attacker == net.PlayerId) return;
            if (e.Nade == GrenadeEventType.Thrown)
            {
                _remote.Add(new Remote { Player = e.Attacker, Seq = e.NadeSeq, Pos = e.Origin, Vel = e.Aim, View = Take() });
                Toss(e.Origin.ToUnity());
                return;
            }
            for (int i = _remote.Count - 1; i >= 0; i--)
            {
                if (_remote[i].Player != e.Attacker || _remote[i].Seq != e.NadeSeq) continue;
                Give(_remote[i].View);
                _remote.RemoveAt(i);
            }
            Blast(e.Point.ToUnity());
        }

        private void LateUpdate()
        {
            NetClient net = _session.Client;
            if (net == null) return;

            // Your own: predicted events (throw whoosh, blast) and the predicted flights.
            foreach (GrenadeEvent g in net.LocalGrenades)
            {
                if (g.Type == GrenadeEventType.Thrown) Toss(g.Position.ToUnity());
                else Blast(g.Position.ToUnity());
            }
            net.LocalGrenades.Clear();
            float alpha = (float)net.Alpha;
            MotorState prev = net.PreviousPredicted, cur = net.Predicted;
            ShowOwn(_own0, prev.Nade0, cur.Nade0, alpha);
            ShowOwn(_own1, prev.Nade1, cur.Nade1, alpha);

            // Others: fly them here with the Core flight numbers; stop on terrain and wait for the server's blast.
            GrenadeConfig c = net.Movement.Items.Grenade;
            double dt = net.Movement.Dt;
            float now = Time.deltaTime;
            for (int i = _remote.Count - 1; i >= 0; i--)
            {
                Remote r = _remote[i];
                r.Clock += now;
                while (!r.Landed && r.Clock >= dt)
                {
                    r.Clock -= (float)dt;
                    Vec3d v = r.Vel + new Vec3d(0, -c.Gravity * dt, 0);
                    Vec3d step = v * dt;
                    double len = step.Length;
                    if (len > 1e-9)
                    {
                        TerrainHit h = net.Grid.Raycast(r.Pos, step * (1.0 / len), len);
                        if (h.Hit) { r.Pos = h.Point + h.Normal * 0.05; r.Landed = true; }
                        else r.Pos += step;
                    }
                    r.Vel = v;
                }
                if (r.Clock > 4f) { Give(r.View); _remote.RemoveAt(i); continue; } // the blast never came (lost join state)
                r.View.gameObject.SetActive(true);
                r.View.position = r.Pos.ToUnity();
                r.View.rotation = Quaternion.Euler(Time.time * 540f, Time.time * 200f + r.Seq * 40f, 0);
                _remote[i] = r;
            }

            if (_flash.enabled)
            {
                _flashAge += Time.deltaTime;
                float k = Mathf.Clamp01(1f - _flashAge / 0.18f);
                _flash.intensity = 14f * k * k;
                if (k <= 0) _flash.enabled = false;
            }
        }

        private static void ShowOwn(Transform t, in GrenadeFlight a, in GrenadeFlight b, float alpha)
        {
            if (!b.Active) { t.gameObject.SetActive(false); return; }
            t.gameObject.SetActive(true);
            Vector3 p = b.Position.ToUnity();
            if (a.Active && a.Seq == b.Seq) p = Vector3.Lerp(a.Position.ToUnity(), p, alpha);
            t.position = p;
            t.rotation = Quaternion.Euler(Time.time * 540f, Time.time * 200f + b.Seq * 40f, 0);
        }

        private void Blast(Vector3 at)
        {
            _flash.transform.position = at;
            _flash.enabled = true;
            _flashAge = 0;
            var box = new Aabb(new Vec3d(at.x - 0.6, at.y - 0.4, at.z - 0.6), new Vec3d(at.x + 0.6, at.y + 0.6, at.z + 0.6));
            _debris.Burst(box, Dir.North, new Color(0.30f, 0.28f, 0.25f), 14, true);
            _debris.Burst(box, Dir.East, new Color(0.36f, 0.33f, 0.30f), 10, true);
            Synth.Play(Boom, at, 1f, Random.Range(0.94f, 1.04f), true, 6f, 160f, true, 32);

            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null || _shake == null) return;
            float d = Vector3.Distance(cam.transform.position, at);
            float k = Mathf.Clamp01(1f - d / 14f);
            if (k <= 0) return;
            _shake.Roll += Random.Range(-1f, 1f) * 4f * k; // roll only: aim is never moved by effects
            _shake.FovPunch += 3f * k;
        }

        private static void Toss(Vector3 at) => Synth.Play(Toss_, at, 0.45f, Random.Range(0.95f, 1.08f), true, 2f, 30f);

        private static AudioClip Toss_ => _toss ? _toss : _toss = Synth.Make("grenade-toss", 0.22f, false, (t, n) =>
        {
            float swish = Synth.Noise(n) * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.2f)) * 0.35f;
            float tick = t < 0.012f ? Synth.Noise(n + 77) * (1f - t / 0.012f) * 0.6f : 0f; // spoon flick
            return swish + tick;
        });

        /// <summary>Generated boom: a hard crack, a deep pitch-dropping body and a long rumbling tail.</summary>
        private static AudioClip Boom => _boom ? _boom : _boom = MakeBoom();

        private static AudioClip MakeBoom()
        {
            float phase = 0, lp = 0, lp2 = 0;
            return Synth.Make("grenade-boom", 1.8f, false, (t, n) =>
            {
                float crack = Synth.Noise(n) * Mathf.Exp(-t * 38f) * 0.9f;
                float f = 34f + 90f * Mathf.Exp(-t * 9f);
                phase += 2 * Mathf.PI * f / 44100f;
                float body = Mathf.Sin(phase) * Mathf.Exp(-t * 3.2f) * 1.1f;
                lp += (Synth.Noise(n + 4111) - lp) * 0.03f;
                lp2 += (lp - lp2) * 0.08f;
                float rumble = lp2 * 9f * Mathf.Exp(-t * 1.8f) * Mathf.Min(1f, t * 60f);
                float grit = t > 0.08f ? Synth.Noise(n + 99) * 0.12f * Mathf.Exp(-(t - 0.08f) * 5f) * (Synth.Noise(n / 300 + 5) > 0.5f ? 1f : 0.2f) : 0f;
                return Synth.Soft(crack + body + rumble + grit);
            });
        }
    }
}
