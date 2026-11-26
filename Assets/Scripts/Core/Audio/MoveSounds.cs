using System;
using System.Collections.Generic;

namespace Sculpt.Core.Audio
{
    /// <summary>
    /// How loud and how far movement carries. Your own and everyone else's footsteps use the same clock and numbers, so
    /// what you hear of yourself is what others hear of you. Ranges are route lengths (<see cref="SoundPaths"/>): round a
    /// corner the sound has further to go.
    /// </summary>
    public sealed class MoveSoundConfig
    {
        /// <summary>Below this ground speed (m/s) no steps.</summary>
        public double MinStepSpeed = 0.8;
        /// <summary>Stride (metres per step) when crouch-walking, and from a jog to a full sprint.</summary>
        public double CrouchStride = 0.9, JogStride = 1.5, SprintStride = 2.3;
        /// <summary>Speeds (m/s) the jog and sprint stride and loudness are set at.</summary>
        public double JogSpeed = 3, SprintSpeed = 10;
        /// <summary>After landing or a slide the first step comes after this much of a stride (metres).</summary>
        public double FirstStepAfter = 0.6;
        /// <summary>Step loudness (0..1, before distance): crouch-walk, jog, sprint.</summary>
        public double CrouchVolume = 0.18, JogVolume = 0.35, SprintVolume = 0.6;
        /// <summary>How far a step carries (metres of route): crouch-walk, jog, sprint. Crouch-walking is the quiet way to
        /// move: heard only up close, never silent.</summary>
        public double CrouchRange = 7, JogRange = 24, SprintRange = 34;
        /// <summary>Jump push-off, slide scrape, hands on a ledge (vault, mantle): range in metres.</summary>
        public double JumpRange = 20, SlideRange = 26, GrabRange = 16;
        /// <summary>Landing: range grows with the fall speed, from a hop to a big drop.</summary>
        public double LandRangeMin = 18, LandRangeMax = 40;
        /// <summary>Fall speed (m/s, downward) counted as a hop and as a big drop (for landing loudness and range).</summary>
        public double HopFallSpeed = 3, BigFallSpeed = 14;
        /// <summary>Teammates' movement is played this much quieter (their steps must not hide an enemy's).</summary>
        public double TeammateGain = 0.5;
        /// <summary>Over this much movement in one update (metres) it was a respawn or a snap: no step.</summary>
        public double TeleportDistance = 2.0;
    }

    /// <summary>One stride clock: steps by distance walked on the ground, quiet when crouched.</summary>
    public sealed class FootstepClock
    {
        public double Stride { get; private set; }

        /// <summary>
        /// Advances by <paramref name="moved"/> metres (horizontal) at <paramref name="speed"/> m/s. Returns true when a step
        /// falls, with its loudness and range. Not on the ground (air, slide, vault, mantle): no steps, and the first step
        /// after comes early.
        /// </summary>
        public bool Advance(MoveSoundConfig c, bool onGround, bool crouched, double moved, double speed, out double volume, out double range)
        {
            volume = 0;
            range = 0;
            if (moved > c.TeleportDistance) moved = 0;
            if (!onGround) { Stride = c.FirstStepAfter; return false; }
            if (speed <= c.MinStepSpeed) return false;
            Stride += moved;
            double k = Clamp01((speed - c.JogSpeed) / (c.SprintSpeed - c.JogSpeed));
            double len = crouched ? c.CrouchStride : c.JogStride + (c.SprintStride - c.JogStride) * k;
            if (Stride < len) return false;
            Stride -= len;
            if (Stride > len) Stride = 0; // a long frame never fires a burst of steps
            volume = crouched ? c.CrouchVolume : c.JogVolume + (c.SprintVolume - c.JogVolume) * k;
            range = crouched ? c.CrouchRange : c.JogRange + (c.SprintRange - c.JogRange) * k;
            return true;
        }

        /// <summary>The first step comes soon (spawned, first seen).</summary>
        public void Reset(MoveSoundConfig c) => Stride = c.FirstStepAfter;

        /// <summary>Just landed: the landing thud stands for this step, the next comes a whole stride on.</summary>
        public void Landed() => Stride = 0;

        internal static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }

    public enum MoveSoundType : byte { Step, Jump, Land, SlideStart, SlideEnd, Grab }

    /// <summary>A movement sound someone else made, worked out from what the client draws of them.</summary>
    public struct MoveSound
    {
        public MoveSoundType Type;
        /// <summary>Loudness 0..1 before distance.</summary>
        public double Volume;
        /// <summary>How far it carries (metres of route).</summary>
        public double Range;
        /// <summary>Their feet.</summary>
        public Vec3d At;
        /// <summary>Landing: 0 (a hop) .. 1 (a big drop).</summary>
        public double Weight;
    }

    /// <summary>
    /// Other players' movement sounds from the poses the client already draws (no extra network data): steps by stride on
    /// the ground, a push-off when they leave the ground going up, a landing thud by how fast they were falling, slide
    /// start and stop, and a grab when they vault or mantle. Only from real snapshots: a pose the client is guessing
    /// (<see cref="RemoteView.Extrapolated"/>) makes no sound, so nothing is ever heard that didn't happen.
    /// </summary>
    public sealed class RemoteMoveSounds
    {
        public readonly MoveSoundConfig Config;
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly List<int> _stale = new List<int>();

        private sealed class Track
        {
            public Vec3d Last;
            public MoveMode Mode;
            public double MaxFall;
            public bool Dead;
            public bool Seen;
            public int Frame;
            public readonly FootstepClock Clock = new FootstepClock();
        }

        private int _frame;

        public RemoteMoveSounds(MoveSoundConfig config = null) { Config = config ?? new MoveSoundConfig(); }

        /// <summary>Call once per drawn frame before <see cref="Update"/> calls; then <see cref="EndFrame"/>.</summary>
        public void BeginFrame() => _frame++;

        /// <summary>Feeds one remote player's drawn pose for this frame; appends the sounds it made to <paramref name="into"/>.</summary>
        /// <param name="feet">Where their feet are drawn (the pose position).</param>
        public void Update(int id, Vec3d feet, Vec3d velocity, MoveMode mode, bool crouched, bool dead, bool extrapolated, List<MoveSound> into)
        {
            if (!_tracks.TryGetValue(id, out Track t))
            {
                t = new Track();
                _tracks[id] = t;
            }
            t.Frame = _frame;
            MoveSoundConfig c = Config;
            if (dead || !t.Seen || t.Dead)
            {
                // First sight, death or respawn: take the pose, make no sound.
                t.Seen = true;
                t.Dead = dead;
                t.Last = feet;
                t.Mode = mode;
                t.MaxFall = 0;
                t.Clock.Reset(c);
                return;
            }
            Vec3d d = feet - t.Last;
            double moved = Math.Sqrt(d.X * d.X + d.Z * d.Z);
            bool jumped = d.LengthSquared > c.TeleportDistance * c.TeleportDistance;
            t.Last = feet;
            MoveMode was = t.Mode;
            t.Mode = mode;
            if (jumped) { t.MaxFall = 0; t.Clock.Reset(c); return; } // respawn or a snap
            if (extrapolated) return;

            if (mode == MoveMode.Air) t.MaxFall = Math.Max(t.MaxFall, -velocity.Y);

            if (was != mode)
            {
                if (mode == MoveMode.Air && (was == MoveMode.Ground || was == MoveMode.Slide) && velocity.Y > 1.0)
                    into.Add(new MoveSound { Type = MoveSoundType.Jump, Volume = 0.4, Range = c.JumpRange, At = feet });
                if (was == MoveMode.Air && (mode == MoveMode.Ground || mode == MoveMode.Slide))
                {
                    double w = FootstepClock.Clamp01((t.MaxFall - c.HopFallSpeed) / (c.BigFallSpeed - c.HopFallSpeed));
                    into.Add(new MoveSound
                    {
                        Type = MoveSoundType.Land, Weight = w, Volume = 0.3 + 0.7 * w,
                        Range = c.LandRangeMin + (c.LandRangeMax - c.LandRangeMin) * w, At = feet,
                    });
                    t.MaxFall = 0;
                    t.Clock.Landed();
                }
                if (mode == MoveMode.Slide)
                    into.Add(new MoveSound { Type = MoveSoundType.SlideStart, Volume = 0.55, Range = c.SlideRange, At = feet });
                if (was == MoveMode.Slide)
                    into.Add(new MoveSound { Type = MoveSoundType.SlideEnd, Volume = 0, Range = c.SlideRange, At = feet });
                if (mode == MoveMode.Vault || mode == MoveMode.Mantle)
                    into.Add(new MoveSound { Type = MoveSoundType.Grab, Volume = 0.5, Range = c.GrabRange, At = feet });
            }

            double speed = Math.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
            if (t.Clock.Advance(c, mode == MoveMode.Ground, crouched, moved, speed, out double vol, out double range))
                into.Add(new MoveSound { Type = MoveSoundType.Step, Volume = vol, Range = range, At = feet });
        }

        /// <summary>Forgets players not fed this frame (left the game).</summary>
        public void EndFrame()
        {
            _stale.Clear();
            foreach (var kv in _tracks) if (kv.Value.Frame != _frame) _stale.Add(kv.Key);
            foreach (int id in _stale) _tracks.Remove(id);
        }

        public int Tracked => _tracks.Count;
    }
}
