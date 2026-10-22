using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a list view keeps itself current by reloading and says so, and that a transcript does not reload.
/// </summary>
/// <remarks>
/// <para>
/// A console left open on a wall is the ordinary way this is used. A list that never refreshes shows a
/// deployment as it was, so a list reloads. A transcript that reloaded would throw away a reader's place,
/// what they had typed and what they were searching for, so a transcript follows instead, without moving
/// them — which <see cref="FollowingTests"/> holds.
/// </para>
/// <para>
/// The stamp is what makes a page that cannot follow safe: a page that says when it was built can be
/// trusted to be stale, which a page that says nothing cannot.
/// </para>
/// </remarks>
public sealed class CurrencyTests
{
    private static string Page(string relative) => File.ReadAllText(RepositoryPath(relative));

    [Theory]
    [InlineData("src/Parley.Web/Pages/Index.cshtml")]
    [InlineData("src/Parley.Web/Pages/Projects/Agents.cshtml")]
    public void Every_list_view_refreshes_itself_and_stamps_what_it_shows(string page)
    {
        var markup = Page(page);

        Assert.Contains("ViewData[\"Refresh\"]", markup, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"AsOf\"]", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcript_stamps_itself_but_does_not_reload()
    {
        // It says when it was read, and follows rather than reloading.
        var markup = Page("src/Parley.Web/Pages/Conversations/Detail.cshtml");

        Assert.Contains("ViewData[\"AsOf\"]", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewData[\"Refresh\"]", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frame_renders_the_stamp_wherever_a_page_sets_one()
    {
        // Carried by the layout rather than repeated per page, so a new page cannot forget it by
        // omission -- it can only fail to set it, which is what the theory above catches.
        var layout = Page("src/Parley.Web/Pages/Shared/_Layout.cshtml");

        Assert.Contains("ViewData[\"AsOf\"] is string asOf", layout, StringComparison.Ordinal);
        Assert.Contains("http-equiv=\"refresh\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_frame_does_not_tell_a_reader_the_console_is_read_only()
    {
        // It was true and stopped being true. The console retires, reinstates, instructs, purges
        // and force-concludes, and a footer promising otherwise is the sentence somebody would
        // reasonably rely on before clicking.
        // Asserted against what is served rather than against the file, because the file says
        // "read-only" in a comment explaining why it must not say it -- and a check that could
        // not tell those apart would be a check on my own prose.
        using var console = new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactory<Program>();

        using var client = console.CreateClient();

        var served = await client.GetStringAsync(
            new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("read-only", served, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds a path in the repository from wherever the tests run.</summary>
    /// <param name="relative">The path, relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 }
               && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
