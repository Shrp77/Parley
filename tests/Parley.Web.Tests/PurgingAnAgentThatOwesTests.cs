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

    [Fact]
    public async Task An_owing_agent_is_purged_once_the_person_ticks_the_acceptance()
    {
        // Only the refusal half was held: a gate that ignored the box, and so never let an owing agent go, passed every
        // test, and the person could tick it and type the name and still be told the agent was kept.
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
            new("AcceptOwed", "true"),
            new("Typed", Agent),
        ]);

        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), form, Ct);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Contains(console.Broker.Paths, called => called.EndsWith("/Purge", StringComparison.Ordinal));
        Assert.DoesNotContain(AgentWithdrawal.Kept + " ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_an_agent_that_owes_says_what_is_lost_and_offers_the_acceptance()
    {
        // Without the box on the page the person could never tick it, so an owing agent could not be purged at all.
        using var console = new RoadmapConsole();
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Waiter, WaitedOn = Agent, ExchangeId = 7 });

        var page = await DescribedAsync(console, "purge");

        Assert.Contains("name=\"AcceptOwed\"", page, StringComparison.Ordinal);
        Assert.Contains(AgentWithdrawal.AcceptOwed, page, StringComparison.Ordinal);
        Assert.Contains(
            $"<p class=\"unreachable\">{Agent} owes a Result in 1 open exchange(s). {Waiter} is waiting, and will find the exchange gone rather than still open.</p>",
            page,
            StringComparison.Ordinal);
    }

    private static async Task<string> DescribedAsync(RoadmapConsole console, string action)
    {
        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = Agent, Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
        });

        using var client = console.CreateClient();
        var path = $"/Projects/Act?project={RoadmapConsole.Project}&action={action}&subject={Agent}";

        return WebUtility.HtmlDecode(await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct));
    }

    [Fact]
    public async Task Purging_an_agent_that_owes_nothing_asks_for_no_acceptance()
    {
        // Without the consequence needing something owed, purging an agent that owed nothing said it "owes a Result in 0
        // open exchange(s)" and required a box ticked to accept leaving nobody waiting (review, lens 3 round 32).
        using var console = new RoadmapConsole();

        var page = await DescribedAsync(console, "purge");

        Assert.Contains("<strong>It owes</strong> nothing right now", page, StringComparison.Ordinal);
        Assert.DoesNotContain("owes a Result in 0", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"AcceptOwed\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_says_what_the_agent_owes()
    {
        // Without retire beside purge in the check, the Retire page never read what the agent owed and said nothing of
        // the agents waiting on it, which is what tells retiring from purging (review, lens 3 round 32).
        using var console = new RoadmapConsole();
        console.Broker.Edges.Add(new Wire.WaitingEdge { Waiter = Waiter, WaitedOn = Agent, ExchangeId = 7 });

        var page = await DescribedAsync(console, "retire");

        Assert.Contains($"<strong>It owes</strong> 1 open exchange(s): {Waiter} is waiting", page, StringComparison.Ordinal);
    }
}
