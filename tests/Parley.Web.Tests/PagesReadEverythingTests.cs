using System.Net;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Lens 3 sweep on 3b752970: the graph, the exchange panel and a roadmap's checks read what they say they read.</summary>
[Integration]
public sealed class PagesReadEverythingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_graph_says_a_project_is_unreachable_when_its_registry_cannot_be_read()
    {
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(new Wire.RegistryRow { Name = "orders@test", Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        console.Broker.FailsOn = "/parley.v1.Operators/DescribeRegistry";

        using var client = console.CreateClient();
        var graph = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Contains("project, unreachable", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_graph_says_a_project_was_read_in_part_when_what_its_working_agents_owe_cannot_be_read()
    {
        using var console = new RoadmapConsole();

        console.Broker.Registry.Add(new Wire.RegistryRow
        {
            Name = "orders@test", Adapter = "mcp", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active,
            Presence = Wire.AgentPresence.Running, TurnsReported = true,
            TurnRunningSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });
        console.Broker.FailsOn = "/parley.v1.Operators/DescribeWaiting";

        using var client = console.CreateClient();
        var graph = await client.GetStringAsync(new Uri("/Graph", UriKind.Relative), Ct);

        Assert.Contains("project, could not be read in full", graph, StringComparison.Ordinal);
        Assert.Contains(">orders@test</text>", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_the_broker_could_not_be_asked_about_says_so_and_not_that_there_is_none()
    {
        using var console = new RoadmapConsole();
        console.Broker.FailsOn = "/parley.v1.Operators/LocateExchange";

        using var client = console.CreateClient();
        var panel = await client.GetStringAsync(
            new Uri($"/Exchanges/Panel?project={RoadmapConsole.Project}&id=50", UriKind.Relative), Ct);

        Assert.Contains("Exchange #50 could not be read", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("There is no exchange #50", panel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_lists_every_check_of_it_newest_first_archived_and_on_later_pages_and_nothing_else()
    {
        using var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", IntendedAgent = "billing@test", ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        });
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 1 });
        console.Broker.ConversationPageSize = 1;
        console.Broker.OmitsArchived = true;

        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "roadmap-1-check-20261101-090000", Archived = true, FirstActivityUtc = "2026-11-01T09:00:00.0000000+00:00",
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "about-roadmap-1-check-1", FirstActivityUtc = "2026-11-03T09:00:00.0000000+00:00",
        });
        console.Broker.Conversations.Add(new Wire.ConversationSummary
        {
            ConversationId = "roadmap-1-check-20261102-090000", FirstActivityUtc = "2026-11-02T09:00:00.0000000+00:00",
        });

        using var client = console.CreateClient();
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(
            new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1", UriKind.Relative), Ct));

        var newer = page.IndexOf(">roadmap-1-check-20261102-090000</a>", StringComparison.Ordinal);
        var older = page.IndexOf(">roadmap-1-check-20261101-090000</a>", StringComparison.Ordinal);

        Assert.True(newer >= 0, "the check on a later page is not listed");
        Assert.True(older >= 0, "the archived check is not listed");
        Assert.True(newer < older, "the checks are not newest first");
        Assert.DoesNotContain(">about-roadmap-1-check-1</a>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Numbering_a_proposals_lines_against_a_roadmap_that_cannot_be_read_says_so()
    {
        using var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 0 });
        console.Broker.Propose(7, "orders@test", "Billing export", ["Add CSV export"], target: "Billing export");
        console.Broker.FailsOn = "/parley.v1.Operators/Roadmap";

        using var client = console.CreateClient();
        var page = await client.GetStringAsync(
            new Uri($"/Roadmaps/Proposal?project={RoadmapConsole.Project}&id=7&keep=true", UriKind.Relative), Ct);

        Assert.Contains("class=\"unreachable\" role=\"alert\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lines_numbered_against_a_roadmap_are_confirmed_at_the_revision_that_was_read()
    {
        // The list says revision 1; the roadmap read to number the lines is at revision 2. The numbers are revision 2's.
        using var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 2 };

        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1, Number = 1, Position = 1, Text = "Add CSV export", ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = false },
        });
        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary { Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = 1 });
        console.Broker.Propose(7, "orders@test", "Billing export", ["Add CSV export"], target: "Billing export");

        using var client = console.CreateClient();
        var page = await client.GetStringAsync(
            new Uri($"/Roadmaps/Proposal?project={RoadmapConsole.Project}&id=7&keep=true", UriKind.Relative), Ct);

        Assert.Contains("<input type=\"hidden\" name=\"Revision\" value=\"2\" />", page, StringComparison.Ordinal);
    }
}
