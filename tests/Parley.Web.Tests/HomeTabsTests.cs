using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the home page has a Projects tab with the create form and an Agents tab listing each project's agents.</summary>
public sealed class HomeTabsTests
{
    [Fact]
    public async Task The_projects_tab_is_the_default_and_offers_to_create_a_project()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/?tab=agents\"", page, StringComparison.Ordinal);
        Assert.Contains("Create a project", page, StringComparison.Ordinal);
        Assert.Contains("name=\"NewProject\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agents_tab_lists_agents_but_not_the_person_and_has_no_create_form()
    {
        using var console = new TranscriptConsole();
        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "orders@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "human-user@alpha", Adapter = Parley.Core.PersonSeat.Adapter, Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(new Uri("/?tab=agents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("orders", page, StringComparison.Ordinal);
        Assert.DoesNotContain("human-user@alpha", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"NewProject\"", page, StringComparison.Ordinal);
    }
}
