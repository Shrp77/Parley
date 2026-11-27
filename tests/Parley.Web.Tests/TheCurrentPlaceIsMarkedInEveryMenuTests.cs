using System.Text.RegularExpressions;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Wherever a place is folded away, in the folded menu's project item or in a phone's More, the item holding the page the
/// person is on says so, and so does the page's own link inside it.
/// </summary>
/// <remarks>
/// <b>Feature 042.</b> A folded menu draws a project's places as one item, and a phone draws the places off its bar under
/// More. Each is a <c>details</c> whose summary is all that shows until it is opened, so without the mark the person reading
/// a page under one sees no place lit, and a screen reader is not told where they are. Found by review: each mark could be
/// dropped and every test passed.
/// </remarks>
[Integration]
public sealed partial class TheCurrentPlaceIsMarkedInEveryMenuTests
{
    private const string Current = "aria-current=\"page\"";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_project_page_under_more_is_marked_on_the_folded_project_and_on_more_and_on_its_link_in_each()
    {
        var page = await PageAsync("/Projects/Health?project=alpha");

        Assert.Contains(Current, Summary(Block(page, "rail-project")), StringComparison.Ordinal);
        Assert.Equal(["/Projects/Health?project=alpha"], MarkedLinks(Block(page, "rail-project")));

        Assert.Contains(Current, Summary(Block(page, "rail-more")), StringComparison.Ordinal);
        Assert.Equal(["/Projects/Health?project=alpha"], MarkedLinks(Block(page, "rail-more")));
    }

    [Fact]
    public async Task A_project_page_on_the_bar_is_marked_on_the_folded_project_and_not_on_more()
    {
        var page = await PageAsync("/Projects/Agents?project=alpha");

        Assert.Contains(Current, Summary(Block(page, "rail-project")), StringComparison.Ordinal);
        Assert.Equal(["/Projects/Agents?project=alpha"], MarkedLinks(Block(page, "rail-project")));

        Assert.DoesNotContain(Current, Summary(Block(page, "rail-more")), StringComparison.Ordinal);
        Assert.Empty(MarkedLinks(Block(page, "rail-more")));
    }

    [Fact]
    public async Task A_page_under_more_outside_a_project_is_marked_on_more_and_on_its_link()
    {
        var page = await PageAsync("/Settings");

        Assert.Contains(Current, Summary(Block(page, "rail-more")), StringComparison.Ordinal);
        Assert.Equal(["/Settings"], MarkedLinks(Block(page, "rail-more")));
    }

    [Fact]
    public async Task A_page_outside_the_project_leaves_the_folded_project_unmarked()
    {
        var page = await PageAsync("/Settings?project=alpha");

        Assert.DoesNotContain(Current, Summary(Block(page, "rail-project")), StringComparison.Ordinal);
        Assert.Empty(MarkedLinks(Block(page, "rail-project")));
        Assert.Equal(["/Settings"], MarkedLinks(Block(page, "rail-more")));
    }

    [GeneratedRegex("<a href=\"([^\"]+)\"[^>]*\\baria-current=\"page\"")]
    private static partial Regex MarkedLink();

    private static async Task<string> PageAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
    }

    /// <summary>The drawn <c>details</c> of the given class in the rail's sections, to its end.</summary>
    private static string Block(string page, string kind)
    {
        var sections = page.IndexOf("<nav aria-label=\"Sections\">", StringComparison.Ordinal);
        Assert.True(sections >= 0, "the rail's sections were not drawn");

        var start = page.IndexOf($"<details class=\"{kind}\">", sections, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {kind} was drawn in the rail's sections");

        return page[start..page.IndexOf("</details>", start, StringComparison.Ordinal)];
    }

    private static string Summary(string block)
    {
        var start = block.IndexOf("<summary", StringComparison.Ordinal);

        Assert.True(start >= 0, "the menu has no summary");
        return block[start..block.IndexOf('>', start)];
    }

    private static string[] MarkedLinks(string block) =>
        [.. MarkedLink().Matches(block).Select(m => m.Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal))];
}
