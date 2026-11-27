using System.Globalization;

namespace Parley.Web.Palettes;

/// <summary>A colour palette for the console: a base colour, and the harmony that gives its accent.</summary>
/// <param name="Key">The name it is chosen by, letters and hyphens; <c>custom</c> for one the person made.</param>
/// <param name="Name">The name the person reads.</param>
/// <param name="BaseHue">The base colour's hue, 0 to 359 degrees.</param>
/// <param name="BaseChroma">The base colour's chroma, 0 to 120.</param>
/// <param name="Harmony">How the accent relates to the base (<see cref="Harmonies"/>), or null for an accent chosen outright.</param>
/// <param name="AccentHue">The accent's hue, when it is chosen outright rather than by the harmony.</param>
/// <param name="AccentChroma">The accent's chroma.</param>
/// <param name="Neutral">Whether the primary is a near-neutral (a warm grey, say), drawn darker so it still reads as an accent.</param>
/// <remarks>
/// <para>
/// <b>Three colours, two of them derived.</b> Material 3 builds a scheme from five key colours. The secondary here is the
/// base's hue at a third of its chroma and the neutrals are the base's hue at very low chroma, because neutrals tinted by
/// the base are what make a scheme feel of a piece, and a person choosing five colours freely can pick neutrals that fight
/// the accents. Error stays fixed, so a failure always reads as one.
/// </para>
/// <para>
/// Feature 042 (research R9). The six ready-made palettes were chosen by the person from pictures; their prototype is
/// <c>specs/042-theme-and-layout-refresh/directions/palettes.py</c>.
/// </para>
/// </remarks>
public sealed record Palette(
    string Key,
    string Name,
    double BaseHue,
    double BaseChroma,
    string? Harmony,
    double? AccentHue = null,
    double AccentChroma = 30,
    bool Neutral = false)
{
    /// <summary>The harmonies a palette's accent may follow, and how far round the colour wheel each turns from the base.</summary>
    public static readonly IReadOnlyDictionary<string, double> Harmonies = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["analogous"] = -30,
        ["complementary"] = 180,
        ["split-complementary"] = 150,
        ["triadic"] = 120,
    };

    /// <summary>The palette a console uses when the person has chosen none.</summary>
    public static Palette Default => Ready[0];

    /// <summary>The ready-made palettes, the default first.</summary>
    public static IReadOnlyList<Palette> Ready { get; } =
    [
        new("frost", "Frost", 262, 40, "analogous", AccentChroma: 16),
        new("plum-dusk", "Plum dusk", 320, 45, "analogous", AccentChroma: 40),
        new("warm-studio", "Warm studio", 85, 6, null, AccentHue: 110, AccentChroma: 70, Neutral: true),
        new("ocean", "Ocean", 205, 40, "complementary", AccentChroma: 45),
        new("forest", "Forest", 140, 38, null, AccentHue: 75, AccentChroma: 50),
        new("ember", "Ember", 40, 50, "split-complementary", AccentChroma: 30),
    ];

    /// <summary>The accent's hue: chosen outright, or the base's turned by the harmony.</summary>
    public double Accent => AccentHue ?? (((BaseHue + Harmonies[Harmony ?? "analogous"]) % 360) + 360) % 360;

    /// <summary>Reads a palette from a stylesheet request's query: a ready-made one by name, or one the person made.</summary>
    /// <param name="name">A ready-made palette's key, or null.</param>
    /// <param name="hue">A made palette's base hue, as written, or null.</param>
    /// <param name="chroma">Its base chroma, as written, or null.</param>
    /// <param name="harmony">Its harmony, or null.</param>
    /// <returns>The palette, or null when the query names nothing a palette can be.</returns>
    /// <remarks>
    /// Strict, because the values come from the person's browser storage and so from anything that can write there: a
    /// name must be a ready-made one, a hue a whole number of degrees, a chroma a whole number from 0 to 120 and a harmony
    /// one of <see cref="Harmonies"/>. Nothing read here reaches the stylesheet but the numbers it computes.
    /// </remarks>
    public static Palette? FromQuery(string? name, string? hue, string? chroma, string? harmony)
    {
        if (name is { Length: > 0 })
        {
            return hue is null && chroma is null && harmony is null
                ? Ready.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.Ordinal))
                : null;
        }

        return int.TryParse(hue, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 0 and < 360
            && int.TryParse(chroma, NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 120
            && harmony is not null && Harmonies.ContainsKey(harmony)
                ? new Palette("custom", "Your own", h, c, harmony, AccentChroma: Math.Max(16, c * 0.8))
                : null;
    }
}
