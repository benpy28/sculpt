using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Weapons
{
    /// <summary>
    /// Placeholder gun sounds generated in code (the real mix comes in M6). Built for weight, not toy pops: every shot is
    /// a sharp supersonic crack, a low pitch-dropping body thump and a lowpassed room tail; the pump adds its rack.
    /// Hit confirms are short and distinct: body tick, brighter head ping, deep kill thud.
    /// </summary>
    public static class GunAudio
    {
        private const int Rate = 44100;
        private static AudioClip[] _shots;
        private static AudioClip _rack, _hit, _head, _kill, _hurt, _impact, _flesh;

        private struct Shape
        {
            public float Len, Crack, CrackDecay, Thump, ThumpHi, ThumpLo, ThumpDecay, Tail, TailDecay, TailCutoff;
            /// <summary>Mechanical action under the shot (bolt or slide cycling): level and delay in seconds.</summary>
            public float Mech, MechAt;
        }

        private static readonly Shape[] Shapes =
        {
            // Pump: huge low body, long tail.
            new Shape { Len = 1.1f, Crack = 0.55f, CrackDecay = 45, Thump = 1.0f, ThumpHi = 95, ThumpLo = 36, ThumpDecay = 8, Tail = 0.55f, TailDecay = 3.6f, TailCutoff = 0.06f },
            // SMG: short, snappy, less body.
            new Shape { Len = 0.4f, Crack = 0.7f, CrackDecay = 85, Thump = 0.42f, ThumpHi = 170, ThumpLo = 75, ThumpDecay = 30, Tail = 0.25f, TailDecay = 11, TailCutoff = 0.12f, Mech = 0.22f, MechAt = 0.028f },
            // AR: punchy crack and a solid thump.
            new Shape { Len = 0.75f, Crack = 0.8f, CrackDecay = 70, Thump = 0.72f, ThumpHi = 135, ThumpLo = 48, ThumpDecay = 15, Tail = 0.36f, TailDecay = 5.5f, TailCutoff = 0.08f, Mech = 0.26f, MechAt = 0.036f },
            // Pistol: sharp crack, medium body.
            new Shape { Len = 0.55f, Crack = 0.85f, CrackDecay = 95, Thump = 0.55f, ThumpHi = 185, ThumpLo = 70, ThumpDecay = 22, Tail = 0.26f, TailDecay = 7.5f, TailCutoff = 0.1f, Mech = 0.3f, MechAt = 0.022f },
        };

        public static AudioClip Shot(Weapon w)
        {
            if (_shots == null) _shots = new AudioClip[4];
            int i = (int)w;
            if (_shots[i] != null) return _shots[i];
            Shape s = Shapes[i];
            float lp = 0;
            float phase = 0;
            return _shots[i] = Make("gun-" + w, s.Len, (t, n) =>
            {
                float crack = s.Crack * Noise(n) * Mathf.Exp(-t * s.CrackDecay);
                float f = s.ThumpLo + (s.ThumpHi - s.ThumpLo) * Mathf.Exp(-t * 25f);
                phase += 2 * Mathf.PI * f / Rate;
                float thump = s.Thump * Mathf.Sin(phase) * Mathf.Exp(-t * s.ThumpDecay);
                lp += (Noise(n + 7919) - lp) * s.TailCutoff;
                float tail = s.Tail * lp * 3f * Mathf.Exp(-t * s.TailDecay) * Mathf.Min(1f, t * 200f);
                float mech = s.Mech > 0 ? s.Mech * (Clack(t, s.MechAt, 1f, n) + Clack(t, s.MechAt + 0.03f, 0.6f, n + 31)) : 0f;
                return Soft(crack + thump + tail + mech);
            });
        }

        /// <summary>Pump rack: back (two metal clacks) then forward.</summary>
        public static AudioClip Rack => _rack ? _rack : _rack = Make("gun-rack", 0.42f, (t, n) =>
            Clack(t, 0.0f, 1.0f, n) + Clack(t, 0.035f, 0.5f, n) + Clack(t, 0.2f, 0.9f, n));

        private static AudioClip _magOut, _magIn, _shellIn;

        /// <summary>Magazine out: a release click, then the mag scraping free.</summary>
        public static AudioClip MagOut => _magOut ? _magOut : _magOut = Make("gun-mag-out", 0.3f, (t, n) =>
            0.7f * Clack(t, 0.0f, 0.8f, n) + 0.35f * Noise(n * 5 + 3) * Mathf.Exp(-Mathf.Pow((t - 0.09f) / 0.05f, 2f)));

        /// <summary>Magazine in: a heavy seat and a latch click (the reload's main beat).</summary>
        public static AudioClip MagIn => _magIn ? _magIn : _magIn = Make("gun-mag-in", 0.32f, (t, n) =>
            Clack(t, 0.0f, 1.1f, n) + 0.45f * Mathf.Sin(2 * Mathf.PI * 95 * t) * Mathf.Exp(-t * 30f) + Clack(t, 0.11f, 0.55f, n + 17));

        /// <summary>One shell pushed into the pump tube.</summary>
        public static AudioClip ShellIn => _shellIn ? _shellIn : _shellIn = Make("gun-shell-in", 0.16f, (t, n) =>
            0.8f * Clack(t, 0.0f, 0.7f, n) + 0.3f * Mathf.Sin(2 * Mathf.PI * 420 * t) * Mathf.Exp(-t * 45f));

        public static AudioClip HitTick => _hit ? _hit : _hit = Make("hit-body", 0.05f, (t, n) =>
            0.5f * Mathf.Sin(2 * Mathf.PI * 2600 * t) * Mathf.Exp(-t * 90f));

        public static AudioClip HitHead => _head ? _head : _head = Make("hit-head", 0.12f, (t, n) =>
            (0.45f * Mathf.Sin(2 * Mathf.PI * 4100 * t) + 0.25f * Mathf.Sin(2 * Mathf.PI * 6150 * t)) * Mathf.Exp(-t * 35f));

        public static AudioClip Kill => _kill ? _kill : _kill = Make("hit-kill", 0.35f, (t, n) =>
            0.8f * Mathf.Sin(2 * Mathf.PI * (70 + 60 * Mathf.Exp(-t * 20)) * t) * Mathf.Exp(-t * 9f) +
            0.3f * Mathf.Sin(2 * Mathf.PI * 1250 * t) * Mathf.Exp(-t * 14f));

        public static AudioClip Hurt => _hurt ? _hurt : _hurt = Make("hurt", 0.25f, (t, n) =>
            Mathf.Exp(-t * 16f) * (0.7f * Mathf.Sin(2 * Mathf.PI * 58 * t) + 0.3f * Noise(n) * Mathf.Exp(-t * 40f)));

        /// <summary>Bullet into rock: short dry crack with grit.</summary>
        public static AudioClip Impact => _impact ? _impact : _impact = Make("impact-rock", 0.12f, (t, n) =>
            Mathf.Exp(-t * 55f) * (0.6f * Noise(n) + 0.3f * Mathf.Sin(2 * Mathf.PI * 900 * t)));

        public static AudioClip Flesh => _flesh ? _flesh : _flesh = Make("impact-flesh", 0.1f, (t, n) =>
            Mathf.Exp(-t * 45f) * (0.6f * Mathf.Sin(2 * Mathf.PI * 140 * t) + 0.25f * Noise(n)));

        private static float Clack(float t, float at, float amp, int n)
        {
            float u = t - at;
            if (u < 0) return 0;
            return amp * Mathf.Exp(-u * 160f) * (0.55f * Noise(n * 3 + 11) + 0.4f * Mathf.Sin(2 * Mathf.PI * 1850 * u) + 0.25f * Mathf.Sin(2 * Mathf.PI * 3300 * u));
        }

        private static float Soft(float x) => x / (1f + Mathf.Abs(x) * 0.6f); // gentle clip: loud without harsh distortion

        private static float Noise(int i)
        {
            uint h = (uint)i * 2654435761u ^ 0x5bd1e995u;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        private static AudioClip Make(string name, float seconds, System.Func<float, int, float> f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float fade = Mathf.Min(1f, (seconds - t) / 0.02f); // no click at the end
                data[i] = Mathf.Clamp(f(t, i) * fade, -1f, 1f);
            }
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>A one-shot. Gunshots carry far (logarithmic rolloff out to 220 m) so you can always hear where fire comes from.</summary>
        public static void Play(AudioClip clip, Vector3 at, float volume, float pitch, bool spatial, float maxDistance = 220f, float minDistance = 3f)
        {
            var go = new GameObject("Sfx " + clip.name);
            go.transform.position = at;
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.volume = volume;
            s.pitch = pitch;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0f;
            s.priority = spatial ? 128 : 16;
            s.Play();
            Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.1f);
        }
    }
}
