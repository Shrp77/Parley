using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Retire and Purge say, in words before anything happens, that they reach this project only.</summary>
/// <remarks>
/// A person reads "remove" as "remove from Parley". Each project is its own store, so the same repository in another
/// project is another agent, and neither act reaches it. A button's title is never shown on a touch screen or to the
/// keyboard, so it is said in words. <b>The person, 2026-11-25 (feature 042):</b> said in the act's own confirmation rather
/// than as a paragraph on the agent's page, where it took the room the page needed; the confirmation is where the act is
/// decided, and it is the one implementation both surfaces show (AgentWithdrawal).
/// </remarks>
[Integration]
public sealed class RetireAndPurgeSayWhereTheyReachTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    [Theory]
    [InlineData("retire")]
    [InlineData("purge")]
    public async Task Retire_and_purge_say_before_they_act_that_they_reach_this_project_only(string action)
    {
        // Without the guard the page said only "Retire and Purge act on this agent.", and a person purging it here thought
        // it was gone from every project, or feared it would be (review, lens 3 round 36).
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Agent, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });

        using var client = console.CreateClient();

        var agentPage = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Agent?project={RoadmapConsole.Project}&agent={Agent}", UriKind.Relative),
            TestContext.Current.CancellationToken));

        Assert.Contains($"action={action}&subject={Agent}", agentPage, StringComparison.Ordinal);

        var confirming = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action={action}&subject={Agent}", UriKind.Relative),
            TestContext.Current.CancellationToken));

        Assert.Contains($"This happens in {RoadmapConsole.Project} only", confirming, StringComparison.Ordinal);
        Assert.Contains("an agent in other projects, it stays there", confirming, StringComparison.Ordinal);
    }
}
