using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That a page says when its project's broker is rewriting its store (SPEC.md §9.1).</summary>
/// <remarks>
/// Found by review, lens 1 run 115: a rewrite holds every call for seconds on a large store. The owner decided nothing is
/// refused meanwhile and the person is told, so a page or a send that waits reads as a wait.
/// </remarks>
[Integration]
public sealed class ARewriteIsSaidOnThePageTests
{
    private const string Said = "Parley is rewriting the store of " + RoadmapConsole.Project;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_page_drawn_while_the_seat_says_a_rewrite_is_underway_says_so(bool tidying)
    {
        using var console = new RoadmapConsole();
        console.Broker.Seats = true;
        console.Broker.Tidying = tidying;
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "human-user@" + RoadmapConsole.Project, Adapter = "parley-console", Tier = Wire.AgentTier.Pull,
            Status = Wire.AgentStatus.Active,
        });
        using var client = console.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var page = string.Empty;

        // The first page takes the seat (the landing page defers it, so the inbox); a later one finds what its stream said.
        for (var tries = 0; tries < 20 && !page.Contains(Said, StringComparison.Ordinal); tries++)
        {
            page = await client.GetStringAsync(new Uri("/Inbox", UriKind.Relative), token);
            await Task.Delay(100, token);
        }

        Assert.Equal(tidying, page.Contains(Said, StringComparison.Ordinal));
    }
}
