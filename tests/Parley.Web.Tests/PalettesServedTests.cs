using System.Net;
using Parley.Web.Palettes;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the console serves a palette strictly and every page links it after the tokens it overrides.</summary>
/// <remarks>
/// Feature 042, spec FR-025 to FR-028. Kept apart from <see cref="PaletteTests"/> because each of these starts the web
/// console, which makes them integration tests; the palettes' own rules stay in the unit run.
/// </remarks>
[Integration]
public sealed class PalettesServedTests
{
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

        // Every palette is offered, and the person's own, in the list that opens from the current choice (feature 042).
        var start = page.IndexOf("<details class=\"palette-menu\"", StringComparison.Ordinal);
        var list = page[start..page.IndexOf("</details>", start, StringComparison.Ordinal)];

        foreach (var ready in Palette.Ready)
        {
            Assert.Contains($"name=\"palette\" value=\"{ready.Key}\"", list, StringComparison.Ordinal);
        }

        Assert.Contains("name=\"palette\" value=\"custom\"", list, StringComparison.Ordinal);
    }
}
