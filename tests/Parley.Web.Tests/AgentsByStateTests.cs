using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>The agents are grouped by what they are doing, or listed A to Z, and a legend says what each badge means.</summary>
/// <remarks>
/// Feature 042, the person's feedback: each state a section that opens, with a toggle beside the filter for A to Z, and
/// every badge explained once beside the list. The choice is in the address, so it works with no script and the page's
/// own refresh, which asks for the lines with the same address, keeps it.
/// </remarks>
[Integration]
public sealed class AgentsByStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task By_default_each_state_is_a_section_holding_its_agents()
    {
        var lines = await LinesAsync("/?tab=agents&handler=AgentTable");

        Assert.Contains("<details class=\"state-group\" open data-state-group=", lines, StringComparison.Ordinal);
        Assert.Contains("data-agent=\"orders@beta\"", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_to_Z_lists_the_agents_with_no_sections()
    {
        var lines = await LinesAsync("/?tab=agents&by=name&handler=AgentTable");

        Assert.DoesNotContain("data-state-group", lines, StringComparison.Ordinal);
        Assert.Contains("data-agent=\"orders@beta\"", lines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_offers_both_and_says_what_every_badge_means()
    {
        var page = await LinesAsync("/?tab=agents");

        Assert.Contains(">By state</a>", page, StringComparison.Ordinal);
        Assert.Contains("by=name\">A–Z</a>", page.Replace("&amp;", "&", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("What the badges mean", page, StringComparison.Ordinal);

        foreach (var (_, means) in Parley.Web.Pages.Shared.AgentStateMark.Legend)
        {
            Assert.Contains(means, page, StringComparison.Ordinal);
        }
    }

    private static async Task<string> LinesAsync(string path)
    {
        using var console = new TranscriptConsole();
        console.Broker.Registry.Add(
            new Wire.RegistryRow { Name = "orders@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        using var client = console.CreateClient();

        return await client.GetStringAsync(new Uri(path, UriKind.Relative), Ct);
    }
}
