using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>That the home page has a Projects tab with the create form and an Agents tab listing each project's agents.</summary>
[Integration]
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

        // The page is drawn before any broker is asked; the agents of a project arrive as a fragment.
        var shell = await client.GetStringAsync(new Uri("/?tab=agents", UriKind.Relative), TestContext.Current.CancellationToken);
        var page = await client.GetStringAsync(new Uri("/?tab=agents&handler=AgentTable", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Reading the agents", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("orders", shell, StringComparison.Ordinal);
        Assert.Contains("orders", page, StringComparison.Ordinal);
        Assert.DoesNotContain("human-user@alpha", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"NewProject\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_view_is_a_grid_with_its_create_in_the_rail_and_its_filters_before_the_rows()
    {
        using var console = new TranscriptConsole();
        console.Broker.Registry.Add(
            new Wire.RegistryRow { Name = "orders@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active });
        using var client = console.CreateClient();

        // All Agents has nothing to create: registering an agent is done inside a project, so its create panel is drawn only
        // there.
        foreach (var (path, creates) in new[] { ("/", true), ("/?tab=agents", false), ("/Inbox", true) })
        {
            var page = await client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

            // In the page's markup, by their class attributes. This once passed on the names in the inline stylesheet,
            // which came first in the page; feature 042 moved the styles out, and the markup is what was meant.
            var actions = page.IndexOf("class=\"rail-actions\"", StringComparison.Ordinal);
            var create = page.IndexOf("class=\"rail-act rail-act-main\"", StringComparison.Ordinal);
            var main = page.IndexOf("<main", StringComparison.Ordinal);
            var filters = page.IndexOf("view-filters\"", StringComparison.Ordinal);
            var head = page.IndexOf("class=\"dgrid-head\"", StringComparison.Ordinal);

            Assert.True(filters > main && filters < head, $"{path}: the filters must come in the page, before the grid header");

            // Feature 042: a page's create opens from the top of the rail, never as a card in the page.
            if (creates)
            {
                Assert.True(actions >= 0 && actions < create && create < main, $"{path}: the create panel must be in the rail's page actions");
            }
            else
            {
                Assert.Equal(-1, create);
            }
        }
    }

    [Fact]
    public async Task The_agents_filters_narrow_the_rows_by_text_and_by_project()
    {
        using var console = new TranscriptConsole();
        console.Broker.Registry.AddRange(
        [
            new Wire.RegistryRow { Name = "orders@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
            new Wire.RegistryRow { Name = "billing@beta", Adapter = "claude-code", Tier = Wire.AgentTier.Full, Status = Wire.AgentStatus.Active },
        ]);
        using var client = console.CreateClient();

        var all = await client.GetStringAsync(new Uri("/?tab=agents&handler=AgentTable", UriKind.Relative), TestContext.Current.CancellationToken);
        var text = await client.GetStringAsync(new Uri("/?tab=agents&q=bill&handler=AgentTable", UriKind.Relative), TestContext.Current.CancellationToken);
        var elsewhere = await client.GetStringAsync(new Uri("/?tab=agents&scope=no-such-project", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains(">orders<", all, StringComparison.Ordinal);
        Assert.Contains(">billing<", all, StringComparison.Ordinal);
        Assert.Contains(">billing<", text, StringComparison.Ordinal);
        Assert.DoesNotContain(">orders<", text, StringComparison.Ordinal);
        Assert.DoesNotContain(">billing<", elsewhere, StringComparison.Ordinal);
        Assert.Contains("No agent matches these filters.", elsewhere, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_page_is_drawn_without_asking_a_broker_and_each_row_arrives_on_its_own()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var shell = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var row = await client.GetStringAsync(new Uri("/?handler=Row&name=alpha", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("Checking", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("data-state=\"down\"", shell, StringComparison.Ordinal);
        Assert.Contains("Not answering", row, StringComparison.Ordinal);
        Assert.Contains("dgrid-detail", row, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5405", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_projects_columns_are_project_agents_conversations_status()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var shell = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        var at = new[] { "Project</span>", "Agents</span>", "Conversations</span>", "Status</span>" }
            .Select(h => shell.IndexOf(h, shell.IndexOf("class=\"dgrid-head\"", StringComparison.Ordinal), StringComparison.Ordinal))
            .ToList();

        Assert.All(at, i => Assert.True(i > 0));
        Assert.Equal(at.Order().ToList(), at);
    }

    [Fact]
    public async Task Inside_a_project_the_rail_has_its_own_agents_and_the_global_one_is_all_agents()
    {
        using var console = new TranscriptConsole();
        using var client = console.CreateClient();

        var inside = await client.GetStringAsync(new Uri("/?tab=agents&project=alpha", UriKind.Relative), TestContext.Current.CancellationToken);
        var outside = await client.GetStringAsync(new Uri("/?tab=agents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains("title=\"All Agents\"", inside, StringComparison.Ordinal);
        Assert.Contains("href=\"/?tab=agents&amp;project=alpha\"", inside, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"f-scope\"", inside, StringComparison.Ordinal);
        Assert.Contains("id=\"f-scope\"", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/?tab=agents&amp;project=", outside, StringComparison.Ordinal);
    }
}
