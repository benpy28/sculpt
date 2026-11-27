using UnityEngine;

namespace Sculpt.Game.Audio
{
    /// <summary>
    /// A looping positional sound (a heal hum, someone's slide scrape) that is heard the way it really reaches you, like
    /// the one-shots (<see cref="SoundRouter"/>). Owners set <see cref="At"/>, <see cref="Volume"/> and
    /// <see cref="Pitch"/> instead of moving the source; the route is worked out again ten times a second and the
    /// gain and muffling glide to it (no zipper noise when someone walks past a doorway).
    /// </summary>
    [DefaultExecutionOrder(10000)] // after every owner has set this frame's position and volume
    public sealed class RoutedLoop : MonoBehaviour
    {
        /// <summary>Where the sound really is.</summary>
        public Vector3 At;
        /// <summary>The owner's volume before routing.</summary>
        public float Volume;
        public float Pitch = 1f;
        /// <summary>Which volume slider this loop follows.</summary>
        public SoundBus Bus = SoundBus.World;
        /// <summary>Caption while it is heard (refreshed while it plays), or None.</summary>
        public SoundCaption Caption;

        public AudioSource Source { get; private set; }
        private AudioLowPassFilter _filter;
        private float _next, _gain, _cutoff = 22000f, _gainTarget, _cutoffTarget = 22000f;
        private Vector3 _offset;
        private bool _audible, _placed;

        /// <summary>A routed loop under <paramref name="parent"/>, silent until given a volume.</summary>
        public static RoutedLoop Create(string name, Transform parent, AudioClip clip, float minDistance, float maxDistance)
        {
            AudioSource s = Synth.Loop(name, parent, clip, minDistance, maxDistance);
            var r = s.gameObject.AddComponent<RoutedLoop>();
            r.Source = s;
            r._filter = s.gameObject.AddComponent<AudioLowPassFilter>();
            r._filter.lowpassResonanceQ = 1f;
            r._filter.enabled = false;
            return r;
        }

        private void LateUpdate()
        {
            if (Source == null) return;
            float now = Time.unscaledTime;
            if (Volume <= 0.0005f) { Source.volume = 0f; _placed = false; return; }
            if (!_placed || now >= _next)
            {
                _next = now + 0.1f;
                Vector3 p = At;
                _audible = SoundRouter.Place(ref p, Source.maxDistance, 0.05f, out _gainTarget, out _cutoffTarget);
                if (!_audible) _gainTarget = 0f;
                // Keep the offset from the true spot to the heard spot, so a moving sound moves smoothly between routes.
                _offset = p - At;
                if (!_placed) { _gain = _gainTarget; _cutoff = _cutoffTarget; }
                _placed = true;
            }
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f);
            _gain += (_gainTarget - _gain) * k;
            _cutoff += (_cutoffTarget - _cutoff) * k;
            transform.position = At + _offset;
            Source.volume = Volume * _gain * SoundMix.Gain(Bus);
            Source.spread = SoundMix.Spread;
            if (Caption != SoundCaption.None && _audible && Source.volume > 0.02f)
                SoundCaptions.Post(Caption, transform.position, _cutoff < (float)SoundRouter.Config.OpenCutoffHz - 1f, Source.volume);
            Source.pitch = Pitch;
            SoundRouter.ApplyCutoff(_filter, _cutoff);
        }
    }
}
