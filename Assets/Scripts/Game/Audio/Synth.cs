using System;
using UnityEngine;

namespace Sculpt.Game.Audio
{
    /// <summary>
    /// Small helpers for sounds generated in code (no audio assets, nothing licensed): clip builder, deterministic noise,
    /// one-pole filters, and a pooled one-shot player so a busy fight does not create and destroy GameObjects per sound.
    /// </summary>
    public static class Synth
    {
        public const int Rate = 44100;

        /// <summary>Deterministic white noise in -1..1 for sample i (and a salt so layers don't correlate).</summary>
        public static float Noise(int i, uint salt = 0x5bd1e995u)
        {
            uint h = (uint)i * 2654435761u ^ salt;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        /// <summary>Gentle saturation: loud without harsh clipping.</summary>
        public static float Soft(float x) => x / (1f + Mathf.Abs(x) * 0.6f);

        /// <summary>One-pole low-pass. k = 1 - exp(-2 pi fc / rate).</summary>
        public sealed class LowPass
        {
            private readonly float _k;
            private float _y;
            public LowPass(float cutoffHz) { _k = 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / Rate); }
            public float Step(float x) { _y += (x - _y) * _k; return _y; }
        }

        /// <summary>Band-ish filter: high-pass of a low-pass (two one-poles).</summary>
        public sealed class Band
        {
            private readonly LowPass _lo, _hi;
            public Band(float lowHz, float highHz) { _lo = new LowPass(highHz); _hi = new LowPass(lowHz); }
            public float Step(float x) { float l = _lo.Step(x); return l - _hi.Step(l); }
        }

        /// <summary>
        /// Builds a mono clip. For loops the ends are crossfaded so there is no click; one-shots fade out over the last
        /// 20 ms. The generator is called once per sample in order (filters may keep state).
        /// </summary>
        public static AudioClip Make(string name, float seconds, bool loop, Func<float, int, float> f, float gain = 1f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                data[i] = f(t, i) * gain;
            }
            if (loop)
            {
                // Crossfade the last 30 ms into the start so the loop is seamless.
                int x = Mathf.Min(n / 4, (int)(0.03f * Rate));
                for (int i = 0; i < x; i++)
                {
                    float a = i / (float)x;
                    data[i] = data[i] * a + data[n - x + i] * (1f - a);
                }
                Array.Resize(ref data, n - x);
                n -= x;
            }
            else
            {
                int fade = Mathf.Min(n, (int)(0.02f * Rate));
                for (int i = 0; i < fade; i++) data[n - 1 - i] *= i / (float)fade;
            }
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(data[i], -1f, 1f);
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ---------------------------------------------------------------- Pooled one-shots

        private static AudioSource[] _pool;
        private static int _next;
        private static Transform _root;

        /// <summary>
        /// Plays a one-shot at a point (3D) or on the listener (2D). Linear rolloff by default (sculpt and movement sounds
        /// are local information: they fade out by maxDistance). Steals the oldest voice when the pool is full.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 at, float volume = 1f, float pitch = 1f, bool spatial = true,
                                float minDistance = 2f, float maxDistance = 45f, bool logarithmic = false, int priority = 128)
        {
            if (clip == null) return;
            AudioSource s = Voice();
            s.transform.position = at;
            s.clip = clip;
            s.volume = volume;
            s.pitch = pitch;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = logarithmic ? AudioRolloffMode.Logarithmic : AudioRolloffMode.Linear;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            s.priority = spatial ? priority : Mathf.Min(priority, 16);
            s.Play();
        }

        private static AudioSource Voice()
        {
            if (_pool == null || _root == null)
            {
                _root = new GameObject("Sfx Pool").transform;
                UnityEngine.Object.DontDestroyOnLoad(_root.gameObject);
                _pool = new AudioSource[48];
                for (int i = 0; i < _pool.Length; i++)
                {
                    var go = new GameObject("Sfx " + i);
                    go.transform.SetParent(_root, false);
                    var a = go.AddComponent<AudioSource>();
                    a.playOnAwake = false;
                    a.dopplerLevel = 0f;
                    _pool[i] = a;
                }
            }
            // Prefer a free voice; otherwise steal round-robin.
            for (int k = 0; k < _pool.Length; k++)
            {
                int i = (_next + k) % _pool.Length;
                if (!_pool[i].isPlaying) { _next = (i + 1) % _pool.Length; return _pool[i]; }
            }
            AudioSource v = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            v.Stop();
            return v;
        }

        /// <summary>A looping positional source you own (hums, slide scrape).</summary>
        public static AudioSource Loop(string name, Transform parent, AudioClip clip, float minDistance, float maxDistance, bool spatial = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0f;
            s.volume = 0f;
            s.Play();
            return s;
        }
    }
}
