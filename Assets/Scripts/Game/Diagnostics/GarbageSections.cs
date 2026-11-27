using System;
using System.Text;
using UnityEngine;

namespace Sculpt.Game.Diagnostics
{
    /// <summary>
    /// Development builds with -profgc: how much managed garbage each section of a frame makes, logged every 10 s.
    /// Unity's Mono has no per-thread allocation counter, so this reads heap growth between a section's start and end
    /// (a collection inside a section reads as nothing; nested sections count in both).
    /// </summary>
    public static class GarbageSections
    {
        public static bool On { get; private set; }
        private const int Max = 32;
        private static readonly string[] Names = new string[Max];
        private static readonly long[] At = new long[Max], Bytes = new long[Max];
        private static int _count;
        private static long _frameStart = -1, _frameBytes;
        private static int _frames;
        private static float _since;

        public static void Init()
        {
            On = Debug.isDebugBuild && Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-profgc", StringComparison.OrdinalIgnoreCase));
        }

        public static int Register(string name)
        {
            if (_count >= Max) return Max - 1;
            Names[_count] = name;
            return _count++;
        }

        public static void Begin(int id) { if (On) At[id] = GC.GetTotalMemory(false); }

        public static void End(int id)
        {
            if (!On) return;
            long d = GC.GetTotalMemory(false) - At[id];
            if (d > 0) Bytes[id] += d;
        }

        /// <summary>Call once per frame (start of the frame).</summary>
        public static void Frame()
        {
            if (!On) return;
            long now = GC.GetTotalMemory(false);
            if (_frameStart >= 0 && now > _frameStart) _frameBytes += now - _frameStart;
            _frameStart = now;
            _frames++;
            _since += Time.unscaledDeltaTime;
            if (_since < 10f) return;
            var sb = new StringBuilder("[SCULPT] Garbage KB per frame: whole frame ");
            sb.Append((_frameBytes / 1024.0 / _frames).ToString("0.00"));
            for (int i = 0; i < _count; i++)
            {
                if (Bytes[i] == 0) continue;
                sb.Append(", ").Append(Names[i]).Append(' ').Append((Bytes[i] / 1024.0 / _frames).ToString("0.00"));
                Bytes[i] = 0;
            }
            Debug.Log(sb.ToString());
            _frameBytes = 0;
            _frames = 0;
            _since = 0;
        }
    }
}
