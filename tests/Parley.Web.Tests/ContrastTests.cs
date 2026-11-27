using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That every text and outline colour pair the console's tokens define meets WCAG 2.2 AA, in light and in dark.</summary>
/// <remarks>
/// <para>
/// Feature 035 checked its colour pairs once, by hand, and nothing held them afterwards; feature 042 changes every colour.
/// The pairs are read from the tokens themselves, so changing a colour is checked as soon as it is made, and a theme
/// cannot ship one unreadable pair.
/// </para>
/// <para>
/// Text needs 4.5:1 against what it sits on, and an outline or a decoration that carries meaning 3:1 (WCAG 2.2, 1.4.3 and
/// 1.4.11). The light scheme is the first <c>:root</c> block; the dark one is the block the Settings page's choice applies,
/// which <c>MaterialTokensTests</c> holds equal to the system's.
/// </para>
/// </remarks>
[Integration]
public sealed partial class ContrastTests
{
    private static readonly string[] Surfaces =
    [
        "surface", "surface-dim", "surface-bright", "surface-container-lowest", "surface-container-low", "surface-container",
        "surface-container-high", "surface-container-highest",
    ];

    public static TheoryData<string> Themes() => new("light", "dark");

    [GeneratedRegex(@"--md-(sys-color|ext-color|ext)-([a-z0-9-]+): (#[0-9a-f]{6});")]
    private static partial Regex Colour();

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_text_pair_meets_aa(string theme)
    {
        var c = Scheme(theme);
        var pairs = new List<(string Text, string On)>();

        foreach (var role in new[] { "primary", "secondary", "tertiary", "error" })
        {
            pairs.Add(($"on-{role}", role));
            pairs.Add(($"on-{role}-container", $"{role}-container"));
        }

        pairs.AddRange(Surfaces.Select(s => ("on-surface", s)));
        pairs.AddRange(Surfaces.Where(s => s.StartsWith("surface-container", StringComparison.Ordinal) || s == "surface").Select(s => ("on-surface-variant", s)));
        pairs.Add(("inverse-on-surface", "inverse-surface"));
        pairs.Add(("primary", "surface"));
        pairs.Add(("primary", "surface-container-lowest"));
        pairs.Add(("error", "surface"));
        pairs.AddRange(Enumerable.Range(0, 8).Select(i => ("on-slot", $"slot-{i}")));

        var failing = pairs
            .Select(p => (p.Text, p.On, Ratio: Ratio(c[p.Text], c[p.On])))
            .Where(p => p.Ratio < 4.5)
            .Select(p => $"{p.Text} on {p.On}: {p.Ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1")
            .ToList();

        Assert.True(failing.Count == 0, $"{theme}: below 4.5:1: " + string.Join("; ", failing));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_outline_and_marker_meets_three_to_one(string theme)
    {
        var c = Scheme(theme);
        var pairs = new[] { "surface", "surface-container-lowest", "surface-container-high" }.Select(s => ("outline", s))
            .Concat(new[] { "surface", "surface-container-lowest" }.Select(s => ("present", s)))
            .Concat(Enumerable.Range(0, 8).Select(i => ($"slot-{i}", "surface")));

        var failing = pairs
            .Select(p => (p.Item1, p.Item2, Ratio: Ratio(c[p.Item1], c[p.Item2])))
            .Where(p => p.Ratio < 3.0)
            .Select(p => $"{p.Item1} on {p.Item2}: {p.Ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1")
            .ToList();

        Assert.True(failing.Count == 0, $"{theme}: below 3:1: " + string.Join("; ", failing));
    }

    [Fact]
    public void The_ratio_is_wcags()
    {
        // Black on white is 21:1 and a colour on itself 1:1, which pins the formula against a slip in the luminance.
        Assert.Equal(21.0, Ratio("#000000", "#ffffff"), 2);
        Assert.Equal(1.0, Ratio("#2b5cd9", "#2b5cd9"), 2);
        Assert.Equal(4.48, Ratio("#777777", "#ffffff"), 2);
    }

    private static Dictionary<string, string> Scheme(string theme)
    {
        var tokens = File.ReadAllText(RepositoryPath("src/Parley.Web/Brand/tokens.css"));
        var start = theme == "light"
            ? tokens.IndexOf(":root {", StringComparison.Ordinal)
            : tokens.IndexOf(":root[data-theme=\"dark\"] {", StringComparison.Ordinal);
        var block = tokens[start..tokens.IndexOf('}', start)];
        var colours = Colour().Matches(block).ToDictionary(m => m.Groups[2].Value, m => m.Groups[3].Value, StringComparer.Ordinal);

        // Dark defines only what differs from light for the extension colours, so light fills the gaps.
        if (theme == "dark")
        {
            foreach (var (role, value) in Scheme("light").Where(pair => !colours.ContainsKey(pair.Key)))
            {
                colours[role] = value;
            }
        }

        Assert.True(colours.Count >= 40, $"{theme}: only {colours.Count} colours read; has the tokens file changed shape?");

        return colours;
    }

    private static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));

        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string part)
        {
            var v = int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex[1..3])) + (0.7152 * Channel(hex[3..5])) + (0.0722 * Channel(hex[5..7]));
    }

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
