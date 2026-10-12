using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the project has a settings page offering the agents, and that a new conversation is marked so the page can tick the default.</summary>
public sealed class DefaultAgentTests
{
    private static TranscriptConsole Project()
    {
        var console = new TranscriptConsole();

        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "orders@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "human-user@alpha", Adapter = Parley.Core.PersonSeat.Adapter, Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);

        return console;
    }

    [Fact]
    public async Task The_settings_page_offers_the_agents_but_not_the_person()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Settings?project={TranscriptConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("name=\"default\" value=\"orders@beta\"", page, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\" class=\"default-box\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"radio\"", page, StringComparison.Ordinal);
        Assert.Contains("Default conversation agent:", page, StringComparison.Ordinal);
        Assert.Contains("id=\"default-dialog\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"human-user@alpha\"", page, StringComparison.Ordinal);
        Assert.Contains("Ask every agent again what it owns", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_page_links_to_its_settings()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={TranscriptConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains($"/Projects/Settings?project={TranscriptConsole.Project}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_conversation_is_marked_new_so_the_default_can_be_ticked()
    {
        using var console = Project();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={TranscriptConsole.Project}&id=nothing-yet", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("data-new=\"true\"", page, StringComparison.Ordinal);
    }
}
