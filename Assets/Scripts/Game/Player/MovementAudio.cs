using Sculpt.Core;
using Sculpt.Game.Audio;
using UnityEngine;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// Your own movement sounds, generated in code: gritty footsteps paced by stride (quiet when crouch-walking), a push-off
    /// on jumps, a landing thud that scales with the drop, a looping stone scrape while sliding (louder and brighter with
    /// speed), vault and mantle grabs, and a distinct success sound for a slide-land (a rising whoosh that snaps shut),
    /// so the timing is learnable by ear. Reads the predicted movement events of the frame.
    /// </summary>
    public sealed class MovementAudio : MonoBehaviour
    {
        private LocalPlayer _player;
        private AudioSource _slide;
        private Vector3 _lastFeet;
        private float _stride;
        private int _stepIndex;
        private static AudioClip[] _steps;
        private static AudioClip _jump, _land, _slideLoop, _slideLand, _grab, _crash;

        /// <summary>Charging through a wall: a heavy shoulder thump, then brick cracking apart and rubble spilling.</summary>
        private static AudioClip Crash => _crash ? _crash : _crash = MakeCrash();

        private static AudioClip MakeCrash()
        {
            float phase = 0, lp = 0;
            return Synth.Make("charge-crash", 0.9f, false, (t, n) =>
            {
                float f = 55f + 70f * Mathf.Exp(-t * 18f);
                phase += 2 * Mathf.PI * f / Synth.Rate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t * 11f) * 0.9f;
                float crack = Synth.Noise(n) * Mathf.Exp(-t * 30f) * 0.7f;
                lp += (Synth.Noise(n + 313) - lp) * 0.12f;
                // Rubble: bursts of filtered noise that thin out.
                float gate = Synth.Noise(n / 900 + 17) > 0.2f ? 1f : 0.15f;
                float rubble = lp * 2.2f * gate * Mathf.Exp(-t * 3.5f) * Mathf.Min(1f, t * 25f);
                return Synth.Soft(thump + crack + rubble);
            });
        }

        public void Init(LocalPlayer player)
        {
            _player = player;
            _slide = Synth.Loop("Slide Scrape", transform, SlideLoop, 1f, 10f, false);
        }

        private void LateUpdate()
        {
            if (_player == null || !_player.Net.Joined) return;
            MotorState s = _player.State;
            Vector3 feet = _player.RenderFeet;
            Vector3 d = feet - _lastFeet;
            _lastFeet = feet;
            d.y = 0;
            float moved = d.magnitude;
            if (moved > 2f) moved = 0; // respawn or a big correction

            foreach (MoveEvent e in _player.Events)
            {
                switch (e.Type)
                {
                    case MoveEventType.Jumped:
                    case MoveEventType.SlideJump:
                        Synth.Play(Jump, feet, 0.4f, Random.Range(0.95f, 1.05f), false);
                        break;
                    case MoveEventType.Landed:
                    {
                        float levels = (float)e.Amount;
                        float vol = Mathf.Clamp(0.3f + 0.22f * levels, 0.3f, 1f);
                        Synth.Play(Land, feet, vol, Mathf.Clamp(1.1f - 0.05f * levels, 0.8f, 1.1f), false);
                        _stride = 0;
                        break;
                    }
                    case MoveEventType.SlideLandSuccess:
                        Synth.Play(SlideLand, feet, 0.7f, 1f, false);
                        break;
                    case MoveEventType.ChargedThrough:
                        Synth.Play(Crash, feet + Vector3.up, 1f, Random.Range(0.92f, 1.04f), false);
                        break;
                    case MoveEventType.Vaulted:
                    case MoveEventType.MantleStarted:
                        Synth.Play(Grab, feet, 0.5f, Random.Range(0.95f, 1.05f), false);
                        break;
                }
            }

            // Footsteps: one per stride on the ground. Crouch-walking is quiet but not silent.
            bool walking = s.Mode == MoveMode.Ground && s.Grounded;
            float speed = _player.RenderSpeed;
            if (walking && speed > 0.8f)
            {
                _stride += moved;
                float strideLen = s.Crouched ? 0.9f : Mathf.Lerp(1.5f, 2.3f, Mathf.InverseLerp(3f, 10f, speed));
                if (_stride >= strideLen)
                {
                    _stride -= strideLen;
                    float vol = s.Crouched ? 0.18f : Mathf.Lerp(0.35f, 0.6f, Mathf.InverseLerp(3f, 10f, speed));
                    _stepIndex = (_stepIndex + 1 + Random.Range(0, 3)) % Steps.Length;
                    Synth.Play(Steps[_stepIndex], feet, vol, Random.Range(0.93f, 1.07f), false);
                }
            }
            else if (!walking) _stride = 0.6f; // first step comes soon after landing

            // Slide scrape.
            bool sliding = s.Mode == MoveMode.Slide;
            // A carve (turning harder than the free steer, which costs speed) digs in: louder and brighter, so you can
            // hear when a turn is costing you.
            bool carving = sliding && s.Carving;
            float target = sliding ? Mathf.Clamp01(speed / 14f) * (carving ? 0.75f : 0.55f) : 0f;
            _slide.volume = Mathf.MoveTowards(_slide.volume, target, Time.unscaledDeltaTime * (sliding ? 8f : 5f));
            _slide.pitch = Mathf.MoveTowards(_slide.pitch, 0.75f + 0.5f * Mathf.Clamp01(speed / 18f) + (carving ? 0.18f : 0f), Time.unscaledDeltaTime * 3f);
        }

        // ---------------------------------------------------------------- Clips

        private static AudioClip[] Steps
        {
            get
            {
                if (_steps != null) return _steps;
                _steps = new AudioClip[4];
                for (int k = 0; k < 4; k++)
                {
                    uint salt = 0x1000u + (uint)k * 7919u;
                    var grit = new Synth.Band(700f, 4500f);
                    var body = new Synth.LowPass(300f);
                    float f0 = 85f + 12f * k;
                    _steps[k] = Synth.Make("step-" + k, 0.16f, false, (t, i) =>
                    {
                        float heel = 0.8f * body.Step(Synth.Noise(i, salt)) * 3f * Mathf.Exp(-t * 45f) + 0.35f * Mathf.Sin(2 * Mathf.PI * f0 * t) * Mathf.Exp(-t * 50f);
                        float scuff = 0.3f * grit.Step(Synth.Noise(i, salt ^ 0xffu)) * Mathf.Exp(-Mathf.Max(0, t - 0.015f) * 30f) * Mathf.Min(1f, t * 300f);
                        return Synth.Soft(heel + scuff);
                    });
                }
                return _steps;
            }
        }

        private static AudioClip Jump => _jump ? _jump : _jump = MakeJump();

        private static AudioClip MakeJump()
        {
            var grit = new Synth.Band(500f, 3000f);
            return Synth.Make("move-jump", 0.14f, false, (t, i) =>
                0.45f * grit.Step(Synth.Noise(i, 0x777u)) * Mathf.Exp(-t * 35f) + 0.3f * Mathf.Sin(2 * Mathf.PI * 110 * t) * Mathf.Exp(-t * 40f));
        }

        private static AudioClip Land => _land ? _land : _land = MakeLand();

        private static AudioClip MakeLand()
        {
            var lp = new Synth.LowPass(250f);
            var grit = new Synth.Band(600f, 4000f);
            float ph = 0;
            return Synth.Make("move-land", 0.3f, false, (t, i) =>
            {
                ph += 2 * Mathf.PI * (55f + 45f * Mathf.Exp(-t * 30f)) / Synth.Rate;
                float thud = 0.85f * Mathf.Sin(ph) * Mathf.Exp(-t * 16f) + 0.6f * lp.Step(Synth.Noise(i, 0x888u)) * 3f * Mathf.Exp(-t * 30f);
                float grain = 0.25f * grit.Step(Synth.Noise(i, 0x999u)) * Mathf.Exp(-t * 18f);
                return Synth.Soft(thud + grain);
            });
        }

        private static AudioClip SlideLoop => _slideLoop ? _slideLoop : _slideLoop = MakeSlideLoop();

        private static AudioClip MakeSlideLoop()
        {
            var band = new Synth.Band(250f, 2200f);
            return Synth.Make("move-slide", 1.0f, true, (t, i) =>
            {
                float grains = 0.7f + 0.3f * (Synth.Noise(i / 90, 0xaaau) > 0.3f ? 1f : 0f);
                return 0.8f * band.Step(Synth.Noise(i, 0xbbbu)) * 2.2f * grains;
            });
        }

        /// <summary>Slide-land success: a short rising whoosh that snaps shut with a crisp tick and a low punch.</summary>
        private static AudioClip SlideLand => _slideLand ? _slideLand : _slideLand = MakeSlideLand();

        private static AudioClip MakeSlideLand()
        {
            var lpA = new Synth.LowPass(800f);
            var lpB = new Synth.LowPass(3000f);
            return Synth.Make("move-slide-land", 0.32f, false, (t, i) =>
            {
                float sweep = Mathf.Clamp01(t / 0.09f);
                float n = Synth.Noise(i, 0xcccu);
                float whoosh = Mathf.Lerp(lpA.Step(n), lpB.Step(n), sweep) * 0.9f * (t < 0.09f ? sweep : Mathf.Exp(-(t - 0.09f) * 40f));
                float u = t - 0.09f;
                float snap = u < 0 ? 0f : 0.5f * Mathf.Sin(2 * Mathf.PI * 2400 * u) * Mathf.Exp(-u * 90f) + 0.6f * Mathf.Sin(2 * Mathf.PI * (70 + 50 * Mathf.Exp(-u * 30f)) * u) * Mathf.Exp(-u * 16f);
                return Synth.Soft(whoosh + snap);
            });
        }

        /// <summary>Hands on stone: a slap and a short scrape (vault and mantle).</summary>
        private static AudioClip Grab => _grab ? _grab : _grab = MakeGrab();

        private static AudioClip MakeGrab()
        {
            var band = new Synth.Band(400f, 3000f);
            return Synth.Make("move-grab", 0.22f, false, (t, i) =>
                0.6f * Synth.Noise(i, 0xdddu) * Mathf.Exp(-t * 120f) +
                0.35f * band.Step(Synth.Noise(i, 0xeeeu)) * 2f * Mathf.Exp(-Mathf.Max(0, t - 0.02f) * 14f) * Mathf.Min(1f, t * 100f));
        }
    }
}
