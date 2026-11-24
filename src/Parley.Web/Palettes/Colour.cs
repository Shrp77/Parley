using System.Globalization;

namespace Parley.Web.Palettes;

/// <summary>The colour arithmetic a palette is built with: a tone of a hue, and the contrast between two colours.</summary>
/// <remarks>
/// <para>
/// <b>A tone is CIELAB lightness.</b> Material 3 assigns each colour role a tone (0 black, 100 white) of one of a scheme's
/// palettes, and contrast between two roles follows from their tones whatever their hues. Material's own space is HCT,
/// whose tone is the same CIELAB lightness; its hue and chroma differ slightly from CIELAB's, which changes how a colour
/// looks, never how it contrasts. So a scheme built here keeps Material 3's contrast guarantees with no dependency.
/// </para>
/// <para>
/// A hue and chroma that cannot be shown at a tone (a saturated yellow at tone 30, say) lose chroma until they can, ending
/// on a grey, which every tone has.
/// </para>
/// </remarks>
public static class Colour
{
    /// <summary>The colour of a tone of a hue, as <c>#rrggbb</c>.</summary>
    /// <param name="hue">The hue, in degrees round the CIELAB colour wheel.</param>
    /// <param name="chroma">How colourful, before it is lowered to fit what sRGB can show at this tone.</param>
    /// <param name="tone">The lightness, 0 to 100.</param>
    /// <returns>The colour.</returns>
    public static string Tone(double hue, double chroma, double tone)
    {
        tone = Math.Clamp(tone, 0, 100);

        for (var c = Math.Max(0, chroma); ; c = Math.Max(0, c - 0.5))
        {
            if (ToSrgb(tone, c, hue) is { } rgb)
            {
                return string.Create(CultureInfo.InvariantCulture, $"#{Byte(rgb.R):x2}{Byte(rgb.G):x2}{Byte(rgb.B):x2}");
            }

            if (c == 0)
            {
                // Unreachable: a grey fits at every tone. Kept so the loop has an end a reader can see.
                return tone >= 50 ? "#ffffff" : "#000000";
            }
        }
    }

    /// <summary>The WCAG 2.2 contrast ratio between two colours.</summary>
    /// <param name="first">One colour, <c>#rrggbb</c>.</param>
    /// <param name="second">The other.</param>
    /// <returns>From 1 (the same) to 21 (black on white).</returns>
    public static double Contrast(string first, string second)
    {
        var (a, b) = (Luminance(first), Luminance(second));

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>The relative luminance WCAG defines, 0 for black and 1 for white.</summary>
    /// <param name="hex">A colour, <c>#rrggbb</c>.</param>
    /// <returns>Its luminance.</returns>
    public static double Luminance(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        static double Channel(string part)
        {
            var v = int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex[1..3])) + (0.7152 * Channel(hex[3..5])) + (0.0722 * Channel(hex[5..7]));
    }

    private static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static (double R, double G, double B)? ToSrgb(double l, double c, double h)
    {
        var (a, b) = (c * Math.Cos(h * Math.PI / 180), c * Math.Sin(h * Math.PI / 180));
        var fy = (l + 16) / 116;
        var (fx, fz) = (fy + (a / 500), fy - (b / 200));

        static double Inverse(double t) => t * t * t > 0.008856 ? t * t * t : (t - (16.0 / 116)) / 7.787;

        var (x, y, z) = (0.95047 * Inverse(fx), Inverse(fy), 1.08883 * Inverse(fz));
        var r = (3.2404542 * x) - (1.5371385 * y) - (0.4985314 * z);
        var g = (-0.9692660 * x) + (1.8760108 * y) + (0.0415560 * z);
        var bl = (0.0556434 * x) - (0.2040259 * y) + (1.0572252 * z);

        static double Gamma(double u) => u <= 0.0031308 ? 12.92 * u : (1.055 * Math.Pow(u, 1 / 2.4)) - 0.055;

        var (gr, gg, gb) = (Gamma(r), Gamma(g), Gamma(bl));

        return gr is >= -0.0005 and <= 1.0005 && gg is >= -0.0005 and <= 1.0005 && gb is >= -0.0005 and <= 1.0005
            ? (gr, gg, gb)
            : null;
    }
}
