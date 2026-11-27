using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// Each filter chip a list draws names the filter it stands for, and its link takes off that filter and only that one.
/// </summary>
/// <remarks>
/// <b>Feature 042.</b> <see cref="FilterChipsTests"/> holds the address a chip is given, from the key it is handed; nothing
/// held the key each page hands it, or the words the chip says. Found by review: the Inbox's Search chip handed the Project
/// filter's key, so pressing it took the project off and left the search on; the home page's Status chip could say the
/// opposite of the filter; and the Agents page's Agent chip could take off the archived filter instead. Each passed every
/// test.
/// </remarks>
[Integration]
public sealed partial class FilterChipsOnThePageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_inbox_chip_names_its_filter_and_takes_off_only_that_one()
    {
        var chips = await ChipsAsync("/Inbox?q=orders&scope=alpha");

        Assert.Equal(["Search: orders", "Project: alpha"], chips.Select(c => c.Says));
        Assert.Equal(["/Inbox", "scope"], Kept(chips[0].Href));
        Assert.Equal(["/Inbox", "q"], Kept(chips[1].Href));
        Assert.Equal("alpha", Query(chips[0].Href)["scope"]);
        Assert.Equal("orders", Query(chips[1].Href)["q"]);
    }

    [Theory]
    [InlineData("up", "Status: Answering")]
    [InlineData("down", "Status: Not answering")]
    public async Task The_home_pages_status_chip_says_the_status_it_filters_on(string status, string says)
    {
        var chip = Assert.Single(await ChipsAsync($"/?tab=agents&status={status}"));

        Assert.Equal(says, chip.Says);
        Assert.Equal(["/", "tab"], Kept(chip.Href));
    }

    [Fact]
    public async Task Each_conversation_list_chip_names_its_filter_and_takes_off_only_that_one()
    {
        var chips = await ChipsAsync("/Projects/Agents?project=alpha&participant=billing@test&showArchived=true");

        Assert.Equal(["Agent: billing@test", "Archived included"], chips.Select(c => c.Says));
        Assert.Equal(["/Projects/Agents", "project", "showArchived"], Kept(chips[0].Href));
        Assert.Equal(["/Projects/Agents", "participant", "project"], Kept(chips[1].Href));
        Assert.Equal("billing@test", Query(chips[1].Href)["participant"]);
    }

    [GeneratedRegex("""<a class="chip" href="(?<href>[^"]*)" title="Take this filter off">(?<says>[^<]*)<span""")]
    private static partial Regex Chip();

    private static async Task<IReadOnlyList<(string Says, string Href)>> ChipsAsync(string path)
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);

        return [.. Chip().Matches(page).Select(m => (WebUtility.HtmlDecode(m.Groups["says"].Value), WebUtility.HtmlDecode(m.Groups["href"].Value)))];
    }

    /// <summary>The path a link leads to, then the names its query keeps, in ordinal order.</summary>
    private static string[] Kept(string href)
    {
        var at = href.IndexOf('?', StringComparison.Ordinal);

        return at < 0 ? [href] : [href[..at], .. Query(href).Keys.Order(StringComparer.Ordinal)];
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query(string href)
    {
        var at = href.IndexOf('?', StringComparison.Ordinal);

        return at < 0 ? [] : QueryHelpers.ParseQuery(href[at..]);
    }
}
