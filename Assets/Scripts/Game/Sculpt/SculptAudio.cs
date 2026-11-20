using Sculpt.Core;
using Sculpt.Game.Audio;
using UnityEngine;

namespace Sculpt.Game.Sculpting
{
    /// <summary>
    /// Sculpt sounds generated in code (no assets). Each one has its own character so it reads in a fight without looking:
    /// - windup hums, positional and heard by enemies, one per action type: a push/pull throbs, a raise swells upward,
    ///   a sink groans downward (the view bends the pitch with progress); reinforced actions add a glassy, heavier layer;
    /// - a throw's rising grind;
    /// - the deep thunk of a piece landing: sub drop, stone knock, gravel spill; reinforced pieces land heavier with a
    ///   crystalline ring;
    /// - impact crunch for thrown pieces, a dry click for a refused confirm, a short tick when your confirm takes;
    /// - the targeted warning: a double knock from below, low and unlike anything else.
    /// </summary>
    public static class SculptAudio
    {
        private static AudioClip _humMove, _humRaise, _humSink, _humCrystal, _grind;
        private static AudioClip _thunk, _thunkHeavy, _crunch, _reject, _warn, _confirm, _confirmHeavy;

        // ---------------------------------------------------------------- Hums (loops)

        /// <summary>The windup loop for an action. Reinforced actions also play <see cref="HumCrystal"/> on top.</summary>
        public static AudioClip HumFor(SculptActionType type)
        {
            switch (type)
            {
                case SculptActionType.Raise: return HumRaise;
                case SculptActionType.Sink: return HumSink;
                case SculptActionType.Throw: return Grind;
                default: return HumMove;
            }
        }

        /// <summary>Push/pull: a stone-on-stone throb, pulsing at 6 Hz like something being shoved.</summary>
        public static AudioClip HumMove => _humMove ? _humMove : _humMove = MakeHum("sculpt-hum-move", 92f, 6f, 0.55f, 0x1111u);

        /// <summary>Raise: brighter, open fifths; the view bends it up as the windup fills.</summary>
        public static AudioClip HumRaise => _humRaise ? _humRaise : _humRaise = MakeHum("sculpt-hum-raise", 123f, 9f, 0.25f, 0x2222u, fifth: true);

        /// <summary>Sink: low, dark groan with a slow wobble; the view bends it down.</summary>
        public static AudioClip HumSink => _humSink ? _humSink : _humSink = MakeHum("sculpt-hum-sink", 64f, 3.5f, 0.45f, 0x3333u);

        private static AudioClip MakeHum(string name, float f0, float tremHz, float tremDepth, uint salt, bool fifth = false)
        {
            var grit = new Synth.Band(300f, 1800f);
            float p1 = 0, p2 = 0, p3 = 0;
            return Synth.Make(name, 1.0f, true, (t, i) =>
            {
                // Integer cycles per second keep the loop seamless.
                p1 += 2 * Mathf.PI * f0 / Synth.Rate;
                p2 += 2 * Mathf.PI * (fifth ? f0 * 1.5f : f0 * 2.01f) / Synth.Rate;
                p3 += 2 * Mathf.PI * f0 * 3.02f / Synth.Rate;
                float trem = 1f - tremDepth * (0.5f + 0.5f * Mathf.Sin(2 * Mathf.PI * tremHz * t));
                float tone = 0.34f * Mathf.Sin(p1) + 0.17f * Mathf.Sin(p2) + 0.07f * Mathf.Sin(p3);
                float rasp = 0.16f * grit.Step(Synth.Noise(i, salt)) * (0.6f + 0.4f * Mathf.Sin(2 * Mathf.PI * 31 * t));
                return Synth.Soft((tone + rasp) * trem);
            });
        }

        /// <summary>Reinforced layer: glassy inharmonic partials with a shimmer, quiet but unmistakable.</summary>
        public static AudioClip HumCrystal => _humCrystal ? _humCrystal : _humCrystal = Synth.Make("sculpt-hum-crystal", 1.0f, true, (t, i) =>
        {
            float shimmer = 0.6f + 0.4f * Mathf.Sin(2 * Mathf.PI * 13 * t);
            return shimmer * (0.10f * Mathf.Sin(2 * Mathf.PI * 1318 * t) + 0.07f * Mathf.Sin(2 * Mathf.PI * 2093 * t) +
                              0.05f * Mathf.Sin(2 * Mathf.PI * 3127 * t)) + 0.22f * Mathf.Sin(2 * Mathf.PI * 46 * t);
        });

        /// <summary>Throw windup: grinding rock that rises (pitch rises with progress in the view).</summary>
        public static AudioClip Grind => _grind ? _grind : _grind = MakeGrind();

        private static AudioClip MakeGrind()
        {
            var lp = new Synth.LowPass(900f);
            return Synth.Make("sculpt-grind", 1.0f, true, (t, i) =>
                0.55f * lp.Step(Synth.Noise(i, 0x4444u)) * 2f * (0.55f + 0.45f * Mathf.Sin(2 * Mathf.PI * 23 * t)) +
                0.20f * Mathf.Sin(2 * Mathf.PI * 70 * t) + 0.08f * Mathf.Sin(2 * Mathf.PI * 141 * t));
        }

        // ---------------------------------------------------------------- One-shots

        /// <summary>A piece landing: sub drop, stone knock and a short gravel spill.</summary>
        public static AudioClip Thunk => _thunk ? _thunk : _thunk = MakeThunk("sculpt-thunk", 1f, false);

        /// <summary>A reinforced piece landing: lower, longer, with a crystalline ring.</summary>
        public static AudioClip ThunkHeavy => _thunkHeavy ? _thunkHeavy : _thunkHeavy = MakeThunk("sculpt-thunk-heavy", 0.8f, true);

        private static AudioClip MakeThunk(string name, float pitch, bool crystal)
        {
            var gravel = new Synth.Band(500f, 3500f);
            var rumble = new Synth.LowPass(160f);
            float ph = 0, knock = 0;
            float len = crystal ? 0.9f : 0.6f;
            return Synth.Make(name, len, false, (t, i) =>
            {
                // Sub drop: 95 Hz falling to 42 Hz.
                float f = (42f + 53f * Mathf.Exp(-t * 26f)) * pitch;
                ph += 2 * Mathf.PI * f / Synth.Rate;
                float sub = 0.95f * Mathf.Sin(ph) * Mathf.Exp(-t * (crystal ? 7f : 10f));
                // Stone knock: short, mid, a little detuned.
                knock += 2 * Mathf.PI * 190f * pitch / Synth.Rate;
                float k = 0.35f * (Mathf.Sin(knock) + 0.5f * Mathf.Sin(knock * 2.37f)) * Mathf.Exp(-t * 55f);
                // Attack transient.
                float click = 0.45f * Synth.Noise(i, 0x51u) * Mathf.Exp(-t * 180f);
                // Gravel spill: grains that trail off over ~0.25 s.
                float grains = Synth.Noise(i / 37, 0x77u) > 0.55f ? 1f : 0.15f;
                float spill = 0.28f * gravel.Step(Synth.Noise(i, 0x99u)) * grains * Mathf.Exp(-Mathf.Max(0, t - 0.02f) * 9f) * Mathf.Min(1f, t * 60f);
                float tail = 0.25f * rumble.Step(Synth.Noise(i, 0x123u)) * 4f * Mathf.Exp(-t * 6f);
                float ring = 0f;
                if (crystal)
                {
                    float e = Mathf.Exp(-t * 4.5f) * Mathf.Min(1f, t * 80f);
                    ring = e * (0.09f * Mathf.Sin(2 * Mathf.PI * 1567 * t) + 0.07f * Mathf.Sin(2 * Mathf.PI * 2489 * t) + 0.05f * Mathf.Sin(2 * Mathf.PI * 3951 * t));
                }
                return Synth.Soft(sub + k + click + spill + tail + ring);
            });
        }

        /// <summary>A thrown piece shattering.</summary>
        public static AudioClip Crunch => _crunch ? _crunch : _crunch = MakeCrunch();

        private static AudioClip MakeCrunch()
        {
            var lp = new Synth.LowPass(2500f);
            float ph = 0;
            return Synth.Make("sculpt-crunch", 0.8f, false, (t, i) =>
            {
                ph += 2 * Mathf.PI * (40f + 50f * Mathf.Exp(-t * 18f)) / Synth.Rate;
                float burst = 0.8f * lp.Step(Synth.Noise(i, 0xabcu)) * Mathf.Exp(-t * 6f);
                float grains = Synth.Noise(i / 23, 0xdefu) > 0.4f ? 1f : 0.2f;
                return Synth.Soft(burst * grains * 1.6f + 0.7f * Mathf.Sin(ph) * Mathf.Exp(-t * 8f));
            });
        }

        /// <summary>Refused confirm: a dry, short buzz (red feedback goes with it).</summary>
        public static AudioClip Reject => _reject ? _reject : _reject = Synth.Make("sculpt-reject", 0.09f, false, (t, i) =>
            Mathf.Exp(-t * 55f) * 0.45f * Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 330 * t)) * (0.7f + 0.3f * Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 47 * t))));

        /// <summary>
        /// You are being sculpted: a double knock from below (two low thumps 110 ms apart with a rumble). Nothing else in
        /// the game sounds like it.
        /// </summary>
        public static AudioClip Warn => _warn ? _warn : _warn = MakeWarn();

        private static AudioClip MakeWarn()
        {
            var lp = new Synth.LowPass(120f);
            return Synth.Make("sculpt-warn", 0.45f, false, (t, i) =>
            {
                float a = Knock(t, 0f, 1f), b = Knock(t, 0.11f, 0.8f);
                float rumble = 0.3f * lp.Step(Synth.Noise(i, 0x5eu)) * 5f * Mathf.Exp(-t * 6f);
                return Synth.Soft(a + b + rumble);
            });
        }

        private static float Knock(float t, float at, float amp)
        {
            float u = t - at;
            if (u < 0) return 0;
            float f = 38f + 34f * Mathf.Exp(-u * 40f);
            return amp * Mathf.Exp(-u * 18f) * Mathf.Sin(2 * Mathf.PI * f * u) * 0.9f;
        }

        /// <summary>Your confirm took: short tick.</summary>
        public static AudioClip Confirm => _confirm ? _confirm : _confirm = Synth.Make("sculpt-confirm", 0.07f, false, (t, i) =>
            Mathf.Exp(-t * 70f) * (0.3f * Mathf.Sin(2 * Mathf.PI * 880 * t) + 0.15f * Synth.Noise(i) * Mathf.Exp(-t * 200f)));

        /// <summary>Your reinforced confirm took: lower tick with a glassy edge.</summary>
        public static AudioClip ConfirmHeavy => _confirmHeavy ? _confirmHeavy : _confirmHeavy = Synth.Make("sculpt-confirm-heavy", 0.18f, false, (t, i) =>
            Mathf.Exp(-t * 30f) * (0.35f * Mathf.Sin(2 * Mathf.PI * 520 * t) + 0.12f * Mathf.Sin(2 * Mathf.PI * 2093 * t)) +
            Mathf.Exp(-t * 20f) * 0.4f * Mathf.Sin(2 * Mathf.PI * 70 * t));

        /// <summary>A one-shot at a point (3D) or on the listener (2D when spatial is false).</summary>
        public static void Play(AudioClip clip, Vector3 at, float volume = 1f, float pitch = 1f, bool spatial = true) =>
            Synth.Play(clip, at, volume, pitch, spatial, 2f, 45f);
    }
}
