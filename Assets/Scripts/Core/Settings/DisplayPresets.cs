namespace Sculpt.Core.Settings
{
    /// <summary>The video values a preset sets (frame cap codes as in the game's VideoSettings: 0 unlimited, -1 display - 3).</summary>
    public struct VideoValues
    {
        public bool VSync;
        public int FpsCap;
        public bool LowLatency;
        public bool LeanRendering;
        public float RenderScale;
        /// <summary>MSAA samples: 1 = off, 2, 4 or 8.</summary>
        public int Msaa;
    }

    public enum VideoPreset : byte { Custom = 0, Competitive = 1, Quality = 2 }

    /// <summary>
    /// The two video presets Ben asked for (home screen roadmap item 20). Competitive: the lowest latency and highest frame
    /// rate (V-Sync off, no cap, one queued frame, lean rendering, full resolution, no anti-aliasing). Quality: smoother
    /// edges and an even frame pace (4x MSAA, capped 3 under the display's refresh so a variable-refresh display never
    /// tears or adds V-Sync delay; low latency stays on). The game's look is unlit, so lean rendering costs no picture in
    /// either; anti-aliasing and the cap are the real differences. Any other mix shows as Custom.
    /// </summary>
    public static class DisplayPresets
    {
        public static VideoValues Of(VideoPreset p) => p == VideoPreset.Quality
            ? new VideoValues { VSync = false, FpsCap = -1, LowLatency = true, LeanRendering = true, RenderScale = 1f, Msaa = 4 }
            : new VideoValues { VSync = false, FpsCap = 0, LowLatency = true, LeanRendering = true, RenderScale = 1f, Msaa = 1 };

        public static string Name(VideoPreset p) => p == VideoPreset.Competitive ? "Competitive" : p == VideoPreset.Quality ? "Quality" : "Custom";

        /// <summary>Which preset these values are exactly, or Custom.</summary>
        public static VideoPreset Detect(in VideoValues v)
        {
            if (Same(v, Of(VideoPreset.Competitive))) return VideoPreset.Competitive;
            if (Same(v, Of(VideoPreset.Quality))) return VideoPreset.Quality;
            return VideoPreset.Custom;
        }

        private static bool Same(in VideoValues a, in VideoValues b) =>
            a.VSync == b.VSync && a.FpsCap == b.FpsCap && a.LowLatency == b.LowLatency && a.LeanRendering == b.LeanRendering &&
            System.Math.Abs(a.RenderScale - b.RenderScale) < 0.001f && NormMsaa(a.Msaa) == NormMsaa(b.Msaa);

        public static int NormMsaa(int s) => s >= 8 ? 8 : s >= 4 ? 4 : s >= 2 ? 2 : 1;
    }

    /// <summary>Crosshair colour choices (Ben: colour only for now). Custom colours are kept out of purple.</summary>
    public static class CrosshairColours
    {
        public static readonly string[] Names = { "White", "Green", "Yellow", "Cyan", "Red", "Orange", "Pink", "Custom" };
        public static readonly Rgb[] Colours =
        {
            new Rgb(1f, 1f, 1f), new Rgb(0.25f, 1f, 0.35f), new Rgb(1f, 0.95f, 0.2f), new Rgb(0.2f, 0.95f, 1f),
            new Rgb(1f, 0.25f, 0.2f), new Rgb(1f, 0.6f, 0.15f), new Rgb(1f, 0.45f, 0.65f),
        };
        public const int Custom = 7;

        /// <summary>The colour to draw for a choice (custom goes through <see cref="ColourVision.AllowedChoice"/>).</summary>
        public static Rgb Of(int choice, Rgb custom)
        {
            if (choice >= 0 && choice < Colours.Length) return Colours[choice];
            return ColourVision.AllowedChoice(new Rgb(Clamp(custom.R), Clamp(custom.G), Clamp(custom.B)));
        }

        private static float Clamp(float v) => float.IsNaN(v) ? 1f : v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
