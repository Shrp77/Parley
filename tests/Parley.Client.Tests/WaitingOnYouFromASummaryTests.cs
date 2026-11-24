using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>What waits on the person reads the same from the broker's summary of a conversation as from the whole of it.</summary>
/// <remarks>
/// Review, performance lens (0.62.7): the list read every conversation with an open exchange whole, on every Inbox open. The
/// summary carries the newest message each sender wrote in each exchange, and the agent's newest is what the person owes a
/// reply to.
/// </remarks>
[Unit]
public sealed class WaitingOnYouFromASummaryTests
{
    private static readonly AgentName Me = AgentName.Create("human-user@alpha");
    private static readonly AgentName Orders = AgentName.Create("orders@alpha");
    private static readonly AgentName Billing = AgentName.Create("billing@alpha");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static Message Said(long id, long exchange, MessageType type, AgentName from, AgentName to, int minutes) =>
        new(id, exchange, type, from, to, "test", "{\"text\":\"m" + id + "\"}", "talk", Now.AddMinutes(minutes), null);

    [Fact]
    public void The_questions_are_the_same_from_the_summary_as_from_the_whole_conversation()
    {
        Message[] whole =
        [
            Said(1, 10, MessageType.Request, Orders, Me, 1),
            Said(2, 10, MessageType.Clarify, Me, Orders, 2),
            Said(3, 10, MessageType.Clarify, Orders, Me, 3),
            Said(4, 11, MessageType.Request, Me, Billing, 4),
            Said(5, 11, MessageType.Clarify, Billing, Me, 5),
            Said(6, 11, MessageType.Clarify, Me, Billing, 6),
            Said(7, 11, MessageType.Clarify, Billing, Me, 7),
            Said(8, 12, MessageType.Request, Orders, Me, 8),
        ];

        ExchangeSnapshot[] exchanges =
        [
            new(10, ExchangeState.Open, Now, null),
            new(11, ExchangeState.Open, Now, null),
            new(12, ExchangeState.Open, Now, null),
        ];

        WaitingEdge[] edges = [new(Orders, Me, 10), new(Billing, Me, 11), new(Orders, Me, 12)];

        // The newest message each sender wrote in each exchange: what the broker's summary returns.
        var summary = whole
            .GroupBy(m => (m.ExchangeId, m.From))
            .Select(g => g.OrderBy(m => m.Id).Last())
            .OrderBy(m => m.Id)
            .ToList();

        static object[] Shape(IReadOnlyList<WaitingOn> waiting) =>
            [.. waiting.SelectMany(w => w.Questions.Select(q => (object)(w.Peer, q.Exchange, q.At, q.AskedUtc, q.Asked)))];

        var fromWhole = Shape(WaitingOnYou.From(new ConversationExport(whole, exchanges), edges, Me));
        var fromSummary = Shape(WaitingOnYou.From(new ConversationExport(summary, exchanges), edges, Me));

        Assert.True(whole.Length > summary.Count, "the summary has to be smaller than the whole");
        Assert.Equal(3, fromWhole.Length);
        Assert.Equal(fromWhole, fromSummary);
    }
}
