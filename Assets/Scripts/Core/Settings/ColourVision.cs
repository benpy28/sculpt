using System;

namespace Sculpt.Core.Settings
{
    /// <summary>A colour in sRGB, 0..1 per channel (Core has no Unity colour type).</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public readonly float R, G, B;
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
        public bool Equals(Rgb o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object obj) => obj is Rgb o && Equals(o);
        public override int GetHashCode() => (R.GetHashCode() * 397 ^ G.GetHashCode()) * 397 ^ B.GetHashCode();
        public override string ToString() => $"({R:0.00}, {G:0.00}, {B:0.00})";
    }

    /// <summary>Colour vision modes in Settings > Accessibility.</summary>
    public enum ColourMode : byte { Off = 0, Protanopia = 1, Deuteranopia = 2, Tritanopia = 3 }

    /// <summary>
    /// Colourblind support. The game's colour pairs that carry information are the team colours and the battle royale
    /// rarity colours. Team blue and orange already stay far apart for every kind of colour blindness (checked by the
    /// tests below), so only rarity changes: each mode has its own five colours chosen so that, seen through that
    /// colour blindness, every pair stays clearly apart, and they also stay apart with normal vision (so a mixed group
    /// can still talk about "the yellow one"). None is purple: purple is kept for sculptable.
    ///
    /// The simulation is Machado, Oliveira and Fernandes (2009) at full severity, applied in linear RGB; distance is in
    /// OKLab (about 0.02 is just noticeable; the tests ask for 0.15, which reads as plainly different at a glance).
    /// </summary>
    public static class ColourVision
    {
        public const int ModeCount = 4;

        public static string Name(ColourMode m) => m switch
        {
            ColourMode.Protanopia => "Protanopia (red-weak)",
            ColourMode.Deuteranopia => "Deuteranopia (green-weak)",
            ColourMode.Tritanopia => "Tritanopia (blue-weak)",
            _ => "Off",
        };

        // Index: 0 none, 1 common, 2 uncommon, 3 rare, 4 epic, 5 legendary (same order as Rarity).
        private static readonly Rgb[] RarityOff =
        {
            new Rgb(0.96f, 0.97f, 0.98f), new Rgb(0.78f, 0.80f, 0.82f), new Rgb(0.42f, 0.86f, 0.38f),
            new Rgb(0.35f, 0.66f, 1.00f), new Rgb(1.00f, 0.38f, 0.30f), new Rgb(1.00f, 0.80f, 0.22f),
        };
        // Red-green (protanopia and deuteranopia): white, teal, blue, orange, yellow.
        private static readonly Rgb[] RarityRedGreen =
        {
            new Rgb(0.96f, 0.97f, 0.98f), new Rgb(0.90f, 0.92f, 0.94f), new Rgb(0.36f, 0.70f, 0.75f),
            new Rgb(0.31f, 0.48f, 1.00f), new Rgb(0.98f, 0.47f, 0.06f), new Rgb(1.00f, 0.95f, 0.10f),
        };
        // Blue-yellow (tritanopia): white, cyan, olive, rose red, amber.
        private static readonly Rgb[] RarityTritan =
        {
            new Rgb(0.96f, 0.97f, 0.98f), new Rgb(0.90f, 0.92f, 0.94f), new Rgb(0.05f, 0.80f, 0.76f),
            new Rgb(0.51f, 0.56f, 0.17f), new Rgb(0.95f, 0.21f, 0.34f), new Rgb(0.97f, 0.67f, 0.28f),
        };

        /// <summary>Team colours (team 1 blue, team 2 orange). The same in every mode; see the class comment.</summary>
        public static readonly Rgb[] Teams = { new Rgb(0.20f, 0.50f, 0.95f), new Rgb(0.95f, 0.45f, 0.15f) };

        /// <summary>The colour for a rarity (0 = none .. 5 = legendary) in this mode.</summary>
        public static Rgb Rarity(ColourMode mode, int rarity)
        {
            Rgb[] p = mode == ColourMode.Protanopia || mode == ColourMode.Deuteranopia ? RarityRedGreen
                    : mode == ColourMode.Tritanopia ? RarityTritan : RarityOff;
            return p[rarity < 0 ? 0 : rarity >= p.Length ? p.Length - 1 : rarity];
        }

        public const int RarityCount = 6;

        // ---------------------------------------------------------------- Simulation and distance (for the tests and the preview)

        private static readonly double[][] Protan = { new[] { 0.152286, 1.052583, -0.204868 }, new[] { 0.114503, 0.786281, 0.099216 }, new[] { -0.003882, -0.048116, 1.051998 } };
        private static readonly double[][] Deutan = { new[] { 0.367322, 0.860646, -0.227968 }, new[] { 0.280085, 0.672501, 0.047413 }, new[] { -0.011820, 0.042940, 0.968881 } };
        private static readonly double[][] Tritan = { new[] { 1.255528, -0.076749, -0.178779 }, new[] { -0.078411, 0.930809, 0.147602 }, new[] { 0.004733, 0.691367, 0.303900 } };

        private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        private static double ToGamma(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>How <paramref name="c"/> looks with this colour blindness (Off = unchanged).</summary>
        public static Rgb Simulate(Rgb c, ColourMode m)
        {
            double[][] k = m == ColourMode.Protanopia ? Protan : m == ColourMode.Deuteranopia ? Deutan : m == ColourMode.Tritanopia ? Tritan : null;
            if (k == null) return c;
            double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
            double R = Clamp01(k[0][0] * r + k[0][1] * g + k[0][2] * b);
            double G = Clamp01(k[1][0] * r + k[1][1] * g + k[1][2] * b);
            double B = Clamp01(k[2][0] * r + k[2][1] * g + k[2][2] * b);
            return new Rgb((float)ToGamma(R), (float)ToGamma(G), (float)ToGamma(B));
        }

        /// <summary>OKLab lightness and the two colour axes of an sRGB colour.</summary>
        public static void OkLab(Rgb c, out double l, out double a, out double b)
        {
            double r = ToLinear(c.R), g = ToLinear(c.G), bl = ToLinear(c.B);
            double L = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bl);
            double M = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bl);
            double S = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bl);
            l = 0.2104542553 * L + 0.7936177850 * M - 0.0040720468 * S;
            a = 1.9779984951 * L - 2.4285922050 * M + 0.4505937099 * S;
            b = 0.0259040371 * L + 0.7827717662 * M - 0.8086757660 * S;
        }

        /// <summary>Perceived difference between two colours as seen with this colour blindness (OKLab distance).</summary>
        public static double Difference(Rgb x, Rgb y, ColourMode seenWith)
        {
            OkLab(Simulate(x, seenWith), out double l1, out double a1, out double b1);
            OkLab(Simulate(y, seenWith), out double l2, out double a2, out double b2);
            return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
        }

        // ---------------------------------------------------------------- Purple is reserved

        /// <summary>Hue in degrees (0..360) and saturation (0..1), HSV style.</summary>
        public static void HueSat(Rgb c, out double hue, out double sat)
        {
            double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), d = max - min;
            sat = max <= 0 ? 0 : d / max;
            if (d <= 1e-9) { hue = 0; return; }
            if (max == c.R) hue = 60 * (((c.G - c.B) / d) % 6);
            else if (max == c.G) hue = 60 * ((c.B - c.R) / d + 2);
            else hue = 60 * ((c.R - c.G) / d + 4);
            if (hue < 0) hue += 360;
        }

        public const double PurpleHueMin = 250, PurpleHueMax = 335, PurpleMinSat = 0.2;

        /// <summary>True if the colour reads as purple or violet (kept for "sculptable" only).</summary>
        public static bool IsPurple(Rgb c)
        {
            HueSat(c, out double h, out double s);
            return s > PurpleMinSat && h >= PurpleHueMin && h <= PurpleHueMax;
        }

        /// <summary>
        /// A colour the player picked, moved out of the purple band if it falls in it: the hue goes to the nearer edge
        /// (blue or pink), keeping brightness and saturation. Anything else comes back unchanged.
        /// </summary>
        public static Rgb AllowedChoice(Rgb c)
        {
            if (!IsPurple(c)) return c;
            HueSat(c, out double h, out double s);
            double v = Math.Max(c.R, Math.Max(c.G, c.B));
            double mid = (PurpleHueMin + PurpleHueMax) / 2;
            double to = h < mid ? PurpleHueMin - 5 : PurpleHueMax + 5;
            return FromHsv(to, s, v);
        }

        public static Rgb FromHsv(double hue, double sat, double val)
        {
            hue = ((hue % 360) + 360) % 360;
            double c = val * sat, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = val - c;
            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return new Rgb((float)(r + m), (float)(g + m), (float)(b + m));
        }
    }
}
