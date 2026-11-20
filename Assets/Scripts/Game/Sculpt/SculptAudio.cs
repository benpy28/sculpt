using UnityEngine;

namespace Sculpt.Game.Sculpting
{
    /// <summary>
    /// Placeholder sculpt sounds generated in code (no assets yet; the real mix comes in M6). Each has its own
    /// character so they read in a fight: windup hum (positional, enemies hear it), throw grind (rises), deep thunk on
    /// landing, crunch on impact, a dry click for a refused confirm, and a low thump "from below" when you are targeted.
    /// </summary>
    public static class SculptAudio
    {
        private const int Rate = 44100;
        private static AudioClip _hum, _grind, _thunk, _crunch, _reject, _warn, _confirm;

        public static AudioClip Hum => _hum ? _hum : _hum = Make("sculpt-hum", 1.0f, true, (t, i) =>
            0.30f * Mathf.Sin(2 * Mathf.PI * 110 * t) + 0.16f * Mathf.Sin(2 * Mathf.PI * 220.5f * t) +
            0.08f * Mathf.Sin(2 * Mathf.PI * 331 * t) * (0.6f + 0.4f * Mathf.Sin(2 * Mathf.PI * 9 * t)) + 0.05f * Noise(i));

        public static AudioClip Grind => _grind ? _grind : _grind = Make("sculpt-grind", 1.0f, true, (t, i) =>
            0.35f * Noise(i) * (0.5f + 0.5f * Mathf.Sin(2 * Mathf.PI * 23 * t)) + 0.2f * Mathf.Sin(2 * Mathf.PI * 70 * t));

        public static AudioClip Thunk => _thunk ? _thunk : _thunk = Make("sculpt-thunk", 0.45f, false, (t, i) =>
        {
            float env = Mathf.Exp(-t * 11f);
            float f = 62f + 40f * Mathf.Exp(-t * 30f);
            return env * (0.9f * Mathf.Sin(2 * Mathf.PI * f * t) + 0.35f * Noise(i) * Mathf.Exp(-t * 40f));
        });

        public static AudioClip Crunch => _crunch ? _crunch : _crunch = Make("sculpt-crunch", 0.6f, false, (t, i) =>
            Mathf.Exp(-t * 7f) * (0.7f * Noise(i) + 0.5f * Mathf.Sin(2 * Mathf.PI * 48 * t)));

        public static AudioClip Reject => _reject ? _reject : _reject = Make("sculpt-reject", 0.08f, false, (t, i) =>
            Mathf.Exp(-t * 60f) * 0.5f * Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 380 * t)));

        public static AudioClip Warn => _warn ? _warn : _warn = Make("sculpt-warn", 0.35f, false, (t, i) =>
            Mathf.Exp(-t * 9f) * (0.8f * Mathf.Sin(2 * Mathf.PI * 45 * t) + 0.3f * Mathf.Sin(2 * Mathf.PI * 90 * t)));

        public static AudioClip Confirm => _confirm ? _confirm : _confirm = Make("sculpt-confirm", 0.06f, false, (t, i) =>
            Mathf.Exp(-t * 70f) * 0.35f * Mathf.Sin(2 * Mathf.PI * 900 * t));

        private static uint _seed = 12345;

        private static float Noise(int i)
        {
            uint h = (uint)i * 2654435761u ^ _seed;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        private static AudioClip Make(string name, float seconds, bool loop, System.Func<float, int, float> f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float v = f(t, i);
                if (loop)
                {
                    // Crossfade the ends so the loop has no click.
                    float edge = Mathf.Min(1f, Mathf.Min(t, seconds - t) / 0.02f);
                    v *= Mathf.Lerp(0.85f, 1f, edge);
                }
                data[i] = Mathf.Clamp(v, -1f, 1f);
            }
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>A one-shot at a point (3D) or on the listener (2D when spatial is false).</summary>
        public static void Play(AudioClip clip, Vector3 at, float volume = 1f, float pitch = 1f, bool spatial = true)
        {
            var go = new GameObject("Sfx " + clip.name);
            go.transform.position = at;
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.volume = volume;
            s.pitch = pitch;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 2f;
            s.maxDistance = 45f;
            s.dopplerLevel = 0f;
            s.Play();
            Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.1f);
        }
    }
}
