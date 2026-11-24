using System.Globalization;
using System.Net;
using Parley.Web.Palettes;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That every palette, ready-made or made by the person, keeps every text and mark readable, and is served strictly.</summary>
/// <remarks>
/// Feature 042, spec FR-025 to FR-028 and SC-008. A palette the person makes can start from any colour, so the rule is
/// held over a sweep of the whole colour wheel at low to high chroma with every harmony, in light and in dark, as well as
/// over the ready-made ones. The query the stylesheet is built from comes from the person's browser storage, so it is
/// read strictly.
/// </remarks>
[Unit]
public sealed class PaletteTests
{
    public static TheoryData<string> ReadyKeys()
    {
        var keys = new TheoryData<string>();

        foreach (var palette in Palette.Ready)
        {
            keys.Add(palette.Key);
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(ReadyKeys))]
    public void A_ready_made_palette_keeps_every_pair_readable_in_both_themes(string key)
    {
        var palette = Palette.Ready.Single(p => p.Key == key);

        Assert.Empty(Failures(palette));
    }

    [Fact]
    public void A_palette_made_from_any_colour_keeps_every_pair_readable()
    {
        var failures = new List<string>();

        for (var hue = 0; hue < 360; hue += 5)
        {
            foreach (var chroma in Enumerable.Range(0, 13).Select(i => i * 10))
            {
                foreach (var harmony in Palette.Harmonies.Keys)
                {
                    var made = Palette.FromQuery(null, Text(hue), Text(chroma), harmony);

                    Assert.NotNull(made);
                    failures.AddRange(Failures(made));
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} pairs fall short, first: " + string.Join("; ", failures.Take(5)));
    }

    [Fact]
    public void The_agents_colours_follow_the_palette()
    {
        // The person's decision: an agent's colour changes with the palette, so none can become hard to see in one.
        var frost = Scheme.For(Palette.Ready.Single(p => p.Key == "frost"), dark: false).Colours;
        var ember = Scheme.For(Palette.Ready.Single(p => p.Key == "ember"), dark: false).Colours;

        Assert.NotEqual(frost["slot-0"], ember["slot-0"]);
        Assert.Equal(8, frost.Keys.Count(k => k.StartsWith("slot-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("frost", null, null, null, true)]
    [InlineData("ocean", null, null, null, true)]
    [InlineData("nowhere", null, null, null, false)]
    [InlineData("frost", "10", null, null, false)]
    [InlineData(null, "210", "40", "triadic", true)]
    [InlineData(null, "0", "0", "analogous", true)]
    [InlineData(null, "359", "120", "complementary", true)]
    [InlineData(null, "360", "40", "triadic", false)]
    [InlineData(null, "210", "121", "triadic", false)]
    [InlineData(null, "-1", "40", "triadic", false)]
    [InlineData(null, "2e2", "40", "triadic", false)]
    [InlineData(null, " 21", "40", "triadic", false)]
    [InlineData(null, "210", "40", "pastel", false)]
    [InlineData(null, "210", "40", null, false)]
    [InlineData(null, null, null, null, false)]
    public void Only_a_known_name_or_whole_numbers_in_range_make_a_palette(string? name, string? hue, string? chroma, string? harmony, bool made)
    {
        Assert.Equal(made, Palette.FromQuery(name, hue, chroma, harmony) is not null);
    }

    [Fact]
    public async Task The_console_serves_a_palette_and_refuses_what_is_not_one()
    {
        using var console = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = console.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var named = await client.GetAsync(new Uri("/brand/palette.css?name=ocean", UriKind.Relative), ct);
        var made = await client.GetAsync(new Uri("/brand/palette.css?hue=210&chroma=40&harmony=triadic", UriKind.Relative), ct);
        var refused = await client.GetAsync(new Uri("/brand/palette.css?name=%3Cscript%3E", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        Assert.Equal("text/css", named.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--md-sys-color-primary:", await named.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Contains("--md-ext-slot-0:", await made.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Every_page_links_the_palette_after_the_tokens_and_settings_offers_every_one()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var page = await client.GetStringAsync(new Uri("/Settings", UriKind.Relative), ct);
        var tokens = page.IndexOf("/brand/tokens.css", StringComparison.Ordinal);
        var palette = page.IndexOf("id=\"palette\" href=\"/brand/palette.css?name=frost", StringComparison.Ordinal);

        Assert.True(tokens > 0 && palette > tokens, "the palette must be linked, after the tokens it overrides");

        // The stored choice is only ever a name or numbers: the guard is code, not a comment.
        Assert.Contains(
            "/^(name=[a-z-]{1,32}|hue=\\d{1,3}&chroma=\\d{1,3}&harmony=[a-z-]{1,32})$/.test(chosen)",
            ScriptSource.Code(page, "localStorage.getItem(\"parley.palette\")"),
            StringComparison.Ordinal);

        foreach (var ready in Palette.Ready)
        {
            Assert.Contains($"name=\"palette\" value=\"{ready.Key}\"", page, StringComparison.Ordinal);
        }

        Assert.Contains("name=\"palette\" value=\"custom\"", page, StringComparison.Ordinal);
    }

    private static List<string> Failures(Palette palette)
    {
        var failures = new List<string>();

        foreach (var dark in new[] { false, true })
        {
            var c = Scheme.For(palette, dark).Colours;
            var theme = dark ? "dark" : "light";

            failures.AddRange(Scheme.Pairs
                .Where(p => Colour.Contrast(c[p.Text], c[p.On]) < 4.5)
                .Select(p => $"{palette.Name} ({palette.BaseHue}/{palette.BaseChroma}/{palette.Harmony}) {theme}: {p.Text} on {p.On}"));
            failures.AddRange(Scheme.Marks
                .Where(p => Colour.Contrast(c[p.Mark], c[p.On]) < 3.0)
                .Select(p => $"{palette.Name} ({palette.BaseHue}/{palette.BaseChroma}/{palette.Harmony}) {theme}: {p.Mark} on {p.On}"));
        }

        return failures;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
