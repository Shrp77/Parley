using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the Kill page decides from the named agent's own row, and leaves the act offered when it cannot read one.</summary>
/// <remarks>
/// <see cref="StoppingAnAgentTests"/> held the page with one row in the registry and a registry that always answered, so
/// the page passed when it offered Kill because another agent was left running, and when an unreadable registry made it
/// say "there is nothing to end here" (review, lens 3 surface A on d99806d9). The page promises that a registry it
/// cannot read leaves the act offered for the broker's own check to decide.
/// </remarks>
[Integration]
public sealed class KillIsOfferedFromTheAgentsOwnRowTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    private static string Other => $"billing@{RoadmapConsole.Project}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RegistryRow Row(string name, bool left) => new()
    {
        Name = name,
        Adapter = "mcp",
        Tier = Wire.AgentTier.Full,
        Status = Wire.AgentStatus.Active,
        Presence = Wire.AgentPresence.Running,
        OpenOutside = left,
        Unsupervised = left,
    };

    private static async Task<string> KillPageAsync(RoadmapConsole console)
    {
        using var client = console.CreateClient();

        return WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Projects/Act?project={RoadmapConsole.Project}&action=kill&subject={Agent}", UriKind.Relative), Ct));
    }

    [Fact]
    public async Task Another_agent_left_running_does_not_offer_kill_for_this_one()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Other, left: true));
        console.Broker.Registry.Add(Row(Agent, left: false));

        var said = await KillPageAsync(console);

        Assert.Contains("there is nothing to end here", said, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"submit\"", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_cannot_be_read_leaves_kill_offered_and_described()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(Row(Agent, left: true));
        console.Broker.FailsOn = "DescribeRegistry";

        var said = await KillPageAsync(console);

        Assert.DoesNotContain("there is nothing to end here", said, StringComparison.Ordinal);
        Assert.Contains("was started by a node that is gone", said, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", said, StringComparison.Ordinal);
    }
}
