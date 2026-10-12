using Xunit;

namespace Parley.Web.Tests;

/// <summary>That the project page leads with what is waiting for the person and offers the agent acts in one place.</summary>
public sealed class ProjectPageLayoutTests
{
    [Fact]
    public async Task Roadmaps_lead_the_page_and_the_title_leads_to_naming()
    {
        using var console = new RoadmapConsole();
        console.Broker.Propose(7, "orders@test", "Billing export", ["Add CSV export"]);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Projects/Agents?project={RoadmapConsole.Project}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var roadmaps = page.IndexOf("aria-label=\"Roadmaps\"", StringComparison.Ordinal);
        var conversations = page.IndexOf("Conversations in this project", StringComparison.Ordinal);

        Assert.True(roadmaps >= 0 && conversations > roadmaps, "roadmaps lead, conversations follow");
        Assert.Contains("/Roadmaps/Proposal?project=alpha&amp;id=7", page, StringComparison.Ordinal);
        Assert.Contains("class=\"title-link\"", page, StringComparison.Ordinal);
    }
}
