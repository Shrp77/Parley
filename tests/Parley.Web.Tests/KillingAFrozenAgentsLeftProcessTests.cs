using System.Globalization;
using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That ending the process of a frozen agent a node that is gone left running does not tell the person new work starts it
/// again (SPEC.md §10.2 <c>kill</c>, §14: a freeze gates starting).
/// </summary>
/// <remarks>
/// Review, lens 3 on 6257cf80: the Kill page's frozen check, its use, and the Agent page's Kill tooltip each passed every
/// test with the frozen case removed.
/// </remarks>
[Integration]
public sealed class KillingAFrozenAgentsLeftProcessTests
{
    private static string Agent => $"orders@{RoadmapConsole.Project}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Wire.RegistryRow LeftRow(string name, bool frozen) => new()
    {
        Name = name,
        Adapter = "mcp",
        Tier = Wire.AgentTier.Full,
        Status = Wire.AgentStatus.Active,
        Presence = Wire.AgentPresence.Running,
        OpenOutside = true,
        Unsupervised = true,
        FrozenUtc = frozen
            ? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : string.Empty,
    };

    private static async Task<string> GetAsync(RoadmapConsole console, string path)
    {
        using var client = console.CreateClient();

        return WebUtility.HtmlDecode(await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_kill_page_says_a_frozen_agent_stays_frozen_and_any_other_is_started_by_new_work(bool frozen)
    {
        using var console = new RoadmapConsole();

        // Another agent, frozen, listed first: the page reads the one it names.
        console.Broker.Registry.Add(LeftRow($"billing@{RoadmapConsole.Project}", frozen: !frozen));
        console.Broker.Registry.Add(LeftRow(Agent, frozen));

        var said = await GetAsync(console, $"/Projects/Act?project={RoadmapConsole.Project}&action=kill&subject={Agent}");

        Assert.Contains("ends the process now", said, StringComparison.Ordinal);
        Assert.Equal(frozen, said.Contains("It stays frozen, so nothing starts it again", StringComparison.Ordinal));
        Assert.Equal(!frozen, said.Contains("The next work for it starts it again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_kill_button_of_a_frozen_agent_does_not_promise_that_new_work_starts_it()
    {
        using var console = new RoadmapConsole();
        console.Broker.Registry.Add(LeftRow(Agent, frozen: true));

        var page = await GetAsync(console, $"/Projects/Agent?project={RoadmapConsole.Project}&agent={Agent}");

        Assert.Contains("action=kill&subject=" + Agent, page, StringComparison.Ordinal);
        Assert.DoesNotContain("and the next work for it starts it again", page, StringComparison.Ordinal);
        Assert.Contains("Unless it is frozen, the next work for it starts it again.", page, StringComparison.Ordinal);
    }
}
