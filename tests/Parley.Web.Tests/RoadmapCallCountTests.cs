using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That a roadmap's page costs the same number of broker calls however many items it has (plan, Performance Goals).
/// </summary>
/// <remarks>
/// <b>Each item's facts come with the roadmap</b>, so the page reads the roadmap and the wait-for graph and nothing per
/// item. A page that exported every item's conversation would be fine at five items and slow at fifty, which is the
/// difference this counts. Item conversations load in their own frames, only when an item is opened.
/// </remarks>
[Integration]
public sealed class RoadmapCallCountTests
{
    private static RoadmapConsole Confirmed(int items)
    {
        var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        for (var number = 1; number <= items; number++)
        {
            detail.Items.Add(new Wire.RoadmapItemDetail
            {
                Id = number,
                Number = number,
                Position = number,
                Text = $"Do step {number}",
                IntendedAgent = "billing@test",
                ConversationName = $"rm1-{number}",
                Facts = new Wire.ItemFacts { ConversationExists = number % 2 == 0, OpenExchanges = number % 2 },
            });
        }

        console.Broker.Details[1] = detail;
        console.Broker.Roadmaps.Add(new Wire.RoadmapSummary
        {
            Id = 1, Number = 1, Title = "Billing export", Revision = 1, LiveItems = items, StartedItems = items / 2,
        });

        return console;
    }

    private static async Task<int> CallsFor(int items)
    {
        using var console = Confirmed(items);
        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Roadmaps/Roadmap?project={RoadmapConsole.Project}&id=1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Contains($"Do step {items}", page, StringComparison.Ordinal);

        return console.Broker.Calls;
    }

    [Fact]
    public async Task Fifty_items_cost_the_same_calls_as_five()
    {
        // Must fail against reading anything per item.
        Assert.Equal(await CallsFor(5), await CallsFor(50));
    }
}
