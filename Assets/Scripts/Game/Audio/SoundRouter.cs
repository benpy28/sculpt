using System;
using System.Diagnostics;
using Sculpt.Core;
using Sculpt.Core.Audio;
using UnityEngine;

namespace Sculpt.Game.Audio
{
    /// <summary>
    /// Places every positional sound by the way it really reaches your ears (Core <see cref="SoundPaths"/>): from the
    /// doorway, hole or wall top it comes round, as far away as the way round is long, duller per corner; or through the
    /// wall from its real direction, quiet and low; plus a light head shadow so behind sounds different from in front.
    /// <see cref="Synth.Play"/> and <see cref="RoutedLoop"/> call it; nothing else needs to.
    /// One air grid per client world, built when the client is made (about 0.1 s on the duel map), updated where a wall
    /// changes (<see cref="Invalidate"/>, from the tile view).
    /// </summary>
    public static class SoundRouter
    {
        public static readonly SoundConfig Config = new SoundConfig();
        private static SoundPaths _paths;
        private static Transform _listener;
        private static int _frame = -1, _searches;

        /// <summary>Time the last air grid took to build (ms). Dev HUD and the benchmark report can show it.</summary>
        public static double LastBuildMs { get; private set; }
        /// <summary>The last route worked out (dev overlay).</summary>
        public static SoundRoute LastRoute { get; private set; }
        /// <summary>Searches for a way round run this frame.</summary>
        public static int SearchesThisFrame => _frame == Time.frameCount ? _searches : 0;

        public static bool Active => _paths != null && _listener != null;

        /// <summary>Routes sounds through <paramref name="world"/> to <paramref name="listener"/> (the camera's ears).
        /// Call again whenever the client world is replaced. Null switches routing off.</summary>
        public static void Bind(ITerrain world, Transform listener)
        {
            _listener = listener;
            if (world == null) { _paths = null; return; }
            var sw = Stopwatch.StartNew();
            _paths = new SoundPaths(world, Config);
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Something in <paramref name="region"/> changed shape (a tile broke, healed or lost cells; a door moved).</summary>
        public static void Invalidate(Aabb region) => _paths?.Invalidate(region);

        /// <summary>
        /// Moves <paramref name="at"/> to where the sound should be played and gives the extra gain and the low-pass
        /// cutoff. False: it can't be heard at all (out of range every way, or walled off by too much solid).
        /// </summary>
        /// <param name="range">How far the sound carries (its fall-off distance).</param>
        /// <param name="sourceRadius">How deep in a solid the sound may sit (a door or wall tile sounds from its middle).</param>
        public static bool Place(ref Vector3 at, float range, float sourceRadius, out float gain, out float cutoff)
        {
            gain = 1f;
            cutoff = (float)Config.OpenCutoffHz;
            if (!Active) return true;
            if (_frame != Time.frameCount) { _frame = Time.frameCount; _searches = 0; }
            Vec3d ear = _listener.position.ToCore();
            long before = _paths.Searches;
            SoundRoute r = _paths.Route(ear, at.ToCore(), range, Time.unscaledTimeAsDouble, sourceRadius, _searches < Config.MaxSearchesPerFrame);
            if (_paths.Searches != before) _searches++;
            LastRoute = r;
            if (!r.Audible) return false;
            SoundPaths.HeadShadow(_listener.forward.ToCore(), r.ArrivesFrom, Config, out double shadowDb, out double shadowHz);
            gain = (float)Math.Pow(10, (r.GainDb + shadowDb) / 20);
            cutoff = (float)Math.Min(r.CutoffHz, shadowHz);
            at = r.Apparent.ToUnity();
            return true;
        }

        /// <summary>Sets a voice's low-pass for <paramref name="cutoff"/> (off when it is fully open, so open sounds cost
        /// nothing extra).</summary>
        public static void ApplyCutoff(AudioLowPassFilter f, float cutoff)
        {
            if (f == null) return;
            bool on = cutoff < (float)Config.OpenCutoffHz - 1f;
            if (f.enabled != on) f.enabled = on;
            if (on) f.cutoffFrequency = cutoff;
        }
    }
}
