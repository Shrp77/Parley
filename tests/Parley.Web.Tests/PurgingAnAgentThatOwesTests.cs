using System.Net;
using System.Text.RegularExpressions;
using Parley.Setup;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>A purge from the web console of an agent that still owes work waits for the person's acceptance.</summary>
/// <remarks>
/// The terminal holds this in <c>OwingIsSaidTests</c>; the web half was tested only with no broker to ask, so a gate that
/// let an owing agent through passed every test (review, lens 3 round 19). The agents waiting on it would find their
/// exchanges gone.
/// </remarks>
[Integration]
public sealed partial class PurgingAnAgentThatOwesTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    private static string Waiter => $"billing@{RoadmapConsole.Project}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""name="__RequestVerificationToken"[^>]*value="([^"]+)""")]
    private static partial Regex Token();

    [Fact]
    public async Task An_owing_agent_is_not_purged_without_the_acceptance_and_is_said_to_owe()
    {
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Agent, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Waiter, WaitedOn = Agent, ExchangeId = 7 });

        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={RoadmapConsole.Project}&action=purge&subject={Agent}";
        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("Typed", Agent),
        ]);

        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), form, Ct);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Contains(AgentWithdrawal.Kept, body, StringComparison.Ordinal);
        Assert.Contains(Waiter, body, StringComparison.Ordinal);
        Assert.DoesNotContain(console.Broker.Paths, called => called.EndsWith("/Purge", StringComparison.Ordinal));
    }
}
