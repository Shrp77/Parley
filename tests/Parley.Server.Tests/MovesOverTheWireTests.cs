using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>That a move works over the wire, tells an agent only at its next collection, and wakes nobody (SPEC.md §5.0).</summary>
[Integration]
public sealed class MovesOverTheWireTests
{
    private const string Orders = "orders@test";
    private const string Billing = "billing@test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_moved_exchange_carries_on_in_its_new_conversation_and_its_agent_is_told_once()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var opened = await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "net or gross?"), cancellationToken: Ct);
        var exchange = Assert.Single(opened.Outcomes).ExchangeId;
        var source = Assert.Single(opened.Outcomes).ConversationId;

        var preview = new Wire.PreviewMoveRequest { Source = source };
        preview.Exchanges.Add(exchange);
        var previewed = await operators.PreviewMoveAsync(preview, cancellationToken: Ct);

        Assert.Equal(string.Empty, previewed.Refused);
        Assert.True(Assert.Single(previewed.Exchanges).Open);

        var move = new Wire.MoveExchangesRequest { Source = source, Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(exchange);
        var moved = await operators.MoveExchangesAsync(move, cancellationToken: Ct);

        Assert.Equal(string.Empty, moved.Refused);
        Assert.Equal(1, moved.Exchanges);

        var inbox = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);
        var notice = Assert.Single(inbox.MoveNotices);

        Assert.Equal(exchange, notice.ExchangeId);
        Assert.Equal(source, notice.FromConversation);
        Assert.Equal("tax-questions", notice.ToConversation);
        Assert.Equal("tax-questions", Assert.Single(inbox.Messages).Message.ConversationId);

        // The requester holds no message about it and is still told, and neither is told twice.
        Assert.Single((await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct)).MoveNotices);
        Assert.Empty((await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct)).MoveNotices);

        var export = await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "tax-questions" }, cancellationToken: Ct);

        Assert.Equal(source, export.Parent);
        Assert.True(export.ParentByMove);
        Assert.Equal(exchange, Assert.Single(Assert.Single(export.Moves).Exchanges));
    }

    [Fact]
    public async Task A_refused_move_answers_with_the_reason_and_not_a_failure()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        var operators = broker.ConnectAsOperator();

        var move = new Wire.MoveExchangesRequest { Source = "nowhere", Destination = "x", NewConversation = true };
        move.Exchanges.Add(1);

        Assert.Contains("no conversation", (await operators.MoveExchangesAsync(move, cancellationToken: Ct)).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_handler_for_a_move_nudges_nobody()
    {
        // Every other operation that changes an agent's work nudges it. A move must not (SPEC.md §5.0), and what stops the
        // next edit adding one is this, because a nudge that wakes nobody in a test still wakes an agent in use.
        var service = File.ReadAllText(Repository("src/Parley.Server/OperatorsService.cs")).ReplaceLineEndings("\n");
        var handler = service[service.IndexOf("Task<Wire.MoveExchangesReply> MoveExchanges(", StringComparison.Ordinal)..];

        handler = handler[..handler.IndexOf("\n    }\n", StringComparison.Ordinal)];

        Assert.DoesNotContain("Nudge", handler, StringComparison.Ordinal);
    }

    private static string Repository(string relative)
    {
        var directory = AppContext.BaseDirectory;

        while (directory is { Length: > 0 } && !File.Exists(Path.Combine(directory, "Parley.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory!, relative);
    }
}
