using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The agent page says, on the page and not only on hover, that Retire and Purge reach this project only.</summary>
/// <remarks>
/// A person reads "remove" as "remove from Parley". Each project is its own store, so the same repository in another
/// project is another agent, and neither act reaches it. A button's title is never shown on a touch screen or to the
/// keyboard, so the page says it in words.
/// </remarks>
[Integration]
public sealed class TheAgentPageSaysWhereWithdrawingReachesTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    [Fact]
    public async Task The_agent_page_says_retire_and_purge_act_in_this_project_only()
    {
        // Without the guard the page said only "Retire and Purge act on this agent.", and a person purging it here thought
        // it was gone from every project, or feared it would be (review, lens 3 round 36).
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Agent, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });

        using var client = console.CreateClient();

        var page = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Agent?project={RoadmapConsole.Project}&agent={Agent}", UriKind.Relative),
            TestContext.Current.CancellationToken));

        var from = page.IndexOf("class=\"composer-note scope-note\"", StringComparison.Ordinal);

        Assert.True(from >= 0, "the agent page has no scope note");

        var note = page[from..page.IndexOf("</p>", from, StringComparison.Ordinal)];

        Assert.Contains($"Retire and Purge act in {RoadmapConsole.Project} only.", note, StringComparison.Ordinal);
        Assert.Contains("also an agent in other projects, it stays there", note, StringComparison.Ordinal);
    }
}
