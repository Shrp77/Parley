using Xunit;
using Wire = Parley.Transport;

namespace Parley.Web.Tests;

/// <summary>
/// That a roadmap item whose work has started opens its conversation as it stands, even with no messages left in it
/// (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// Retention removes a finished conversation's messages and keeps its exchanges. The page once decided "not started"
/// from the messages it read, so done work came back with the item's words in the box and its agent ticked, one press
/// of Send from being asked again. The terminal reads the broker's fact about the item, and so does the page now.
/// </remarks>
public sealed class AStartedItemIsNeverOfferedAgainTests
{
    [Fact]
    public async Task A_started_item_whose_messages_retention_removed_prefills_nothing()
    {
        using var console = new RoadmapConsole();
        var detail = new Wire.RoadmapDetail { Id = 1, Number = 1, Title = "Billing export", Revision = 1 };

        detail.Items.Add(new Wire.RoadmapItemDetail
        {
            Id = 1,
            Number = 1,
            Position = 1,
            Text = "Add CSV export",
            IntendedAgent = "billing@test",
            ConversationName = "rm1-1",
            Facts = new Wire.ItemFacts { ConversationExists = true, CompleteExchanges = 1 },
        });

        console.Broker.Details[1] = detail;

        // What the broker exports once retention removed the messages: the exchange stays, closed complete.
        console.Broker.ExportExchanges.Add(new Wire.ExchangeSnapshot
        {
            ExchangeId = 7,
            State = Wire.ExchangeState.Closed,
            OpenedUtc = "2026-01-01T00:00:00Z",
            ClosedUtc = "2026-01-01T01:00:00Z",
            Outcome = Wire.ExchangeOutcome.Complete,
        });

        using var client = console.CreateClient();

        var page = await client.GetStringAsync(
            new Uri($"/Conversations/Detail?project={RoadmapConsole.Project}&id=rm1-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(">Add CSV export</textarea>", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("name=\"Targets\" value=\"billing@test\" checked=\"checked\"", page);
    }
}
