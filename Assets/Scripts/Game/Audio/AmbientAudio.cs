using System.Collections.Generic;
using Sculpt.Game.Settings;
using Sculpt.Game.World;
using UnityEngine;

namespace Sculpt.Game.Audio
{
    /// <summary>
    /// Plains ambience, all generated in code (no recordings, nothing licensed): a wind bed that follows the shared
    /// gusts (<see cref="Wind"/>) with a leafy rustle layer, and sparse birdsong from the tree canopies around you.
    /// Kept quiet and high-pitched so it never masks footsteps, sculpt hums or gunfire; its own volume setting.
    /// </summary>
    public sealed class AmbientAudio : MonoBehaviour
    {
        private ClientSettings _settings;
        private AudioSource _wind, _rustle;
        private AudioClip[] _birds;
        private readonly List<Vector3> _perches = new List<Vector3>();
        private float _nextBird = 2f;
        private int _phraseLeft;
        private Vector3 _phraseAt;
        private int _phraseClip;
        private System.Random _rng = new System.Random(1);

        /// <summary>perches: canopy points birds sing from (may be empty: then they sing from around the listener).</summary>
        public void Init(ClientSettings settings, IEnumerable<Vector3> perches)
        {
            _settings = settings;
            if (perches != null) _perches.AddRange(perches);
            _wind = Synth.Loop("Ambience wind", transform, WindClip(), 1f, 10f, spatial: false);
            _rustle = Synth.Loop("Ambience rustle", transform, RustleClip(), 1f, 10f, spatial: false);
            _wind.priority = 200;
            _rustle.priority = 210;
            _birds = new[] { TwoNote(), Trill(), Chirps(), Warble(), Peep() };
        }

        private void Update()
        {
            if (_settings == null) return;
            float amb = _settings.AmbienceVolume;
            float g = Wind.Gust;
            _wind.volume = amb * (0.10f + 0.22f * g);
            _wind.pitch = 0.9f + 0.2f * g;
            _rustle.volume = amb * 0.07f * Mathf.SmoothStep(0.2f, 1f, g);

            Camera cam = Camera.main;
            if (cam == null || amb <= 0.001f) return;
            _nextBird -= Time.unscaledDeltaTime;
            if (_nextBird > 0) return;
            Vector3 ear = cam.transform.position;
            if (_phraseLeft <= 0)
            {
                // A new singer: a canopy within 55 m if there is one, else somewhere 20-40 m away and up high.
                _phraseClip = _rng.Next(_birds.Length);
                _phraseAt = PickPerch(ear);
                _phraseLeft = 1 + _rng.Next(4);
            }
            float pitch = 0.92f + (float)_rng.NextDouble() * 0.16f;
            Synth.Play(_birds[_phraseClip], _phraseAt, amb * (0.10f + (float)_rng.NextDouble() * 0.06f), pitch,
                       spatial: true, minDistance: 6f, maxDistance: 70f, logarithmic: true, priority: 220, route: false, bus: SoundBus.Ambience);
            _phraseLeft--;
            _nextBird = _phraseLeft > 0 ? 0.25f + (float)_rng.NextDouble() * 0.5f : 2.5f + (float)_rng.NextDouble() * 6f;
        }

        private Vector3 PickPerch(Vector3 ear)
        {
            for (int tries = 0; tries < 8 && _perches.Count > 0; tries++)
            {
                Vector3 p = _perches[_rng.Next(_perches.Count)];
                if ((p - ear).sqrMagnitude < 55f * 55f) return p;
            }
            float a = (float)_rng.NextDouble() * Mathf.PI * 2f;
            float d = 20f + (float)_rng.NextDouble() * 20f;
            return ear + new Vector3(Mathf.Cos(a) * d, 6f + (float)_rng.NextDouble() * 6f, Mathf.Sin(a) * d);
        }

        // ---------------------------------------------------------------- Generated clips

        private static AudioClip WindClip()
        {
            var lo = new Synth.LowPass(380f);
            var lo2 = new Synth.LowPass(90f);
            return Synth.Make("wind", 9f, true, (t, i) =>
            {
                float nz = Synth.Noise(i, 0x1234567u);
                float body = lo.Step(nz) * 2.4f - lo2.Step(nz) * 1.2f;
                float swell = 0.65f + 0.35f * Mathf.Sin(t * 0.7f) * Mathf.Sin(t * 0.31f + 1f);
                return body * swell;
            }, 0.9f);
        }

        private static AudioClip RustleClip()
        {
            var band = new Synth.Band(1800f, 6500f);
            var env = new Synth.LowPass(6f);
            return Synth.Make("rustle", 7f, true, (t, i) =>
            {
                float gate = Synth.Noise(i / 2205, 0xBEEFu) > 0.2f ? 1f : 0.25f; // clumps of leaves every ~50 ms
                return band.Step(Synth.Noise(i, 0xABCDu)) * env.Step(gate) * 1.4f;
            }, 0.8f);
        }

        private static float Env(float t, float start, float len)
        {
            float x = (t - start) / len;
            if (x < 0 || x > 1) return 0;
            return Mathf.Sin(x * Mathf.PI) * Mathf.Min(1f, x * 6f);
        }

        /// <summary>Two clear notes, the second lower.</summary>
        private static AudioClip TwoNote()
        {
            float ph = 0;
            return Synth.Make("bird two-note", 0.6f, false, (t, i) =>
            {
                float f = t < 0.26f ? 3300f - t * 600f : 2650f - (t - 0.3f) * 300f;
                ph += f / Synth.Rate;
                float e = Env(t, 0.0f, 0.22f) + Env(t, 0.3f, 0.26f);
                return Mathf.Sin(ph * Mathf.PI * 2f) * e * 0.5f;
            });
        }

        /// <summary>A fast rising trill.</summary>
        private static AudioClip Trill()
        {
            float ph = 0;
            return Synth.Make("bird trill", 0.75f, false, (t, i) =>
            {
                float f = 2700f + t * 900f + Mathf.Sin(t * 2f * Mathf.PI * 22f) * 250f;
                ph += f / Synth.Rate;
                float am = 0.55f + 0.45f * Mathf.Sin(t * 2f * Mathf.PI * 22f);
                return Mathf.Sin(ph * Mathf.PI * 2f) * am * Env(t, 0f, 0.72f) * 0.4f;
            });
        }

        /// <summary>Three to four quick upward chirps.</summary>
        private static AudioClip Chirps()
        {
            float ph = 0;
            return Synth.Make("bird chirps", 0.5f, false, (t, i) =>
            {
                float local = t % 0.11f;
                int k = (int)(t / 0.11f);
                if (k > 3 || local > 0.06f) { ph = 0; return 0f; }
                float f = 2400f + local / 0.06f * 2300f;
                ph += f / Synth.Rate;
                return Mathf.Sin(ph * Mathf.PI * 2f) * Mathf.Sin(local / 0.06f * Mathf.PI) * 0.45f;
            });
        }

        /// <summary>A wavering song phrase.</summary>
        private static AudioClip Warble()
        {
            float ph = 0;
            return Synth.Make("bird warble", 0.9f, false, (t, i) =>
            {
                float f = 3500f + Mathf.Sin(t * 2f * Mathf.PI * 9f) * 600f + Mathf.Sin(t * 2f * Mathf.PI * 2.3f) * 300f;
                ph += f / Synth.Rate;
                float gaps = Mathf.Sin(t * 2f * Mathf.PI * 4.5f) > -0.6f ? 1f : 0f;
                return Mathf.Sin(ph * Mathf.PI * 2f) * gaps * Env(t, 0f, 0.88f) * 0.35f;
            });
        }

        /// <summary>A soft single peep with a little harmonic.</summary>
        private static AudioClip Peep()
        {
            float ph = 0;
            return Synth.Make("bird peep", 0.22f, false, (t, i) =>
            {
                float f = 4100f - t * 2500f;
                ph += f / Synth.Rate;
                float s = Mathf.Sin(ph * Mathf.PI * 2f) + 0.25f * Mathf.Sin(ph * Mathf.PI * 4f);
                return s * Env(t, 0f, 0.18f) * 0.4f;
            });
        }
    }
}
