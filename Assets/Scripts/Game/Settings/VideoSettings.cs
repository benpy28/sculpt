using System;
using System.Globalization;
using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>
    /// Frame pacing, rendering cost and performance overlay settings, saved in PlayerPrefs and edited in the Esc menu
    /// (Video and latency). One shared instance (<see cref="Current"/>): the render profile re-applies whenever
    /// <see cref="Version"/> changes. Defaults are the competitive ones: V-Sync off, no cap, one queued frame, lean
    /// rendering.
    /// </summary>
    public sealed class VideoSettings
    {
        /// <summary>Frame cap choice meaning "the display's refresh rate minus 3" (keeps a variable-refresh display in its range).</summary>
        public const int CapBelowRefresh = -1;
        public const int CapUnlimited = 0;
        public const float MinRenderScale = 0.5f, MaxRenderScale = 1f;

        /// <summary>The cap choices offered in the menu, in order.</summary>
        public static readonly int[] CapChoices = { CapUnlimited, CapBelowRefresh, 60, 120, 144, 165, 240, 360, 500 };

        private static VideoSettings _current;
        public static VideoSettings Current => _current ??= Load();

        /// <summary>Wait for the display's refresh before showing a frame. Off is the lowest latency (tearing unless the display is variable-refresh).</summary>
        public bool VSync;
        /// <summary>Frames per second limit: <see cref="CapUnlimited"/>, <see cref="CapBelowRefresh"/>, or a number. Ignored while V-Sync is on.</summary>
        public int FpsCap = CapUnlimited;
        /// <summary>Let the graphics driver queue at most one finished frame instead of two (less delay when the graphics card is the limit).</summary>
        public bool LowLatency = true;
        /// <summary>Skip the render pipeline work the game's unlit look never uses (HDR target, depth and colour copies, shadow pass, ambient occlusion).</summary>
        public bool LeanRendering = true;
        /// <summary>Fraction of the screen resolution the 3D view is drawn at (the HUD stays sharp).</summary>
        public float RenderScale = 1f;
        /// <summary>Small frame rate readout in the top right corner.</summary>
        public bool ShowFps;
        /// <summary>The per-click latency breakdown and frame-time panel.</summary>
        public bool ShowLatency;

        /// <summary>Bumped by <see cref="Changed"/>; whoever applies settings compares it with the version it last applied.</summary>
        public int Version { get; private set; } = 1;

        public void Changed() => Version++;

        /// <summary>The frames per second the cap setting means on a display with this refresh rate (0 = no limit).</summary>
        public int CapFor(double refreshHz)
        {
            if (FpsCap == CapBelowRefresh) return refreshHz >= 30 ? Math.Max(30, (int)Math.Round(refreshHz) - 3) : 0;
            return FpsCap > 0 ? FpsCap : 0;
        }

        public static string CapName(int cap) => cap == CapUnlimited ? "Unlimited" : cap == CapBelowRefresh ? "Display - 3" : cap.ToString(CultureInfo.InvariantCulture);

        public VideoSettings Clamped()
        {
            RenderScale = Mathf.Clamp(float.IsNaN(RenderScale) ? 1f : RenderScale, MinRenderScale, MaxRenderScale);
            if (FpsCap < CapBelowRefresh) FpsCap = CapUnlimited;
            if (FpsCap > 1000) FpsCap = 1000;
            if (FpsCap > 0 && FpsCap < 30) FpsCap = 30;
            return this;
        }

        public static VideoSettings Load()
        {
            var v = new VideoSettings
            {
                VSync = PlayerPrefs.GetInt("sculpt.video.vsync", 0) == 1,
                FpsCap = PlayerPrefs.GetInt("sculpt.video.fpscap", CapUnlimited),
                LowLatency = PlayerPrefs.GetInt("sculpt.video.lowlatency", 1) == 1,
                LeanRendering = PlayerPrefs.GetInt("sculpt.video.lean", 1) == 1,
                RenderScale = PlayerPrefs.GetFloat("sculpt.video.renderscale", 1f),
                ShowFps = PlayerPrefs.GetInt("sculpt.video.showfps", 0) == 1,
                ShowLatency = PlayerPrefs.GetInt("sculpt.video.showlatency", 0) == 1,
            }.Clamped();
            v.ApplyCommandLine(Environment.GetCommandLineArgs());
            return v;
        }

        public void Save()
        {
            PlayerPrefs.SetInt("sculpt.video.vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.video.fpscap", FpsCap);
            PlayerPrefs.SetInt("sculpt.video.lowlatency", LowLatency ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.video.lean", LeanRendering ? 1 : 0);
            PlayerPrefs.SetFloat("sculpt.video.renderscale", RenderScale);
            PlayerPrefs.SetInt("sculpt.video.showfps", ShowFps ? 1 : 0);
            PlayerPrefs.SetInt("sculpt.video.showlatency", ShowLatency ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// One-run overrides, never saved (benchmarks and A/B tests): -vsync 0|1, -fpscap N (0 = unlimited, -1 = display - 3),
        /// -lowlatency 0|1, -lean 0|1, -renderscale 0.5..1, -showfps 0|1, -showlatency 0|1.
        /// </summary>
        public void ApplyCommandLine(string[] args)
        {
            if (args == null) return;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant(), next = args[i + 1];
                if (a == "-vsync" && Flag(next, out bool b)) VSync = b;
                else if (a == "-lowlatency" && Flag(next, out b)) LowLatency = b;
                else if (a == "-lean" && Flag(next, out b)) LeanRendering = b;
                else if (a == "-showfps" && Flag(next, out b)) ShowFps = b;
                else if (a == "-showlatency" && Flag(next, out b)) ShowLatency = b;
                else if (a == "-fpscap" && int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap)) FpsCap = cap;
                else if (a == "-renderscale" && float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) RenderScale = s;
            }
            Clamped();
        }

        private static bool Flag(string s, out bool value)
        {
            value = s == "1" || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            return value || s == "0" || string.Equals(s, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
