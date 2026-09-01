using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// That nothing destructive happens on one click, and that refusal is not shown as failure.
/// </summary>
/// <remarks>
/// The half of the console that changes a deployment. SPEC.md §10.2's purge removes an agent and
/// every conversation it was party to — its peers' side included — and cannot be undone, so the
/// only acceptable behaviour is to say what will be lost and wait. An operator who meant retire
/// and clicked purge has to have been told.
/// </remarks>
public sealed class ActingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _console;

    public ActingTests(WebApplicationFactory<Program> console) => _console = console;

    [Theory]
    [InlineData("purge", "cannot be undone")]
    [InlineData("retire", "Its history is kept")]
    [InlineData("reinstate", "routed to again")]
    [InlineData("forceconclude", "over its participants")]
    [InlineData("instruct", "It may decline")]
    public async Task Every_action_is_described_before_it_is_offered(string action, string said)
    {
        // A GET describes; only a POST does. So a link cannot destroy anything, and neither can
        // anything that follows links — which includes whatever crawls a page somebody left open.
        // Against a console that actually has this project, and with a client that does not
        // follow redirects. On a machine with no manifest every page here redirects to the list,
        // and a test asserting OK on a followed redirect passes without ever reaching the page.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        using var response = await client.GetAsync(
            new Uri(
                $"/Projects/Act?project={DeadBrokerConsole.Project}"
                + $"&action={action}&subject=orders@{DeadBrokerConsole.Project}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Contains(said, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_action_the_console_does_not_do_says_so_rather_than_offering_a_button()
    {
        // Anything can be typed into that query string. The page must not describe an operation
        // it has no case for, because the confirm button under it would then do nothing while
        // looking exactly like the ones that do.
        using var console = new DeadBrokerConsole();
        using var client = console.CreateStubbornClient();

        var body = await client.GetStringAsync(
            new Uri(
                $"/Projects/Act?project={DeadBrokerConsole.Project}"
                + $"&action=obliterate&subject=orders@{DeadBrokerConsole.Project}",
                UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("not something the console does", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_says_what_it_destroys_and_names_the_gentler_option()
    {
        // The words matter more than the mechanism here. Somebody about to purge is usually
        // somebody who wanted to retire, and the page is the last place that can say so.
        var page = File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Projects/Act.cshtml.cs"));

        Assert.Contains("cannot be undone", page, StringComparison.Ordinal);
        Assert.Contains("peers' side", page, StringComparison.Ordinal);
        Assert.Contains("retiring it instead", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decline_is_not_reported_as_a_failure()
    {
        // §11.5: an agent may refuse, and an interface that renders refusal as malfunction
        // teaches whoever reads it to work around the agent rather than ask why.
        var page = File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Projects/Act.cshtml.cs"));

        Assert.Contains("declined, which is its right", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeliverable_is_told_apart_from_failed()
    {
        // Different facts with different remedies: one is a machine that is off, and re-sending
        // will not change it.
        var page = File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Projects/Act.cshtml.cs"));

        Assert.Contains("not connected, so nothing was delivered", page, StringComparison.Ordinal);
        Assert.Contains("The turn did not complete", page, StringComparison.Ordinal);
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
