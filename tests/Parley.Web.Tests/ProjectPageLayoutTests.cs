using Xunit;

namespace Parley.Web.Tests;

/// <summary>That a project's Conversations page holds conversations and nothing that has a page of its own.</summary>
[Integration]
public sealed class ProjectPageLayoutTests
{
    [Fact]
    public async Task The_page_is_conversations_filtered_by_agent_and_nothing_with_a_page_of_its_own()
    {
        using var console = new RoadmapConsole();
        console.Broker.Propose(7, "orders@test", "Billing export", ["Add CSV export"]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("aria-label=\"Filter the conversations\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"participant\"", page, StringComparison.Ordinal);

        // Each of these is a rail item of its own now.
        Assert.DoesNotContain("aria-label=\"Roadmaps\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Agents\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"This project\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("title-link", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_on_an_agent_that_is_not_on_the_roster_is_still_shown_as_applied()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}&participant=gone@nowhere", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("<option value=\"gone@nowhere\" selected=\"selected\">", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_is_offered_in_settings()
    {
        using var console = new RoadmapConsole();
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Settings?project={RoadmapConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains("action=\"/Projects/Naming\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Destroy_is_a_button_in_warning_colours()
    {
        var markup = File.ReadAllText(RepositoryPath("src/Parley.Web/Pages/Projects/Agents.cshtml"));

        Assert.Contains("class=\"button danger\" href=\"/Conversations/Destroy", markup, StringComparison.Ordinal);
    }

    private static string RepositoryPath(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!, relative);
    }
}
