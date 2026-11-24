using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>Freeze all names an agent whose process no node was there to stop (SPEC.md §14).</summary>
public sealed partial class FreezingOnTheWebTests
{
    [Fact]
    public async Task Freezing_everyone_names_an_agent_whose_process_no_node_was_there_to_stop()
    {
        // A process a node started, whose node is not joined: the broker cannot ask anybody to stop it, and freezing that one
        // agent says so. Freezing everyone counted it and said nothing (review, lens 1 run B on 44c8e1e6).
        using var console = new RoadmapConsole();
        var row = Row(Orders);
        row.Presence = Wire.AgentPresence.Running;
        console.Broker.Registry.Add(row);
        console.Broker.FreezeProcess = "no_node";

        var after = await ConfirmAsync(console, "freezeall", RoadmapConsole.Project, []);

        Assert.Contains("1 of 1 agent frozen", after, StringComparison.Ordinal);
        Assert.Contains($"'{Orders}': ", after, StringComparison.Ordinal);
        Assert.Contains("no node is joined to stop it", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unfreezing_everyone_says_it_reaches_every_project_that_serves_each_agent()
    {
        // One process serves every project an agent is in, so Unfreeze all in one project releases it in the others too;
        // the page said nothing of them (review, lens 2 run B on 44c8e1e6).
        using var console = Everyone();
        using var client = console.CreateClient();

        var before = System.Net.WebUtility.HtmlDecode(
            await client.GetStringAsync(Address("unfreezeall", RoadmapConsole.Project, string.Empty), Ct));

        Assert.Contains("Each is unfrozen in every project that serves it", before, StringComparison.Ordinal);
    }
}
