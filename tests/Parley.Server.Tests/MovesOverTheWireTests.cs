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
        Assert.Equal(1, moved.Messages);
        Assert.Equal(
            "tax-questions",
            Assert.Single((await operators.ExportAsync(new Wire.ExportRequest { ConversationId = source }, cancellationToken: Ct)).Children));
    }

    [Fact]
    public async Task A_result_after_a_move_lands_in_the_new_conversation()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var opened = await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "net or gross?"), cancellationToken: Ct);
        var exchange = Assert.Single(opened.Outcomes).ExchangeId;
        var source = Assert.Single(opened.Outcomes).ConversationId;

        var move = new Wire.MoveExchangesRequest { Source = source, Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(exchange);
        Assert.Equal(string.Empty, (await operators.MoveExchangesAsync(move, cancellationToken: Ct)).Refused);

        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);

        var said = await billing.Client.ResultAsync(Requests.Result(Billing, exchange), cancellationToken: Ct);

        Assert.True(said.Ok, said.Refusal.ToString());

        var there = await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "tax-questions" }, cancellationToken: Ct);

        Assert.Equal(2, there.Messages.Count);
        Assert.Equal(Wire.ExchangeState.Closed, Assert.Single(there.Exchanges).State);
        Assert.Empty((await operators.ExportAsync(new Wire.ExportRequest { ConversationId = source }, cancellationToken: Ct)).Messages);
    }

    [Fact]
    public async Task A_list_and_an_export_carry_the_parent_whether_it_closed_and_where_a_note_goes()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var opened = await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "net or gross?"), cancellationToken: Ct);
        var exchange = Assert.Single(opened.Outcomes).ExchangeId;
        var source = Assert.Single(opened.Outcomes).ConversationId;

        var move = new Wire.MoveExchangesRequest { Source = source, Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(exchange);
        Assert.Equal(string.Empty, (await operators.MoveExchangesAsync(move, cancellationToken: Ct)).Refused);

        Assert.True((await operators.ForceConcludeAsync(new Wire.ForceConcludeRequest { ConversationId = source }, cancellationToken: Ct)).Changed);

        var listed = (await operators.ListConversationsAsync(new Wire.ListConversationsRequest(), cancellationToken: Ct)).Conversations;

        Assert.Equal(source, listed.Single(c => c.ConversationId == "tax-questions").Parent);
        Assert.False(listed.Single(c => c.ConversationId == "tax-questions").Closed);
        Assert.True(listed.Single(c => c.ConversationId == source).Closed);

        var export = await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "tax-questions" }, cancellationToken: Ct);

        Assert.NotEqual(0, Assert.Single(export.Moves).FirstMessage);
    }

    [Fact]
    public async Task A_preview_says_why_a_group_grew_and_a_spin_off_can_be_taken_back()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var billing2 = await AttachedClient.AttachAsync(broker, "billing@other", cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var opened = await orders.Client.RequestAsync(Requests.Open(Orders, "billing", "are you on the new price?"), cancellationToken: Ct);

        Assert.Equal(2, opened.Outcomes.Count);

        var preview = new Wire.PreviewMoveRequest { Source = opened.Outcomes[0].ConversationId };
        preview.Exchanges.Add(opened.Outcomes[0].ExchangeId);

        var previewed = await operators.PreviewMoveAsync(preview, cancellationToken: Ct);

        Assert.Equal(2, previewed.Exchanges.Count);
        Assert.Single(previewed.Exchanges, e => e.AddedBecause.Length > 0);

        var link = new Wire.SpinOffRequest { Parent = opened.Outcomes[0].ConversationId, Child = "never-sent" };

        Assert.Equal(string.Empty, (await operators.SpinOffAsync(link, cancellationToken: Ct)).Refused);
        Assert.True((await operators.AbandonSpinOffAsync(link, cancellationToken: Ct)).Changed);
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
    public async Task An_export_names_the_conversation_the_other_half_of_a_settled_pair_is_now_in()
    {
        var person = Parley.Core.PersonSeat.On(Parley.Core.ProjectName.Create("test")).Value;

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var seat = await AttachedClient.AttachAsync(broker, person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        var question = Assert.Single((await orders.Client.RequestAsync(Requests.Open(Orders, person, "which rate?", "pricing"), cancellationToken: Ct)).Outcomes).ExchangeId;
        var line = Assert.Single((await seat.Client.RequestAsync(Requests.Open(person, Orders, "drop the old rate", "pricing"), cancellationToken: Ct)).Outcomes).ExchangeId;

        _ = await orders.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Orders }, cancellationToken: Ct);

        var reply = Requests.Result(Orders, line);
        reply.Settles.Add(question);
        Assert.Equal([question], (await orders.Client.ResultAsync(reply, cancellationToken: Ct)).Settled);

        var move = new Wire.MoveExchangesRequest { Source = "pricing", Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(question);
        Assert.Equal(string.Empty, (await operators.MoveExchangesAsync(move, cancellationToken: Ct)).Refused);

        var here = Assert.Single((await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "pricing" }, cancellationToken: Ct)).SettledElsewhere);
        var there = Assert.Single((await operators.ExportAsync(new Wire.ExportRequest { ConversationId = "tax-questions" }, cancellationToken: Ct)).SettledElsewhere);

        Assert.Equal((question, line, "tax-questions"), (here.ExchangeId, here.SettledBy, here.ConversationId));
        Assert.Equal((question, line, "pricing"), (there.ExchangeId, there.SettledBy, there.ConversationId));
    }

    [Fact]
    public async Task A_move_starts_no_turn_and_posts_no_notice()
    {
        var patience = TimeSpan.FromSeconds(5);

        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "test", ["billing"], Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct, startedBy: node);
        var operators = broker.ConnectAsOperator();

        var exchange = Assert.Single((await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "net or gross?", "pricing"), cancellationToken: Ct)).Outcomes).ExchangeId;

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, (await node.WaitForCommandAsync(patience)).CommandCase);

        // Collected, so whatever asks for this agent next asks for a new turn, and it holds work still open that a move will take.
        _ = await billing.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = Billing }, cancellationToken: Ct);

        int noticed;

        lock (billing.Notices)
        {
            noticed = billing.Notices.Count;
        }

        var move = new Wire.MoveExchangesRequest { Source = "pricing", Destination = "tax-questions", NewConversation = true };
        move.Exchanges.Add(exchange);
        Assert.Equal(string.Empty, (await operators.MoveExchangesAsync(move, cancellationToken: Ct)).Refused);

        // A Request after it is a fence, not a sleep: a stream is in order, so what a move asked for would come first.
        _ = await orders.Client.RequestAsync(Requests.Open(Orders, Billing, "and the rest?", "tax-questions"), cancellationToken: Ct);

        var next = await node.WaitForCommandAsync(patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, next.CommandCase);
        Assert.Contains(Orders, next.Turn.Because, StringComparison.Ordinal);

        _ = await billing.WaitForNoticeAsync(patience, notice => notice.MessagesWaiting > 0 && Since(billing, noticed).Contains(notice));

        Assert.All(Since(billing, noticed), notice => Assert.True(notice.MessagesWaiting > 0, "a notice with nothing waiting came of the move"));
    }

    private static List<Wire.Notice> Since(AttachedClient client, int count)
    {
        lock (client.Notices)
        {
            return [.. client.Notices.Skip(count)];
        }
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
