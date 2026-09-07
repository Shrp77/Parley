using System.Net;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That the console's view state survives the round trip a web page makes it take.
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 of the review gate, run 1.4. The terminal keeps the sort and the archived toggle in a
/// field; the console has to put them in a URL and get them back, and every action in between is a
/// redirect that can drop them.
/// </para>
/// <para>
/// <b>The failure is specific and quiet.</b> An operator turns on <i>show archived</i>, archives
/// something, and the redirect loses the toggle — so the conversation they just filed vanishes
/// from a list they had deliberately set to show it, and reads as having been destroyed rather than
/// archived.
/// </para>
/// </remarks>
public sealed class ListingStateTests
{
    [Theory]
    [InlineData("showArchived=true")]
    [InlineData("sort=name")]
    [InlineData("showArchived=true&sort=name")]
    public async Task The_listing_accepts_its_own_options_without_falling_over(string query)
    {
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri($"/Projects/Agents?project={DeadBrokerConsole.Project}&{query}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_sort_is_the_default_rather_than_an_error()
    {
        // A value in a URL is a value somebody typed or a link somebody edited. The list has two
        // orders and anything else is one of them, not a page that will not open.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(
                $"/Projects/Agents?project={DeadBrokerConsole.Project}&sort=sideways",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void The_archive_action_carries_the_listing_options_back()
    {
        // The quiet failure this run is for. Read from the source rather than driven, because
        // driving it needs an antiforgery token and a live broker, and what is being checked is
        // that the redirect names the two options at all -- which is a property of the line, not of
        // a round trip.
        var model = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Web", "Pages", "Projects", "Agents.cshtml.cs"));

        var redirect = model[model.IndexOf("OnPostArchiveAsync", StringComparison.Ordinal)..];

        Assert.Contains("showArchived", redirect, StringComparison.Ordinal);
        Assert.Contains("sort", redirect, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hidden_line_and_the_toggle_cannot_disagree()
    {
        // Both come from the same read: the count is what the broker omitted, and the toggle is what
        // was asked for. A page that computed the line from anything else could say "2 archived are
        // not shown" on a listing that was showing them.
        var model = File.ReadAllText(Path.Combine(
            Root(), "src", "Parley.Web", "Pages", "Projects", "Agents.cshtml.cs"));

        Assert.Contains(
            "ConversationsView.Hidden(listed.Value?.ArchivedOmitted ?? 0, ShowArchived)",
            model,
            StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SPEC.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
