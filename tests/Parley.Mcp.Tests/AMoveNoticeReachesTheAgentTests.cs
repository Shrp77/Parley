using Xunit;
using Wire = Parley.Transport;

namespace Parley.Mcp.Tests;

/// <summary>That an agent's inbox tool says which of its open exchanges moved, and what that means for it (SPEC.md §5.0).</summary>
[Integration]
public sealed class AMoveNoticeReachesTheAgentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task More_than_a_collection_carries_are_counted_and_said()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        var operators = new Wire.Operators.OperatorsClient(Grpc.Net.Client.GrpcChannel.ForAddress(fixture.Broker));

        for (var i = 0; i < 52; i++)
        {
            var sent = await orders.RequestAsync("billing@test", $"Question {i}?", cancellationToken: ct);
            var move = new Wire.MoveExchangesRequest
            {
                Source = sent["conversation_id"]!.GetValue<string>(),
                Destination = $"moved-{i}",
                NewConversation = true,
            };

            move.Exchanges.Add(sent["exchange_id"]!.GetValue<long>());

            Assert.Equal(string.Empty, (await operators.MoveExchangesAsync(move, cancellationToken: ct)).Refused);
        }

        var inbox = await billing.InboxAsync(cancellationToken: ct);

        Assert.Equal(50, inbox["move_notices"]!.AsArray().Count);
        Assert.Contains("2 more are waiting", inbox["move_notices_note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inbox_names_the_exchange_and_both_conversations_once_and_says_files_stayed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync("billing@test", "Does an invoice carry the rate at time of sale?", cancellationToken: ct);
        var exchange = sent["exchange_id"]!.GetValue<long>();
        var source = sent["conversation_id"]!.GetValue<string>();

        var move = new Wire.MoveExchangesRequest { Source = source, Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(exchange);

        var moved = await new Wire.Operators.OperatorsClient(Grpc.Net.Client.GrpcChannel.ForAddress(fixture.Broker))
            .MoveExchangesAsync(move, cancellationToken: ct);

        Assert.Equal(string.Empty, moved.Refused);

        var inbox = await billing.InboxAsync(cancellationToken: ct);
        var notice = Assert.Single(inbox["move_notices"]!.AsArray())!;

        Assert.Equal(exchange, notice["exchange_id"]!.GetValue<long>());
        Assert.Equal(source, notice["from"]!.GetValue<string>());
        Assert.Equal("tax-questions", notice["to"]!.GetValue<string>());

        var note = inbox["move_notices_note"]!.GetValue<string>();

        Assert.Contains("Nothing is owed back", note, StringComparison.Ordinal);
        Assert.Contains("Files stayed under the old conversation", note, StringComparison.Ordinal);

        Assert.Null((await billing.InboxAsync(cancellationToken: ct))["move_notices"]);
    }
}
