using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Settings;

namespace Sculpt.Core.Tests.Settings
{
    public class AimMathTests
    {
        [Test]
        public void CmPer360_HandWorked()
        {
            // Sensitivity 2 = 0.044 degrees per count. 360 / 0.044 = 8181.82 counts; at 800 DPI that is 10.227 inches = 25.977 cm.
            Assert.That(AimMath.CmPer360(2.0, 800), Is.EqualTo(25.977).Within(0.001));
            // Doubling DPI or sensitivity halves the distance.
            Assert.That(AimMath.CmPer360(2.0, 1600), Is.EqualTo(25.977 / 2).Within(0.001));
            Assert.That(AimMath.CmPer360(4.0, 800), Is.EqualTo(25.977 / 2).Within(0.001));
            // A multiplier (ADS) of 0.5 doubles it.
            Assert.That(AimMath.CmPer360(2.0, 800, 0.5), Is.EqualTo(25.977 * 2).Within(0.002));
        }

        [Test]
        public void CmPer360_NonsenseGivesZero()
        {
            Assert.That(AimMath.CmPer360(0, 800), Is.EqualTo(0));
            Assert.That(AimMath.CmPer360(2, 0), Is.EqualTo(0));
            Assert.That(AimMath.CmPer360(double.NaN, 800), Is.EqualTo(0));
            Assert.That(AimMath.SensitivityFor(0, 800), Is.EqualTo(0));
        }

        [Test]
        public void SensitivityFor_IsTheInverse([Values(0.3, 1.0, 2.5, 7.0)] double sens, [Values(400, 800, 1600, 3200)] int dpi)
        {
            double cm = AimMath.CmPer360(sens, dpi);
            Assert.That(AimMath.SensitivityFor(cm, dpi), Is.EqualTo(sens).Within(1e-9));
        }

        [Test]
        public void Edpi_AndDpiClamp()
        {
            Assert.That(AimMath.Edpi(1.5, 800), Is.EqualTo(1200));
            Assert.That(AimMath.ClampDpi(5), Is.EqualTo(AimMath.MinDpi));
            Assert.That(AimMath.ClampDpi(99999), Is.EqualTo(AimMath.MaxDpi));
            Assert.That(AimMath.ClampDpi(1600), Is.EqualTo(1600));
        }

        [Test]
        public void Ads_ZoomMatchedScalesByTheZoom()
        {
            double hip = AimMath.DegreesPerCount(2);
            Assert.That(AimMath.AdsDegreesPerCount(2, 1, false, 0.8), Is.EqualTo(hip).Within(1e-12));
            Assert.That(AimMath.AdsDegreesPerCount(2, 1, true, 0.8), Is.EqualTo(hip * 0.8).Within(1e-12));
            Assert.That(AimMath.AdsDegreesPerCount(2, 0.5, true, 0.8), Is.EqualTo(hip * 0.4).Within(1e-12));
            // No zoom (pump, SMG: scale 1) means zoom matching changes nothing.
            Assert.That(AimMath.AdsDegreesPerCount(2, 1, true, 1.0), Is.EqualTo(hip).Within(1e-12));
        }
    }

    public class SettingsSearchTests
    {
        [TestCase("vsync", "V-Sync (off = lowest latency)", true)]
        [TestCase("v-sync", "V-Sync", true)]
        [TestCase("v sync", "V-Sync", true)]
        [TestCase("VSYNC", "V-Sync", true)]
        [TestCase("sens", "Mouse sensitivity", true)]
        [TestCase("mouse sens", "Mouse sensitivity", true)]
        [TestCase("sens mouse", "Mouse sensitivity", true)]
        [TestCase("ensitivity", "Mouse sensitivity", false)] // prefix only, not substring
        [TestCase("cm 360", "Sensitivity cm/360 readout", true)]
        [TestCase("cm/360", "Sensitivity cm/360 readout", true)]
        [TestCase("fov", "Field of view fov", true)]
        [TestCase("colour", "Colourblind mode color colour", true)]
        [TestCase("color", "Colourblind mode color colour", true)]
        [TestCase("blind", "Colourblind mode", false)] // "colourblind" is one word; keywords cover the other spellings
        [TestCase("", "anything", true)]
        [TestCase("   ", "anything", true)]
        [TestCase("zzz", "", false)]
        public void Matches(string query, string text, bool expected) => Assert.That(SettingsSearch.Matches(query, text), Is.EqualTo(expected));

        [Test]
        public void Words_GlueRunsJoinedByPunctuation()
        {
            Assert.That(SettingsSearch.Words("V-Sync on"), Is.EquivalentTo(new[] { "v", "sync", "vsync", "on" }));
            Assert.That(SettingsSearch.Words("cm/360"), Is.EquivalentTo(new[] { "cm", "360", "cm360" }));
            Assert.That(SettingsSearch.Words("plain words"), Is.EquivalentTo(new[] { "plain", "words" }));
            Assert.That(SettingsSearch.Words(null), Is.Empty);
        }

        [Test]
        public void EveryQueryWordMustMatch()
        {
            Assert.That(SettingsSearch.Matches("mouse volume", "Mouse sensitivity"), Is.False);
        }
    }

    public class ColourVisionTests
    {
        private const double Apart = 0.15;
        private static readonly ColourMode[] Modes = { ColourMode.Off, ColourMode.Protanopia, ColourMode.Deuteranopia, ColourMode.Tritanopia };

        [Test]
        public void SimulateOff_IsIdentity()
        {
            var c = new Rgb(0.3f, 0.6f, 0.9f);
            Assert.That(ColourVision.Simulate(c, ColourMode.Off), Is.EqualTo(c));
        }

        [Test]
        public void SimulatedGreysStayGrey([Values(ColourMode.Protanopia, ColourMode.Deuteranopia, ColourMode.Tritanopia)] ColourMode m)
        {
            // Each Machado matrix keeps white white (rows sum to 1), so a neutral grey reads the same to everyone.
            foreach (float g in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                Rgb s = ColourVision.Simulate(new Rgb(g, g, g), m);
                Assert.That(s.R, Is.EqualTo(g).Within(0.01));
                Assert.That(s.G, Is.EqualTo(g).Within(0.01));
                Assert.That(s.B, Is.EqualTo(g).Within(0.01));
            }
        }

        [Test]
        public void OffPalette_IsWhatTheGameUsedBefore()
        {
            // Turning the option on is the only thing that changes colours.
            Assert.That(ColourVision.Rarity(ColourMode.Off, 2), Is.EqualTo(new Rgb(0.42f, 0.86f, 0.38f)));
            Assert.That(ColourVision.Rarity(ColourMode.Off, 5), Is.EqualTo(new Rgb(1.00f, 0.80f, 0.22f)));
        }

        [Test]
        public void TheReasonForTheModes_OffPaletteCollapsesForRedWeakEyes()
        {
            // Uncommon green and legendary gold look almost the same with protanopia: the problem the modes fix.
            double d = ColourVision.Difference(ColourVision.Rarity(ColourMode.Off, 2), ColourVision.Rarity(ColourMode.Off, 5), ColourMode.Protanopia);
            Assert.That(d, Is.LessThan(0.05));
        }

        [Test]
        public void EachModesRarities_StayApart_ForThatEyeAndForNormalVision([Values(ColourMode.Protanopia, ColourMode.Deuteranopia, ColourMode.Tritanopia)] ColourMode m)
        {
            for (int i = 1; i < ColourVision.RarityCount; i++)
                for (int j = i + 1; j < ColourVision.RarityCount; j++)
                {
                    Rgb a = ColourVision.Rarity(m, i), b = ColourVision.Rarity(m, j);
                    Assert.That(ColourVision.Difference(a, b, m), Is.GreaterThanOrEqualTo(Apart), $"{m}: rarity {i} vs {j} seen with {m}");
                    Assert.That(ColourVision.Difference(a, b, ColourMode.Off), Is.GreaterThanOrEqualTo(Apart), $"{m}: rarity {i} vs {j} with normal vision");
                }
        }

        [Test]
        public void TeamColours_StayApartForEveryone()
        {
            foreach (ColourMode m in Modes)
                Assert.That(ColourVision.Difference(ColourVision.Teams[0], ColourVision.Teams[1], m), Is.GreaterThanOrEqualTo(0.25), m.ToString());
        }

        [Test]
        public void NoPaletteColourIsPurple()
        {
            foreach (ColourMode m in Modes)
                for (int i = 0; i < ColourVision.RarityCount; i++)
                    Assert.That(ColourVision.IsPurple(ColourVision.Rarity(m, i)), Is.False, $"{m} rarity {i}");
            foreach (Rgb t in ColourVision.Teams) Assert.That(ColourVision.IsPurple(t), Is.False);
            foreach (Rgb c in CrosshairColours.Colours) Assert.That(ColourVision.IsPurple(c), Is.False, c.ToString());
        }

        [Test]
        public void IsPurple_HandPicked()
        {
            Assert.That(ColourVision.IsPurple(new Rgb(0.6f, 0.2f, 1f)), Is.True);   // violet
            Assert.That(ColourVision.IsPurple(new Rgb(0.8f, 0.2f, 0.8f)), Is.True);  // magenta-purple
            Assert.That(ColourVision.IsPurple(new Rgb(0.2f, 0.4f, 1f)), Is.False);   // blue
            Assert.That(ColourVision.IsPurple(new Rgb(1f, 0.3f, 0.5f)), Is.False);   // pink-red
            Assert.That(ColourVision.IsPurple(new Rgb(0.6f, 0.6f, 0.62f)), Is.False); // grey with a blue cast
        }

        [Test]
        public void AllowedChoice_NeverPurple_AndLeavesOtherColoursAlone()
        {
            var rnd = new Random(7);
            for (int n = 0; n < 20000; n++)
            {
                var c = new Rgb((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble());
                Rgb a = ColourVision.AllowedChoice(c);
                Assert.That(ColourVision.IsPurple(a), Is.False, c.ToString());
                if (!ColourVision.IsPurple(c)) Assert.That(a, Is.EqualTo(c));
                Assert.That(ColourVision.AllowedChoice(a), Is.EqualTo(a)); // idempotent
            }
        }

        [Test]
        public void CustomCrosshairIsClampedAndKeptOutOfPurple()
        {
            Rgb c = CrosshairColours.Of(CrosshairColours.Custom, new Rgb(0.6f, 0.2f, 1f));
            Assert.That(ColourVision.IsPurple(c), Is.False);
            Rgb n = CrosshairColours.Of(CrosshairColours.Custom, new Rgb(float.NaN, 2f, -1f));
            Assert.That(n, Is.EqualTo(new Rgb(1f, 1f, 0f)));
            Assert.That(CrosshairColours.Of(0, default), Is.EqualTo(new Rgb(1f, 1f, 1f)));
            Assert.That(CrosshairColours.Names.Length, Is.EqualTo(CrosshairColours.Colours.Length + 1));
        }
    }

    public class DisplayPresetTests
    {
        [Test]
        public void Competitive_IsTheLowestLatencyMix()
        {
            VideoValues v = DisplayPresets.Of(VideoPreset.Competitive);
            Assert.That(v.VSync, Is.False);
            Assert.That(v.FpsCap, Is.EqualTo(0));
            Assert.That(v.LowLatency, Is.True);
            Assert.That(v.LeanRendering, Is.True);
            Assert.That(v.RenderScale, Is.EqualTo(1f));
            Assert.That(v.Msaa, Is.EqualTo(1));
        }

        [Test]
        public void Detect_FindsEachPresetAndCustom()
        {
            Assert.That(DisplayPresets.Detect(DisplayPresets.Of(VideoPreset.Competitive)), Is.EqualTo(VideoPreset.Competitive));
            Assert.That(DisplayPresets.Detect(DisplayPresets.Of(VideoPreset.Quality)), Is.EqualTo(VideoPreset.Quality));
            VideoValues v = DisplayPresets.Of(VideoPreset.Competitive);
            v.RenderScale = 0.9f;
            Assert.That(DisplayPresets.Detect(v), Is.EqualTo(VideoPreset.Custom));
            v = DisplayPresets.Of(VideoPreset.Competitive);
            v.Msaa = 0; // 0 and 1 both mean off
            Assert.That(DisplayPresets.Detect(v), Is.EqualTo(VideoPreset.Competitive));
        }

        [Test]
        public void Competitive_HasTheCleanPicture()
        {
            LookValues l = DisplayPresets.Of(VideoPreset.Competitive).Look;
            Assert.That(l.NeedsPost, Is.False, "no post-processing pass at all");
            Assert.That(l.NeedsDepth, Is.False);
            Assert.That(l.NeedsHdr, Is.False);
            Assert.That(l.Ao, Is.EqualTo(0));
            Assert.That(l.Shadows, Is.EqualTo(0));
            Assert.That(l.Lighting, Is.EqualTo(0));
            Assert.That(l.Shafts, Is.False);
            Assert.That(l.Sharpen, Is.EqualTo(0f));
            Assert.That(l.Fog, Is.EqualTo(1), "the light haze the game always had");
            Assert.That(l.SameAs(default(LookValues)), Is.False, "default struct is fog off, not the clean look");
            Assert.That(l.SameAs(LookValues.Clean), Is.True);
        }

        [Test]
        public void Quality_KeepsDepthOfFieldMotionBlurAndGrainOff()
        {
            LookValues l = DisplayPresets.Of(VideoPreset.Quality).Look;
            Assert.That(l.Dof, Is.EqualTo(0));
            Assert.That(l.MotionBlur, Is.EqualTo(0));
            Assert.That(l.Grain, Is.EqualTo(0f));
            Assert.That(l.Shadows, Is.EqualTo(2));
            Assert.That(l.Ao, Is.EqualTo(2));
            Assert.That(l.NeedsPost, Is.True);
            Assert.That(l.PostAa, Is.Not.EqualTo(3), "TAA would switch the preset's MSAA off");
        }

        [Test]
        public void ChangingAnyPictureSetting_MakesItCustom()
        {
            for (int field = 0; field < 14; field++)
            {
                VideoValues v = DisplayPresets.Of(VideoPreset.Competitive);
                LookValues l = v.Look;
                switch (field)
                {
                    case 0: l.PostAa = 1; break;
                    case 1: l.Tonemap = 1; break;
                    case 2: l.Grading = 2; break;
                    case 3: l.Bloom = 1; break;
                    case 4: l.Ao = 1; break;
                    case 5: l.Shadows = 1; break;
                    case 6: l.Lighting = 1; break;
                    case 7: l.Fog = 2; break;
                    case 8: l.Shafts = true; break;
                    case 9: l.Dof = 1; break;
                    case 10: l.MotionBlur = 1; break;
                    case 11: l.Vignette = 0.3f; break;
                    case 12: l.Grain = 0.3f; break;
                    case 13: l.Sharpen = 0.3f; break;
                }
                v.Look = l;
                Assert.That(DisplayPresets.Detect(v), Is.EqualTo(VideoPreset.Custom), "field " + field);
            }
        }

        [Test]
        public void LookClamp_FixesBadValues()
        {
            var l = new LookValues { PostAa = 9, Tonemap = -2, Grading = 5, Bloom = 7, Ao = -1, Shadows = 3, Lighting = 4, Fog = 9, Dof = 2, MotionBlur = 5, Vignette = float.NaN, Grain = 4f, Sharpen = -1f }.Clamped();
            Assert.That(l.PostAa, Is.EqualTo(3));
            Assert.That(l.Tonemap, Is.EqualTo(0));
            Assert.That(l.Grading, Is.EqualTo(2));
            Assert.That(l.Bloom, Is.EqualTo(2));
            Assert.That(l.Ao, Is.EqualTo(0));
            Assert.That(l.Shadows, Is.EqualTo(2));
            Assert.That(l.Lighting, Is.EqualTo(1));
            Assert.That(l.Fog, Is.EqualTo(2));
            Assert.That(l.Dof, Is.EqualTo(1));
            Assert.That(l.MotionBlur, Is.EqualTo(2));
            Assert.That(l.Vignette, Is.EqualTo(0f));
            Assert.That(l.Grain, Is.EqualTo(1f));
            Assert.That(l.Sharpen, Is.EqualTo(0f));
            // Names cover every legal value.
            Assert.That(LookValues.PostAaNames.Length, Is.EqualTo(4));
            Assert.That(LookValues.FogNames.Length, Is.EqualTo(3));
        }

        [Test]
        public void TheDistanceHaze_CantBeTurnedOff()
        {
            // Map v3 (Ben's plan): view distance and haze are the same on every setting, so nobody sees further.
            Assert.That(new LookValues { Fog = 0 }.Clamped().Fog, Is.EqualTo(1), "an old saved 'off' reads as the haze");
            Assert.That(new LookValues { Fog = -3 }.Clamped().Fog, Is.EqualTo(1));
            Assert.That(DisplayPresets.Of(VideoPreset.Competitive).Look.Fog, Is.EqualTo(1));
            Assert.That(DisplayPresets.Of(VideoPreset.Quality).Look.Fog, Is.EqualTo(2));
            Assert.That(LookValues.FogChoices, Does.Not.Contain("Off"));
        }

        [Test]
        public void Needs_FollowTheEffects()
        {
            Assert.That(new LookValues { Vignette = 0.2f }.NeedsPost, Is.True);
            Assert.That(new LookValues { Sharpen = 0.5f, Shafts = true, Ao = 2, Shadows = 2 }.NeedsPost, Is.False, "our own passes and shader effects don't need URP's post pass");
            Assert.That(new LookValues { PostAa = 3 }.NeedsDepth, Is.True);
            Assert.That(new LookValues { PostAa = 2 }.NeedsDepth, Is.False);
            Assert.That(new LookValues { Shafts = true }.NeedsHdr, Is.True);
            Assert.That(new LookValues { Grading = 2 }.NeedsHdr, Is.False);
        }

        [Test]
        public void ThePresetsDiffer() =>
            Assert.That(DisplayPresets.Detect(DisplayPresets.Of(VideoPreset.Quality)), Is.Not.EqualTo(VideoPreset.Competitive));
    }

    public class SettingsCodeTests
    {
        private static Dictionary<string, string> Sample() => new Dictionary<string, string>
        {
            ["sculpt.fov"] = "103.5",
            ["sculpt.sens"] = "1.25",
            ["sculpt.binds"] = "{\"bindings\":[{\"path\":\"<Keyboard>/f\",\"action\":\"Player/Punch\"}]}",
            ["sculpt.odd"] = "line one\nline two \\ back\r",
            ["sculpt.empty"] = "",
            ["sculpt.unicode"] = "é ü 字",
        };

        [Test]
        public void RoundTrips()
        {
            string code = SettingsCode.Encode(Sample());
            Assert.That(code, Does.StartWith(SettingsCode.Prefix));
            Assert.That(code, Does.Not.Contain("\n").And.Not.Contain("=").And.Not.Contain("+").And.Not.Contain("/"));
            Assert.That(SettingsCode.Decode(code), Is.EquivalentTo(Sample()));
            Assert.That(SettingsCode.Decode("  " + code + "\n"), Is.EquivalentTo(Sample())); // pasted with whitespace
        }

        [Test]
        public void IsDeterministic()
        {
            var a = Sample();
            var b = new Dictionary<string, string>();
            var keys = new List<string>(a.Keys);
            keys.Reverse();
            foreach (string k in keys) b[k] = a[k];
            Assert.That(SettingsCode.Encode(b), Is.EqualTo(SettingsCode.Encode(a)));
        }

        [Test]
        public void BadKeysAreRefusedWhenEncoding()
        {
            Assert.Throws<ArgumentException>(() => SettingsCode.Encode(new Dictionary<string, string> { ["bad key"] = "1" }));
            Assert.Throws<ArgumentException>(() => SettingsCode.Encode(new Dictionary<string, string> { ["a=b"] = "1" }));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("hello")]
        [TestCase("SCULPT1-")]
        [TestCase("SCULPT1-!!!!")]
        [TestCase("SCULPT2-AAAA")]
        [TestCase("SCULPT1-AAAA")]
        public void JunkGivesNull(string code) => Assert.That(SettingsCode.Decode(code), Is.Null);

        [Test]
        public void CorruptedCodesNeverThrow()
        {
            string code = SettingsCode.Encode(Sample());
            var rnd = new Random(3);
            for (int n = 0; n < 5000; n++)
            {
                char[] c = code.ToCharArray();
                int flips = 1 + rnd.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int i = SettingsCode.Prefix.Length + rnd.Next(c.Length - SettingsCode.Prefix.Length);
                    c[i] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[rnd.Next(64)];
                }
                Dictionary<string, string> d = null;
                Assert.DoesNotThrow(() => d = SettingsCode.Decode(new string(c)));
                if (d != null) foreach (string k in d.Keys) Assert.That(SettingsCode.ValidKey(k), Is.True);
            }
        }

        [Test]
        public void ADecompressionBombIsRefused()
        {
            var big = new Dictionary<string, string> { ["sculpt.big"] = new string('a', SettingsCode.MaxBytes + 10) };
            string code = SettingsCode.Encode(big); // deflates to a few hundred bytes
            Assert.That(code.Length, Is.LessThan(2000));
            Assert.That(SettingsCode.Decode(code), Is.Null);
        }
    }
}
