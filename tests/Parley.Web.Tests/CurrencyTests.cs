using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That a list view keeps itself current and says so, and that a transcript does not.
/// </summary>
/// <remarks>
/// <para>
/// A console left open on a wall is the ordinary way this is used, and the two failures are
/// opposite. A list that never refreshes shows a deployment as it was; a transcript that
/// refreshes moves under somebody halfway through reading a message, which is worse than being
/// briefly out of date because the reader cannot tell it happened.
/// </para>
/// <para>
/// So the rule is split by page rather than applied uniformly, and the stamp is what makes either
/// half safe: a page that says when it was built can be trusted to be stale, which a page that
/// says nothing cannot.
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
    public void The_transcript_stamps_itself_but_does_not_refresh()
    {
        // The deliberate asymmetry. It says when it was read, and then holds still.
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
        Assert.Contains("says what it will do before it does it", served, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_staleness_check_that_could_not_run_is_not_reported_as_up_to_date()
    {
        // "Nothing has arrived" and "nobody could say" are the same silence to a reader, and the
        // second is where they most need to stop trusting the screen. A transcript whose broker
        // went away would otherwise sit there asserting it was current for as long as the tab
        // stayed open.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Conversations/Detail?project={DeadBrokerConsole.Project}"
                + "&id=shared&handler=Since&since=0",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("\"checkFailed\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcript_has_somewhere_to_say_the_check_failed()
    {
        // The reporting half. A handler that answered honestly into a page with nowhere to put
        // the answer would be a fix that changed nothing anybody sees.
        var markup = Page("src/Parley.Web/Pages/Conversations/Detail.cshtml");

        Assert.Contains("id=\"uncertain\"", markup, StringComparison.Ordinal);
        Assert.Contains("d.checkFailed", markup, StringComparison.Ordinal);
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
