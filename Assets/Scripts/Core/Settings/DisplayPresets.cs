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
        /// <summary>Picture effects (post-processing, shadows, lighting, fog). All off is the competitive look.</summary>
        public LookValues Look;
    }

    /// <summary>
    /// The picture settings beyond frame pacing: post-processing, sun shadows, ambient occlusion, lighting and fog.
    /// Every value at 0 / false is the clean competitive picture (the game exactly as it looked before these settings).
    /// None of them change what the game knows or sends: players never cast shadows (so nobody sees someone round a
    /// corner by their shadow), and players and loot never take shadow, ambient occlusion or atmospheric fog.
    /// </summary>
    public struct LookValues
    {
        /// <summary>Post-process anti-aliasing: 0 off, 1 FXAA, 2 SMAA, 3 TAA (TAA turns MSAA off).</summary>
        public int PostAa;
        /// <summary>0 off, 1 filmic (neutral: keeps hues, so team and rarity colours stay true).</summary>
        public int Tonemap;
        /// <summary>Colour grading: 0 off, 1 natural, 2 vivid.</summary>
        public int Grading;
        /// <summary>0 off, 1 low, 2 high. Only very bright things glow (sun, sparks, blasts), never walls or players.</summary>
        public int Bloom;
        /// <summary>Screen-space ambient occlusion on the world: 0 off, 1 low, 2 high.</summary>
        public int Ao;
        /// <summary>Sun shadows cast by the world: 0 off, 1 low (40 m), 2 high (80 m, soft), 3 ultra (150 m, soft, 4096 map).</summary>
        public int Shadows;
        /// <summary>Directional sun light with sky and bounce colour on the world: 0 flat (baked shading only), 1 rich.</summary>
        public int Lighting;
        /// <summary>1 light haze (the distance haze everyone has), 2 atmospheric (adds height fog on the scenery that
        /// glows toward the sun). 0 was "off" until map v3: the haze is the same on every setting now, so it reads as 1.</summary>
        public int Fog;
        /// <summary>Light shafts round the sun when you look toward it.</summary>
        public bool Shafts;
        /// <summary>Depth of field: 0 off, 1 far background only (never near you or your gun).</summary>
        public int Dof;
        /// <summary>Camera motion blur: 0 off, 1 low, 2 high.</summary>
        public int MotionBlur;
        /// <summary>0..1 amounts.</summary>
        public float Vignette, Grain, Sharpen;
        /// <summary>Surface detail on the world: 0 off, 1 on (per-pixel detail normals and a soft sheen on stone and steel).
        /// Shading only: no shape changes.</summary>
        public int Detail;
        /// <summary>Grass blades on the ground: 0 sparse, 1 full, 2 dense and drawn further out. Blades are always under
        /// 0.3 m and pressed flat round players, so no setting lets grass hide anyone (or shows more than another).</summary>
        public int Grass;

        /// <summary>The picture as it was before these settings (and the competitive one): only the light haze.</summary>
        public static LookValues Clean => new LookValues { Fog = 1 };

        public static readonly string[] PostAaNames = { "Off", "FXAA", "SMAA", "TAA" };
        public static readonly string[] TonemapNames = { "Off", "Filmic" };
        public static readonly string[] GradingNames = { "Off", "Natural", "Vivid" };
        public static readonly string[] LowHighNames = { "Off", "Low", "High" };
        public static readonly string[] ShadowNames = { "Off", "Low", "High", "Ultra" };
        public static readonly string[] OffOnNames = { "Off", "On" };
        public static readonly string[] GrassNames = { "Sparse", "Full", "Dense" };
        public static readonly string[] LightingNames = { "Flat", "Rich" };
        public static readonly string[] FogNames = { "Light haze", "Light haze", "Atmospheric" };
        /// <summary>The fog choices the settings offer (value 1 and 2).</summary>
        public static readonly string[] FogChoices = { "Light haze", "Atmospheric" };
        public static readonly string[] DofNames = { "Off", "Far background" };

        /// <summary>Puts every value in its legal range (bad saved values or a pasted code can't break the renderer).</summary>
        public LookValues Clamped()
        {
            LookValues v = this;
            v.PostAa = Clamp(v.PostAa, 0, 3);
            v.Tonemap = Clamp(v.Tonemap, 0, 1);
            v.Grading = Clamp(v.Grading, 0, 2);
            v.Bloom = Clamp(v.Bloom, 0, 2);
            v.Ao = Clamp(v.Ao, 0, 2);
            v.Shadows = Clamp(v.Shadows, 0, 3);
            v.Lighting = Clamp(v.Lighting, 0, 1);
            v.Fog = Clamp(v.Fog, 1, 2); // the haze can't be turned off (the same view for everyone)
            v.Dof = Clamp(v.Dof, 0, 1);
            v.MotionBlur = Clamp(v.MotionBlur, 0, 2);
            v.Vignette = Unit(v.Vignette);
            v.Grain = Unit(v.Grain);
            v.Sharpen = Unit(v.Sharpen);
            v.Detail = Clamp(v.Detail, 0, 1);
            v.Grass = Clamp(v.Grass, 0, 2);
            return v;
        }

        /// <summary>Whether the render pipeline's post-processing pass is needed at all (off = zero post cost).</summary>
        public bool NeedsPost => PostAa > 0 || Tonemap > 0 || Grading > 0 || Bloom > 0 || Dof > 0 || MotionBlur > 0 || Vignette > 0.001f || Grain > 0.001f;

        /// <summary>Whether a scene depth texture is needed (depth of field, motion blur, light shafts and TAA read it).</summary>
        public bool NeedsDepth => Dof > 0 || MotionBlur > 0 || Shafts || PostAa == 3;

        /// <summary>Whether the frame needs a high dynamic range target (bloom, tonemapping and shafts work above white).</summary>
        public bool NeedsHdr => Bloom > 0 || Tonemap > 0 || Shafts;

        public bool SameAs(in LookValues b) =>
            PostAa == b.PostAa && Tonemap == b.Tonemap && Grading == b.Grading && Bloom == b.Bloom && Ao == b.Ao &&
            Shadows == b.Shadows && Lighting == b.Lighting && Fog == b.Fog && Shafts == b.Shafts && Dof == b.Dof &&
            MotionBlur == b.MotionBlur && Near(Vignette, b.Vignette) && Near(Grain, b.Grain) && Near(Sharpen, b.Sharpen) &&
            Detail == b.Detail && Grass == b.Grass;

        private static bool Near(float a, float b) => System.Math.Abs(a - b) < 0.005f;
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        private static float Unit(float v) => float.IsNaN(v) || v < 0 ? 0 : v > 1 ? 1 : v;
    }

    public enum VideoPreset : byte { Custom = 0, Competitive = 1, Quality = 2 }

    /// <summary>Video > Picture quality: one step for the whole picture (Ben 2026-11-28: "off/minimum up to ultra").</summary>
    public enum PictureQuality : byte { Off = 0, Low = 1, Medium = 2, High = 3, Ultra = 4, Custom = 255 }

    /// <summary>
    /// The picture quality ladder. Each step adds to the one below; none changes what anyone can see of the game (view
    /// distance, haze, grass height and every colour of players and loot are the same on all of them), only how the
    /// scenery is lit and finished, and what it costs the graphics card. Off is the clean competitive picture.
    /// </summary>
    public static class PictureLadder
    {
        public static readonly PictureQuality[] Steps = { PictureQuality.Off, PictureQuality.Low, PictureQuality.Medium, PictureQuality.High, PictureQuality.Ultra };
        public static readonly string[] Names = { "Off", "Low", "Medium", "High", "Ultra" };

        public static string Name(PictureQuality q) => q == PictureQuality.Custom ? "Custom" : Names[(int)q];

        public static LookValues Of(PictureQuality q)
        {
            LookValues l = LookValues.Clean; // Off: no lighting or effects, sparse grass
            if (q == PictureQuality.Custom || q == PictureQuality.Off) return l;
            // Low: sun and sky light on the world, filmic tonemapping, natural grading, FXAA, full grass.
            l.Lighting = 1; l.Tonemap = 1; l.Grading = 1; l.PostAa = 1; l.Grass = 1;
            if (q == PictureQuality.Low) return l;
            // Medium: hard sun shadows to 40 m, low ambient occlusion, low bloom, SMAA, a little sharpening.
            l.Shadows = 1; l.Ao = 1; l.Bloom = 1; l.PostAa = 2; l.Sharpen = 0.2f;
            if (q == PictureQuality.Medium) return l;
            // High: soft shadows to 80 m, high ambient occlusion, atmospheric fog, light shafts, surface detail, vignette.
            l.Shadows = 2; l.Ao = 2; l.Fog = 2; l.Shafts = true; l.Detail = 1; l.Vignette = 0.2f; l.Sharpen = 0.25f;
            if (q == PictureQuality.High) return l;
            // Ultra: shadows to 150 m on a bigger map, high bloom, dense grass drawn further out.
            l.Shadows = 3; l.Bloom = 2; l.Grass = 2;
            return l;
        }

        /// <summary>Which step these values are exactly, or Custom.</summary>
        public static PictureQuality Detect(in LookValues l)
        {
            foreach (PictureQuality q in Steps)
                if (Of(q).SameAs(l)) return q;
            return PictureQuality.Custom;
        }

        /// <summary>Reads "off", "low", "medium", "high", "ultra" (and the preset names "competitive" = off, "quality" = high).</summary>
        public static bool TryParse(string s, out PictureQuality q)
        {
            q = PictureQuality.Custom;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "off": case "competitive": q = PictureQuality.Off; return true;
                case "low": q = PictureQuality.Low; return true;
                case "medium": case "med": q = PictureQuality.Medium; return true;
                case "high": case "quality": q = PictureQuality.High; return true;
                case "ultra": q = PictureQuality.Ultra; return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// The two video presets Ben asked for (home screen roadmap item 20). Competitive: the lowest latency and highest frame
    /// rate (V-Sync off, no cap, one queued frame, lean rendering, full resolution, no anti-aliasing) and the clean
    /// picture (no post-processing, shadows or ambient occlusion; only the light haze). Quality: smoother edges and an
    /// even frame pace (4x MSAA, capped 3 under the display's refresh so a variable-refresh display never tears or adds
    /// V-Sync delay; low latency stays on) and the full look: filmic tonemapping, natural grading, low bloom, high
    /// ambient occlusion and soft shadows, rich lighting, atmospheric fog, light shafts, a light vignette and a little
    /// sharpening. Depth of field, motion blur and film grain stay off in both (Ben: off by default). Any other mix
    /// shows as Custom.
    /// </summary>
    public static class DisplayPresets
    {
        public static VideoValues Of(VideoPreset p) => p == VideoPreset.Quality
            ? new VideoValues { VSync = false, FpsCap = -1, LowLatency = true, LeanRendering = true, RenderScale = 1f, Msaa = 4, Look = QualityLook }
            : new VideoValues { VSync = false, FpsCap = 0, LowLatency = true, LeanRendering = true, RenderScale = 1f, Msaa = 1, Look = LookValues.Clean };

        /// <summary>The Quality preset's picture: the High step of the picture quality ladder.</summary>
        public static LookValues QualityLook => PictureLadder.Of(PictureQuality.High);

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
            System.Math.Abs(a.RenderScale - b.RenderScale) < 0.001f && NormMsaa(a.Msaa) == NormMsaa(b.Msaa) && a.Look.SameAs(b.Look);

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
