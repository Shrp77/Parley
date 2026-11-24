using System.Text;

namespace Parley.Web.Palettes;

/// <summary>A palette turned into every colour role the console reads, for light and for dark.</summary>
/// <remarks>
/// <para>
/// Each role is the tone Material 3's schemes give it, of one of the palette's tonal palettes. <b>The tones alone keep
/// every pair readable</b>: contrast follows from tone whatever the hue, and Material's tones are spaced so that every text
/// role is at least 4.5:1 from what it sits on. The rule once also moved any role that fell short a tone at a time; a sweep
/// of every whole degree of hue at every fifth step of chroma, with each harmony, in light and dark (36,000 palettes) never
/// needed it, so it was taken out rather than kept as code no test could reach. <c>PaletteTests</c> holds the claim
/// instead, over <see cref="Pairs"/> and <see cref="Marks"/>, for every ready-made palette and a sweep of made ones.
/// </para>
/// <para>
/// <b>The agents' colours follow the palette.</b> The person decided on 2026-11-24 that an agent's colour changes with the
/// palette so that none becomes hard to see against it. Each of the eight keeps its own hue, turned a little towards the
/// base so the eight sit in the scheme (Material's harmonising, at most 15 degrees), at tone 40 in light and 75 in dark,
/// which keeps it 3:1 from the surfaces and 4.5:1 under the initials written on it.
/// </para>
/// </remarks>
public sealed class Scheme
{
    /// <summary>The text pairs that must be 4.5:1: the text role, then the role it sits on.</summary>
    public static readonly IReadOnlyList<(string Text, string On)> Pairs = BuildPairs();

    /// <summary>The marks that must be 3:1 (outlines, the presence dot, the agents' colours): the mark, then what it sits on.</summary>
    public static readonly IReadOnlyList<(string Mark, string On)> Marks = BuildMarks();

    // Each role: the palette it is drawn from, and its tone in light and in dark (Material 3's tones).
    private static readonly (string Role, char Palette, double Light, double Dark)[] Roles =
    [
        ("primary", 'p', 40, 80), ("on-primary", 'p', 100, 20), ("primary-container", 'p', 90, 30), ("on-primary-container", 'p', 10, 90),
        ("secondary", 's', 40, 80), ("on-secondary", 's', 100, 20), ("secondary-container", 's', 90, 30), ("on-secondary-container", 's', 10, 90),
        ("tertiary", 't', 40, 80), ("on-tertiary", 't', 100, 20), ("tertiary-container", 't', 90, 30), ("on-tertiary-container", 't', 10, 90),
        ("error", 'e', 40, 80), ("on-error", 'e', 100, 20), ("error-container", 'e', 90, 30), ("on-error-container", 'e', 10, 90),
        ("surface", 'n', 98, 6), ("on-surface", 'n', 10, 90), ("surface-variant", 'v', 90, 30), ("on-surface-variant", 'v', 30, 80),
        ("surface-dim", 'n', 87, 6), ("surface-bright", 'n', 98, 24), ("surface-container-lowest", 'n', 100, 4),
        ("surface-container-low", 'n', 96, 10), ("surface-container", 'n', 94, 12), ("surface-container-high", 'n', 92, 17),
        ("surface-container-highest", 'n', 90, 22), ("outline", 'v', 50, 60), ("outline-variant", 'v', 80, 30),
        ("inverse-surface", 'n', 20, 90), ("inverse-on-surface", 'n', 95, 20), ("inverse-primary", 'p', 80, 40),
    ];

    // The agents' eight hues, as today's slot colours: blue, green, orange, violet, cyan, rose, olive, slate.
    private static readonly double[] SlotHues = [285, 170, 55, 315, 215, 5, 115, 255];


    private Scheme(Palette palette, bool dark)
    {
        Palette = palette;
        Dark = dark;
        Colours = new Dictionary<string, string>(StringComparer.Ordinal);

        var palettes = new Dictionary<char, (double Hue, double Chroma)>
        {
            ['p'] = (palette.BaseHue, palette.BaseChroma),
            ['s'] = (palette.BaseHue, Math.Max(4, palette.BaseChroma / 3)),
            ['t'] = (palette.Accent, palette.AccentChroma),
            ['e'] = (25, 60),
            ['n'] = (palette.BaseHue, 4),
            ['v'] = (palette.BaseHue, 8),
        };

        foreach (var (role, from, light, darkTone) in Roles)
        {
            var (hue, chroma) = palettes[from];
            Draw(role, hue, chroma, Override(role) ?? (dark ? darkTone : light));
        }

        // The person's own presence: a green that is never the palette's, so "here" reads the same in every palette.
        Draw("present", 150, 45, dark ? 80 : 40);

        for (var i = 0; i < SlotHues.Length; i++)
        {
            var turn = Math.Clamp(Turn(SlotHues[i], palette.BaseHue), -15, 15);
            Draw($"slot-{i}", SlotHues[i] + turn, 45, dark ? 75 : 40);
        }

        Draw("on-slot", palette.BaseHue, 4, dark ? 6 : 100);
    }

    /// <summary>Gets the palette this scheme was built from.</summary>
    public Palette Palette { get; }

    /// <summary>Gets a value indicating whether this is the dark scheme.</summary>
    public bool Dark { get; }

    /// <summary>Gets every role's colour, <c>#rrggbb</c>, by role name (for example <c>on-surface</c> or <c>slot-3</c>).</summary>
    public IReadOnlyDictionary<string, string> Colours { get; }

    /// <summary>Builds a palette's scheme.</summary>
    /// <param name="palette">The palette.</param>
    /// <param name="dark">Whether to build the dark scheme.</param>
    /// <returns>The scheme.</returns>
    public static Scheme For(Palette palette, bool dark)
    {
        ArgumentNullException.ThrowIfNull(palette);

        return new Scheme(palette, dark);
    }

    /// <summary>A palette as the stylesheet the console serves: its light roles, and its dark ones for both ways dark is chosen.</summary>
    /// <param name="palette">The palette.</param>
    /// <returns>The stylesheet.</returns>
    /// <remarks>
    /// Dark is written twice, once for the system's theme and once for the person's choice on the Settings page, as the
    /// console's own tokens are, because one CSS rule cannot answer to both a media query and an attribute.
    /// </remarks>
    public static string Stylesheet(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var light = For(palette, dark: false);
        var dark = For(palette, dark: true);
        var css = new StringBuilder();

        _ = css.Append("/* Palette: ").Append(palette.Name).Append(". Written by the console from its base colour and harmony;")
            .Append(" every text pair meets WCAG 2.2 AA. */\n");
        _ = css.Append(":root {\n").Append(Block(light, "    ")).Append("}\n");
        _ = css.Append("@media (prefers-color-scheme: dark) {\n    :root:not([data-theme=\"light\"]) {\n")
            .Append(Block(dark, "        ")).Append("    }\n}\n");
        _ = css.Append(":root[data-theme=\"dark\"] {\n").Append(Block(dark, "    ")).Append("}\n");

        return css.ToString();
    }

    private static List<(string, string)> BuildPairs()
    {
        var surfaces = new[]
        {
            "surface", "surface-dim", "surface-bright", "surface-container-lowest", "surface-container-low", "surface-container",
            "surface-container-high", "surface-container-highest",
        };
        var pairs = new List<(string, string)>();

        foreach (var role in new[] { "primary", "secondary", "tertiary", "error" })
        {
            pairs.Add(($"on-{role}", role));
            pairs.Add(($"on-{role}-container", $"{role}-container"));
        }

        pairs.AddRange(surfaces.Select(s => ("on-surface", s)));
        pairs.AddRange(surfaces.Where(s => s != "surface-dim" && s != "surface-bright").Select(s => ("on-surface-variant", s)));
        pairs.Add(("inverse-on-surface", "inverse-surface"));
        pairs.Add(("primary", "surface"));
        pairs.Add(("primary", "surface-container-lowest"));
        pairs.Add(("error", "surface"));
        pairs.AddRange(Enumerable.Range(0, 8).Select(i => ("on-slot", $"slot-{i}")));

        return pairs;
    }

    private static List<(string, string)> BuildMarks() =>
        [
            .. new[] { "surface", "surface-container-lowest", "surface-container-high" }.Select(s => ("outline", s)),
            .. new[] { "surface", "surface-container-lowest" }.Select(s => ("present", s)),
            .. Enumerable.Range(0, 8).Select(i => ($"slot-{i}", "surface")),
        ];

    private static double Turn(double from, double to) => ((((to - from) % 360) + 540) % 360) - 180;

    private double? Override(string role) => (Palette.Neutral, Dark, role) switch
    {
        // A near-neutral primary (a warm grey) is drawn near-black in light, so buttons still read as the main action.
        (true, false, "primary") => 20,
        (true, false, "on-primary") => 98,

        // Its accent is the colour: a bright container with dark text, in both themes.
        (true, false, "tertiary-container") => 92,
        (true, false, "on-tertiary-container") => 15,
        (true, true, "tertiary-container") => 85,
        (true, true, "on-tertiary-container") => 15,
        _ => null,
    };

    private void Draw(string role, double hue, double chroma, double tone)
    {
        ((Dictionary<string, string>)Colours)[role] = Colour.Tone(hue, chroma, tone);
    }

    private static string Block(Scheme scheme, string indent)
    {
        var css = new StringBuilder();

        foreach (var (role, colour) in scheme.Colours)
        {
            var name = role.StartsWith("slot-", StringComparison.Ordinal) || role == "on-slot"
                ? $"--md-ext-{role}"
                : role == "present" ? "--md-ext-color-present" : $"--md-sys-color-{role}";

            _ = css.Append(indent).Append(name).Append(": ").Append(colour).Append(";\n");
        }

        return css.ToString();
    }
}
