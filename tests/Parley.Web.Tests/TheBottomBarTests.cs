using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>On a phone the rail is a bar along the bottom: four places and More inside a project, three outside.</summary>
/// <remarks>
/// <b>Feature 042, the UI contract's compact class.</b> Every place the rail offers must be reachable on a phone, on the
/// bar or under More, and exactly one of the two, or a place is lost or offered twice. The bar keeps the page's order, so
/// focus goes along it as it is drawn.
/// </remarks>
[Integration]
public sealed class TheBottomBarTests
{
    [Fact]
    public async Task Inside_a_project_the_bar_holds_four_places_and_more_holds_the_rest()
    {
        var page = await PageAsync("/?tab=agents&project=alpha");

        Assert.Equal(["/", "/Inbox", "/Projects/Agents?project=alpha", "/?tab=agents&project=alpha"], OnTheBar(page));
        Assert.Equal(
            ["/?tab=agents", "/Roadmaps?project=alpha", "/Projects/Files?project=alpha", "/Projects/Health?project=alpha", "/Projects/Settings?project=alpha", "/Settings"],
            UnderMore(page));
    }

    [Fact]
    public async Task Outside_a_project_the_bar_holds_three_places_and_more_holds_the_settings()
    {
        var page = await PageAsync("/Inbox");

        Assert.Equal(["/", "/?tab=agents", "/Inbox"], OnTheBar(page));
        Assert.Equal(["/Settings"], UnderMore(page));
    }

    [Fact]
    public async Task More_names_the_two_settings_apart_and_offers_the_graph_only_to_script()
    {
        var page = await PageAsync("/?tab=agents&project=alpha");
        var more = MoreBlock(page);

        Assert.Contains(">Project settings<", more, StringComparison.Ordinal);
        Assert.Contains(">Console settings<", more, StringComparison.Ordinal);
        Assert.Matches("<button[^>]*data-pg-open[^>]*hidden", more);
    }

    private static async Task<string> PageAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    private static string Sections(string page)
    {
        var start = page.IndexOf("<nav aria-label=\"Sections\">", StringComparison.Ordinal);
        var end = page.IndexOf("</nav>", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "the rail's sections were not drawn");
        return page[start..end];
    }

    private static string MoreBlock(string page)
    {
        var sections = Sections(page);
        var start = sections.IndexOf("<details class=\"rail-more\">", StringComparison.Ordinal);

        Assert.True(start >= 0, "More is no longer inside the rail's sections");
        return sections[start..];
    }

    private static string[] OnTheBar(string page) =>
        [.. Regex.Matches(Sections(page), "<a href=\"([^\"]+)\"[^>]*\\bclass=\"bar\"").Select(m => Decoded(m.Groups[1].Value))];

    private static string[] UnderMore(string page) =>
        [.. Regex.Matches(MoreBlock(page), "<a href=\"([^\"]+)\"").Select(m => Decoded(m.Groups[1].Value))];

    private static string Decoded(string href) => href.Replace("&amp;", "&", StringComparison.Ordinal);
}
