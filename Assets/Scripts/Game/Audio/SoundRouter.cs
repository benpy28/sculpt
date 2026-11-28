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

        /// <summary>The ears sounds are routed to (the camera), or null.</summary>
        public static Transform Listener => _listener;

        /// <summary>Routes sounds through <paramref name="world"/> to <paramref name="listener"/> (the camera's ears).
        /// Call again whenever the client world is replaced. Null switches routing off.</summary>
        public static void Bind(ITerrain world, Transform listener)
        {
            _listener = listener;
            _paths = null;
            _pending.Clear();
            int bind = ++_bind;
            if (world == null) return;
            // Built on a worker (seconds on the battle royale's village map, which used to hold up the load); sounds are
            // heard straight until it is ready, and shape changes in the meantime are applied when it is.
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var paths = new SoundPaths(world, Config);
                double ms = sw.Elapsed.TotalMilliseconds;
                lock (_pending)
                {
                    if (bind != _bind) return; // bound again since: this world is gone
                    foreach (Aabb r in _pending) paths.Invalidate(r);
                    _pending.Clear();
                    _ready = paths;
                    LastBuildMs = ms;
                }
            });
        }

        private static readonly System.Collections.Generic.List<Aabb> _pending = new System.Collections.Generic.List<Aabb>();
        private static volatile SoundPaths _ready;
        private static int _bind;

        /// <summary>Waits (up to <paramref name="timeoutMs"/>) for the air grid the last <see cref="Bind"/> started, and
        /// takes it (the loading screen does this).</summary>
        public static void WaitReady(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (_paths == null && _listener != null && sw.ElapsedMilliseconds < timeoutMs)
            {
                Adopt();
                if (_paths == null) System.Threading.Thread.Sleep(5);
            }
        }

        /// <summary>Takes the air grid once the worker has built it (main thread).</summary>
        private static void Adopt()
        {
            if (_ready == null) return;
            lock (_pending) { _paths = _ready; _ready = null; }
        }

        /// <summary>Something in <paramref name="region"/> changed shape (a tile broke, healed or lost cells; a door moved).</summary>
        public static void Invalidate(Aabb region)
        {
            using (MkInvalidate.Auto())
            {
                Adopt();
                if (_paths != null) { _paths.Invalidate(region); return; }
                lock (_pending) { if (_ready != null) _ready.Invalidate(region); else _pending.Add(region); }
            }
        }

        // Named sections for -benchmarkmarkers and the Unity profiler (the busy-fight cost of routing).
        private static readonly Unity.Profiling.ProfilerMarker MkPlace = new Unity.Profiling.ProfilerMarker("SCULPT.SoundRoute"),
            MkInvalidate = new Unity.Profiling.ProfilerMarker("SCULPT.SoundInvalidate");

        /// <summary>
        /// Moves <paramref name="at"/> to where the sound should be played and gives the extra gain and the low-pass
        /// cutoff. False: it can't be heard at all (out of range every way, or walled off by too much solid).
        /// </summary>
        /// <param name="range">How far the sound carries (its fall-off distance).</param>
        /// <param name="sourceRadius">How deep in a solid the sound may sit (a door or wall tile sounds from its middle).</param>
        /// <param name="muffled">The route dulled it (through walls or round a corner); the head shadow alone never counts.</param>
        public static bool Place(ref Vector3 at, float range, float sourceRadius, out float gain, out float cutoff, out bool muffled)
        {
            gain = 1f;
            cutoff = (float)Config.OpenCutoffHz;
            muffled = false;
            Adopt();
            if (!Active) return true;
            using var mk = MkPlace.Auto();
            if (_frame != Time.frameCount) { _frame = Time.frameCount; _searches = 0; }
            Vec3d ear = _listener.position.ToCore();
            long before = _paths.Searches;
            SoundRoute r = _paths.Route(ear, at.ToCore(), range, Time.unscaledTimeAsDouble, sourceRadius, _searches < Config.MaxSearchesPerFrame);
            if (_paths.Searches != before) _searches++;
            LastRoute = r;
            if (!r.Audible) return false;
            // Settings > Audio > front/back cue: scales how much a sound behind you is dulled (1 = as designed).
            SoundPaths.Hear(r, _listener.forward.ToCore(), Sculpt.Game.Settings.ClientSettings.Current.HeadShadow, Config,
                            out double db, out double hz, out muffled);
            gain = (float)Math.Pow(10, db / 20);
            cutoff = SoundMix.Cutoff((float)hz, (float)Config.OpenCutoffHz);
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
