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
        /// <summary>A small network readout: ping, corrections, snapshot data rate, server input lead.</summary>
        public bool ShowNetStats;
        /// <summary>MSAA samples for the game camera: 1 = off (default, competitive), 2, 4 or 8. Smooths polygon edges.</summary>
        public int Msaa = 1;
        /// <summary>Picture effects: post-processing, shadows, ambient occlusion, lighting, fog (see <see cref="Sculpt.Core.Settings.LookValues"/>).
        /// Defaults are the clean competitive picture.</summary>
        public Sculpt.Core.Settings.LookValues Look = Sculpt.Core.Settings.LookValues.Clean;

        /// <summary>The preset these values are exactly (or Custom).</summary>
        public Sculpt.Core.Settings.VideoPreset Preset => Sculpt.Core.Settings.DisplayPresets.Detect(Values);

        public Sculpt.Core.Settings.VideoValues Values
        {
            get => new Sculpt.Core.Settings.VideoValues { VSync = VSync, FpsCap = FpsCap, LowLatency = LowLatency, LeanRendering = LeanRendering, RenderScale = RenderScale, Msaa = Msaa, Look = Look };
            set { VSync = value.VSync; FpsCap = value.FpsCap; LowLatency = value.LowLatency; LeanRendering = value.LeanRendering; RenderScale = value.RenderScale; Msaa = Sculpt.Core.Settings.DisplayPresets.NormMsaa(value.Msaa); Look = value.Look; _cmd = null; Clamped(); Changed(); }
        }

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
            Msaa = Sculpt.Core.Settings.DisplayPresets.NormMsaa(Msaa);
            Look = Look.Clamped();
            return this;
        }

        private static readonly PrefTable<VideoSettings> Table = new PrefTable<VideoSettings>()
            .Bool(SettingsGroup.Video, "sculpt.video.vsync", false, v => v.VSync, (v, b) => v.VSync = b)
            .Int(SettingsGroup.Video, "sculpt.video.fpscap", CapUnlimited, CapBelowRefresh, 1000, v => v.FpsCap, (v, i) => v.FpsCap = i)
            .Bool(SettingsGroup.Latency, "sculpt.video.lowlatency", true, v => v.LowLatency, (v, b) => v.LowLatency = b)
            .Bool(SettingsGroup.Video, "sculpt.video.lean", true, v => v.LeanRendering, (v, b) => v.LeanRendering = b)
            .Float(SettingsGroup.Video, "sculpt.video.renderscale", 1f, MinRenderScale, MaxRenderScale, v => v.RenderScale, (v, f) => v.RenderScale = f)
            .Int(SettingsGroup.Video, "sculpt.video.msaa", 1, 1, 8, v => v.Msaa, (v, i) => v.Msaa = i)
            // Picture effects (defaults = the clean competitive picture, so nobody's look changes until they pick it).
            .Int(SettingsGroup.Video, "sculpt.look.postaa", 0, 0, 3, v => v.Look.PostAa, (v, i) => v.Look.PostAa = i)
            .Int(SettingsGroup.Video, "sculpt.look.tonemap", 0, 0, 1, v => v.Look.Tonemap, (v, i) => v.Look.Tonemap = i)
            .Int(SettingsGroup.Video, "sculpt.look.grading", 0, 0, 2, v => v.Look.Grading, (v, i) => v.Look.Grading = i)
            .Int(SettingsGroup.Video, "sculpt.look.bloom", 0, 0, 2, v => v.Look.Bloom, (v, i) => v.Look.Bloom = i)
            .Int(SettingsGroup.Video, "sculpt.look.ao", 0, 0, 2, v => v.Look.Ao, (v, i) => v.Look.Ao = i)
            .Int(SettingsGroup.Video, "sculpt.look.shadows", 0, 0, 3, v => v.Look.Shadows, (v, i) => v.Look.Shadows = i)
            .Int(SettingsGroup.Video, "sculpt.look.lighting", 0, 0, 1, v => v.Look.Lighting, (v, i) => v.Look.Lighting = i)
            .Int(SettingsGroup.Video, "sculpt.look.fog", 1, 1, 2, v => v.Look.Fog, (v, i) => v.Look.Fog = i)
            .Bool(SettingsGroup.Video, "sculpt.look.shafts", false, v => v.Look.Shafts, (v, b) => v.Look.Shafts = b)
            .Int(SettingsGroup.Video, "sculpt.look.dof", 0, 0, 1, v => v.Look.Dof, (v, i) => v.Look.Dof = i)
            .Int(SettingsGroup.Video, "sculpt.look.motionblur", 0, 0, 2, v => v.Look.MotionBlur, (v, i) => v.Look.MotionBlur = i)
            .Float(SettingsGroup.Video, "sculpt.look.vignette", 0f, 0f, 1f, v => v.Look.Vignette, (v, f) => v.Look.Vignette = f)
            .Float(SettingsGroup.Video, "sculpt.look.grain", 0f, 0f, 1f, v => v.Look.Grain, (v, f) => v.Look.Grain = f)
            .Float(SettingsGroup.Video, "sculpt.look.sharpen", 0f, 0f, 1f, v => v.Look.Sharpen, (v, f) => v.Look.Sharpen = f)
            .Int(SettingsGroup.Video, "sculpt.look.detail", 0, 0, 1, v => v.Look.Detail, (v, i) => v.Look.Detail = i)
            .Int(SettingsGroup.Video, "sculpt.look.grass", 0, 0, 2, v => v.Look.Grass, (v, i) => v.Look.Grass = i)
            .Bool(SettingsGroup.Latency, "sculpt.video.showfps", false, v => v.ShowFps, (v, b) => v.ShowFps = b)
            .Bool(SettingsGroup.Latency, "sculpt.video.showlatency", false, v => v.ShowLatency, (v, b) => v.ShowLatency = b)
            .Bool(SettingsGroup.Latency, "sculpt.video.shownet", false, v => v.ShowNetStats, (v, b) => v.ShowNetStats = b);

        public static VideoSettings Load()
        {
            var v = new VideoSettings();
            Table.Load(v);
            v.Clamped();
            v.ApplyCommandLine(Environment.GetCommandLineArgs());
            return v;
        }

        /// <summary>Saves the settings. A value still at a one-run command-line override (-vsync, -fpscap, -lean, ...) is
        /// saved as what was stored before the override, so the override never sticks; one the player changed since is
        /// saved as changed.</summary>
        public void Save()
        {
            if (_cmd == null) { Table.Save(this); return; }
            CommandLineOverrides c = _cmd;
            // Once the player changes a value away from its override it is theirs from then on.
            c.VSync &= VSync == c.Over.VSync;
            c.LowLatency &= LowLatency == c.Over.LowLatency;
            c.Lean &= LeanRendering == c.Over.LeanRendering;
            c.ShowFps &= ShowFps == c.Over.ShowFps;
            c.ShowLatency &= ShowLatency == c.Over.ShowLatency;
            c.FpsCap &= FpsCap == c.Over.FpsCap;
            c.RenderScale &= Mathf.Approximately(RenderScale, c.Over.RenderScale);
            c.Look &= Look.Equals(c.Over.Look);
            var v = (VideoSettings)MemberwiseClone();
            if (c.VSync) v.VSync = c.Stored.VSync;
            if (c.LowLatency) v.LowLatency = c.Stored.LowLatency;
            if (c.Lean) v.LeanRendering = c.Stored.LeanRendering;
            if (c.ShowFps) v.ShowFps = c.Stored.ShowFps;
            if (c.ShowLatency) v.ShowLatency = c.Stored.ShowLatency;
            if (c.FpsCap) v.FpsCap = c.Stored.FpsCap;
            if (c.RenderScale) v.RenderScale = c.Stored.RenderScale;
            if (c.Look) v.Look = c.Stored.Look;
            Table.Save(v);
        }

        /// <summary>Which values the command line overrode this run, what they were before, and what they became.</summary>
        private sealed class CommandLineOverrides
        {
            public bool VSync, LowLatency, Lean, ShowFps, ShowLatency, FpsCap, RenderScale, Look;
            public VideoSettings Stored, Over;
        }

        private CommandLineOverrides _cmd;

        public void ResetGroup(string group) { _cmd = null; Table.Defaults(this, group); Clamped(); Changed(); }
        public void ResetAll() { _cmd = null; Table.Defaults(this); Clamped(); Changed(); }
        public void WriteTo(System.Collections.Generic.IDictionary<string, string> into) => Table.Write(this, into);
        public int ReadFrom(System.Collections.Generic.IReadOnlyDictionary<string, string> from) { _cmd = null; int n = Table.Read(this, from); Clamped(); Changed(); return n; }

        /// <summary>
        /// One-run overrides, never saved (benchmarks and A/B tests): -vsync 0|1, -fpscap N (0 = unlimited, -1 = display - 3),
        /// -lowlatency 0|1, -lean 0|1, -renderscale 0.5..1, -showfps 0|1, -showlatency 0|1, -look off|low|medium|high|ultra (competitive|quality).
        /// </summary>
        public void ApplyCommandLine(string[] args)
        {
            if (args == null) return;
            var stored = (VideoSettings)MemberwiseClone();
            var cmd = new CommandLineOverrides();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant(), next = args[i + 1];
                if (a == "-vsync" && Flag(next, out bool b)) { VSync = b; cmd.VSync = true; }
                else if (a == "-lowlatency" && Flag(next, out b)) { LowLatency = b; cmd.LowLatency = true; }
                else if (a == "-lean" && Flag(next, out b)) { LeanRendering = b; cmd.Lean = true; }
                else if (a == "-showfps" && Flag(next, out b)) { ShowFps = b; cmd.ShowFps = true; }
                else if (a == "-showlatency" && Flag(next, out b)) { ShowLatency = b; cmd.ShowLatency = true; }
                else if (a == "-fpscap" && int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap)) { FpsCap = cap; cmd.FpsCap = true; }
                else if (a == "-renderscale" && float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out float s)) { RenderScale = s; cmd.RenderScale = true; }
                // -look off|low|medium|high|ultra (or competitive = off, quality = high): that picture quality step for
                // one run (benchmarks and before/after photos).
                else if (a == "-look" && Sculpt.Core.Settings.PictureLadder.TryParse(next, out Sculpt.Core.Settings.PictureQuality q))
                {
                    Look = Sculpt.Core.Settings.PictureLadder.Of(q);
                    cmd.Look = true;
                }
            }
            Clamped();
            if (!(cmd.VSync || cmd.LowLatency || cmd.Lean || cmd.ShowFps || cmd.ShowLatency || cmd.FpsCap || cmd.RenderScale || cmd.Look)) return;
            cmd.Stored = stored;
            cmd.Over = (VideoSettings)MemberwiseClone();
            cmd.Over._cmd = null;
            _cmd = cmd;
        }

        private static bool Flag(string s, out bool value)
        {
            value = s == "1" || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            return value || s == "0" || string.Equals(s, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
