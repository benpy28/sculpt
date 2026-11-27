using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Audio;
using Sculpt.Core.Net;
using Sculpt.Game.Audio;
using Sculpt.Game.Net;
using UnityEngine;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// Everyone else's movement, by ear (Ben 2026-11-26, directional sound pass): footsteps by stride (quiet and short
    /// range when crouch-walking), jump push-offs, landing thuds by how far they fell, the slide scrape while they slide,
    /// and hands on a ledge for vaults and mantles. Same clips, clock and numbers as your own
    /// (<see cref="MovementAudio"/>). Worked out from the poses already drawn (Core <see cref="RemoteMoveSounds"/>), so it
    /// needs no network data and plays the moment a pose is drawn; a guessed pose (snapshots ran dry) makes no sound.
    /// Every sound is routed (<see cref="SoundRouter"/>): from the doorway or hole it comes through, or muffled through
    /// the wall. Teammates are half as loud so their steps never hide an enemy's.
    /// </summary>
    public sealed class RemoteMovementAudio : MonoBehaviour
    {
        private NetSession _session;
        private RemoteMoveSounds _tracker;
        private readonly List<int> _ids = new List<int>();
        private readonly List<MoveSound> _sounds = new List<MoveSound>();
        private readonly Dictionary<int, RoutedLoop> _slides = new Dictionary<int, RoutedLoop>();
        private readonly List<int> _gone = new List<int>();
        private int _stepIndex;

        /// <summary>Steps sound from just above the feet, clear of the floor they stand on.</summary>
        private const float StepHeight = 0.3f;

        public void Init(NetSession session)
        {
            _session = session;
            _tracker = new RemoteMoveSounds(MovementAudio.Sounds);
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            NetClient net = _session.Client;
            if (net == null) return;
            net.GetRemoteIds(_ids);
            _tracker.BeginFrame();
            MoveSoundConfig c = _tracker.Config;
            foreach (int id in _ids)
            {
                if (!net.TryGetRemote(id, out RemoteView v)) continue;
                _sounds.Clear();
                _tracker.Update(id, v.Position, v.Velocity, v.Mode, v.Crouched, v.Dead, v.Extrapolated, _sounds);
                float team = v.Team > 0 && v.Team == net.Team ? (float)c.TeammateGain : 1f;
                foreach (MoveSound s in _sounds) Play(s, team);

                // Slide scrape: a loop per player while they slide, louder and brighter with speed.
                bool sliding = v.Mode == MoveMode.Slide && !v.Dead && !v.Extrapolated;
                if (!_slides.TryGetValue(id, out RoutedLoop loop) || loop == null)
                {
                    if (!sliding) continue;
                    loop = RoutedLoop.Create("Remote Slide " + id, transform, MovementAudio.SlideLoop, 1.5f, (float)c.SlideRange);
                    loop.Source.time = Random.Range(0f, loop.Source.clip.length * 0.9f);
                    loop.Bus = SoundBus.Movement;
                    loop.Caption = SoundCaption.Sliding;
                    _slides[id] = loop;
                }
                float speed = (float)System.Math.Sqrt(v.Velocity.X * v.Velocity.X + v.Velocity.Z * v.Velocity.Z);
                float target = sliding ? Mathf.Clamp01(speed / 14f) * 0.55f * team : 0f;
                loop.Volume = Mathf.MoveTowards(loop.Volume, target, Time.unscaledDeltaTime * (sliding ? 8f : 5f));
                loop.Pitch = 0.75f + 0.5f * Mathf.Clamp01(speed / 18f);
                loop.At = v.Position.ToUnity() + Vector3.up * StepHeight;
            }
            _tracker.EndFrame();

            // Players who left: their slide loop goes too.
            _gone.Clear();
            foreach (var kv in _slides) if (!_ids.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (int id in _gone)
            {
                if (_slides[id] != null) Destroy(_slides[id].gameObject);
                _slides.Remove(id);
            }
        }

        private void Play(in MoveSound s, float team)
        {
            Vector3 at = s.At.ToUnity() + Vector3.up * StepHeight;
            float range = (float)s.Range;
            float vol = (float)s.Volume * team;
            bool enemy = team >= 0.99f; // captions only for enemies (teammates are drawn on the minimap and HUD)
            const SoundBus mv = SoundBus.Movement;
            switch (s.Type)
            {
                case MoveSoundType.Step:
                    AudioClip[] steps = MovementAudio.Steps;
                    _stepIndex = (_stepIndex + 1 + Random.Range(0, 3)) % steps.Length;
                    Synth.Play(steps[_stepIndex], at, vol, Random.Range(0.93f, 1.07f), true, 1.5f, range, bus: mv,
                               caption: !enemy ? SoundCaption.None : range > 28f ? SoundCaption.Running : SoundCaption.Footsteps);
                    break;
                case MoveSoundType.Jump:
                    Synth.Play(MovementAudio.Jump, at, vol, Random.Range(0.95f, 1.05f), true, 1.5f, range, bus: mv, caption: enemy ? SoundCaption.Jump : SoundCaption.None);
                    break;
                case MoveSoundType.Land:
                    Synth.Play(MovementAudio.Land, at, vol, Mathf.Lerp(1.1f, 0.85f, (float)s.Weight), true, 2f, range, bus: mv, caption: enemy ? SoundCaption.Landing : SoundCaption.None);
                    break;
                case MoveSoundType.Grab:
                    Synth.Play(MovementAudio.Grab, at, vol, Random.Range(0.95f, 1.05f), true, 1.5f, range, bus: mv, caption: enemy ? SoundCaption.Climbing : SoundCaption.None);
                    break;
                // Slide start and end drive the loop above.
            }
        }

        private void OnDestroy()
        {
            foreach (var kv in _slides) if (kv.Value != null) Destroy(kv.Value.gameObject);
            _slides.Clear();
        }
    }
}
