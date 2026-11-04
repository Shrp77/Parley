using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>That the person, and only the person, can really move whole exchanges between conversations (SPEC.md §5.0).</summary>
[Integration]
public sealed class MovingExchangesTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly AgentName Person = AgentName.Create("human-user@test");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly HashSet<AgentName> _started = [];

    public MovingExchangesTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00')
                ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
              ('orders@test', 'test', 'full', 'active'),
              ('billing@test', 'test', 'full', 'active'),
              ('inventory@test', 'test', 'full', 'active');
            INSERT INTO agents (name, adapter, tier, status) VALUES ('human-user@test', 'parley-console', 'full', 'active')
                ON CONFLICT DO NOTHING;
            """);

        _broker = new Broker(_connection, BrokerOptions.Default);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> AskAsync(AgentName from, AgentName to, string ask, string conversation = "pricing")
    {
        await _broker.StartAsync(from, T0, Ct);

        var sent = await _broker.RequestAsync(from, to, "model-a", $$"""{"ask":"{{ask}}"}""", T0, conversation, Ct);

        return sent.Exchange!.Id;
    }

    private Task<MoveOutcome> MoveAsync(string destination, bool isNew, params long[] exchanges) =>
        _console.MoveExchangesAsync("pricing", destination, isNew, exchanges, T0.AddMinutes(5), Ct);

    private async Task CloseAsync(long exchange, AgentName receiver)
    {
        // Starting hands back what the agent collected, so an agent already started is not started again.
        if (!_started.Contains(receiver))
        {
            await _broker.StartAsync(receiver, T0, Ct);
        }

        var done = await _broker.ResultAsync(
            exchange, receiver, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"done"}]}""", T0.AddMinutes(1), Ct);

        Assert.True(_connection.ExecuteScalar<string>("SELECT state FROM exchanges WHERE id = @exchange;", new { exchange }) == "CLOSED", done.ToString());
    }

    private void PlaceFile(string conversation, AgentName placer) => _connection.Execute(
        """
        INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES (@conversation, @conversation, '2026-01-01T00:00:00.000Z');
        INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
        VALUES (@conversation, 'NOTES.MD', 'notes.md', 10, @sha, @placer, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 0);
        """,
        new { conversation, placer = placer.Value, sha = new string('a', 64) });

    private string Row(string sql, object? arguments = null) => _connection.ExecuteScalar<string>(sql, arguments) ?? string.Empty;

    [Fact]
    public async Task Whole_exchanges_move_and_nothing_about_a_message_changes_but_where_it_is()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var staying = await AskAsync(Orders, Inventory, "how many are left?");
        _ = await _broker.ClarifyAsync(going, Billing, "model-a", """{"text":"which ledger?"}""", T0.AddMinutes(1), Ct);

        const string Fields =
            "SELECT id || '|' || exchange_id || '|' || type || '|' || from_agent || '|' || to_agent || '|' || payload_json || '|' || content_hash || '|' || created_utc || '|' || coalesce(delivered_utc, '') AS Whole FROM messages WHERE exchange_id = @going ORDER BY id;";

        var before = _connection.Query<string>(Fields, new { going }).ToList();

        var moved = await MoveAsync("tax-questions", true, going);

        Assert.True(moved.Moved, moved.Refused);
        Assert.Equal((1, 2), (moved.Exchanges, moved.Messages));
        Assert.Equal(before, _connection.Query<string>(Fields, new { going }));

        var there = await _console.ExportAsync("tax-questions", Ct);
        var here = await _console.ExportAsync("pricing", Ct);

        Assert.Equal(2, there.Messages.Count);
        Assert.All(there.Messages, m => Assert.Equal("tax-questions", m.ConversationId));
        Assert.Equal(staying, Assert.Single(here.Messages).ExchangeId);
        Assert.Equal(going, Assert.Single(there.Exchanges).ExchangeId);
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Both_conversations_show_the_move_and_a_new_one_has_the_source_as_its_parent()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var there = await _console.ExportAsync("tax-questions", Ct);
        var here = await _console.ExportAsync("pricing", Ct);
        var record = Assert.Single(there.Moves!);

        Assert.Equal(("pricing", "tax-questions", "human-user@test"), (record.Source, record.Destination, record.MovedBy));
        Assert.Equal([going], record.Exchanges);
        Assert.Equal(record.Id, Assert.Single(here.Moves!).Id);
        Assert.Equal("pricing", there.Links!.Parent);
        Assert.Equal(ConversationLinkKind.Move, there.Links.How);
        Assert.Equal(["tax-questions"], here.Links!.Children);

        var listed = await _console.ListConversationsAsync(cancellationToken: Ct);

        Assert.Equal("pricing", listed.Single(c => c.Id == "tax-questions").Parent);
        Assert.Null(listed.Single(c => c.Id == "pricing").Parent);
    }

    [Fact]
    public async Task Moving_to_an_existing_conversation_links_nothing_and_they_can_be_moved_back()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");

        Assert.True((await MoveAsync("stock", false, going)).Moved);
        Assert.Null((await _console.ExportAsync("stock", Ct)).Links!.Parent);

        var back = await _console.MoveExchangesAsync("stock", "pricing", false, [going], T0.AddMinutes(6), Ct);

        Assert.True(back.Moved, back.Refused);
        Assert.Equal(going, Assert.Single((await _console.ExportAsync("pricing", Ct)).Exchanges).ExchangeId);
        Assert.Equal(2, (await _console.ExportAsync("pricing", Ct)).Moves!.Count);
    }

    [Fact]
    public async Task A_group_goes_whole_or_not_at_all_and_the_preview_says_why_it_grew()
    {
        var one = await AskAsync(Orders, Billing, "are you on the new price?");
        var two = await AskAsync(Orders, Inventory, "are you on the new price?");
        _connection.Execute("UPDATE exchanges SET request_group = 'g1' WHERE id IN (@one, @two);", new { one, two });

        var preview = await _console.PreviewMoveAsync("pricing", [one], Ct);

        Assert.Equal([one, two], preview.Exchanges.Select(e => e.Id));
        Assert.Null(preview.Exchanges[0].AddedBecause);
        Assert.NotNull(preview.Exchanges[1].AddedBecause);

        var partial = await MoveAsync("elsewhere", true, one);

        Assert.False(partial.Moved);
        Assert.Contains("left behind", partial.Refused, StringComparison.Ordinal);
        Assert.Equal("pricing", Row("SELECT conversation_id FROM exchanges WHERE id = @one;", new { one }));
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversations WHERE id = 'elsewhere';"));

        Assert.True((await MoveAsync("elsewhere", true, one, two)).Moved);
    }

    [Fact]
    public async Task A_preview_changes_nothing_and_names_the_files_that_stay()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        PlaceFile("pricing", Orders);
        var before = Row("SELECT conversation_id FROM exchanges WHERE id = @going;", new { going });

        var preview = await _console.PreviewMoveAsync("pricing", [going], Ct);

        Assert.Null(preview.Refused);
        Assert.True(Assert.Single(preview.Exchanges).Open);
        Assert.Contains("gross or net", preview.Exchanges[0].FirstWords, StringComparison.Ordinal);
        Assert.Equal(["notes.md"], preview.FilesStaying);
        Assert.Equal(before, Row("SELECT conversation_id FROM exchanges WHERE id = @going;", new { going }));
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversation_moves;"));
    }

    [Theory]
    [InlineData("pricing", false, "already in that conversation")]
    [InlineData("nowhere", false, "no conversation called")]
    [InlineData("has space", true, "letters, digits")]
    [InlineData("owns-billing", true, "owns-")]
    public async Task A_destination_that_cannot_take_them_is_refused_and_nothing_moves(string destination, bool isNew, string says)
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        var moved = await MoveAsync(destination, isNew, going);

        Assert.False(moved.Moved);
        Assert.Contains(says, moved.Refused, StringComparison.Ordinal);
        Assert.Equal("pricing", Row("SELECT conversation_id FROM exchanges WHERE id = @going;", new { going }));
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversation_moves;"));
    }

    [Fact]
    public async Task A_new_name_already_taken_is_refused()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");

        Assert.Contains("already", (await MoveAsync("stock", true, going)).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_list_and_an_exchange_from_elsewhere_are_refused()
    {
        var other = await AskAsync(Orders, Inventory, "how many?", "stock");
        _ = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.False((await MoveAsync("elsewhere", true)).Moved);
        Assert.Contains("not in pricing", (await MoveAsync("elsewhere", true, other)).Refused, StringComparison.Ordinal);
        Assert.Contains("not in pricing", (await MoveAsync("elsewhere", true, 999)).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_conversation_takes_nothing_and_stays_closed_when_entries_leave_it()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var inClosed = await AskAsync(Orders, Inventory, "how many?", "stock");
        Assert.True(await _console.ForceConcludeAsync("stock", null, T0.AddMinutes(1), Ct));

        Assert.Contains("stays closed", (await MoveAsync("stock", false, going)).Refused, StringComparison.Ordinal);

        var out_ = await _console.MoveExchangesAsync("stock", "archive-of-stock", true, [inClosed], T0.AddMinutes(2), Ct);

        Assert.True(out_.Moved, out_.Refused);
        Assert.Equal("CLOSED", Row("SELECT state FROM conversations WHERE id = 'stock';"));
    }

    [Fact]
    public async Task Storage_refuses_the_moves_the_broker_refuses_should_the_broker_ever_be_wrong()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");
        Assert.True(await _console.ForceConcludeAsync("stock", null, T0.AddMinutes(1), Ct));

        void Attempt(string destination)
        {
            using var transaction = _connection.BeginTransaction();

            _connection.Execute("PRAGMA defer_foreign_keys = ON;", transaction: transaction);
            _connection.Execute(
                "UPDATE exchanges SET conversation_id = @destination WHERE id = @going;",
                new { destination, going },
                transaction);
        }

        Assert.Contains("stays closed", Assert.Throws<SqliteException>(() => Attempt("stock")).Message, StringComparison.Ordinal);

        _connection.Execute("INSERT INTO conversations (id, state, opened_utc) VALUES ('owns-x', 'OPEN', '2026-11-03T09:00:00Z');");
        Assert.Contains("ownership", Assert.Throws<SqliteException>(() => Attempt("owns-x")).Message, StringComparison.Ordinal);

        _connection.Execute(
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO conversations (id, state, opened_utc) VALUES ('rm1-1', 'OPEN', '2026-11-03T09:00:00Z');
            INSERT INTO roadmap_item_conversations (item_id, conversation_id) VALUES (1, 'rm1-1');
            PRAGMA foreign_keys = ON;
            """);
        Assert.Contains("roadmap", Assert.Throws<SqliteException>(() => Attempt("rm1-1")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roadmap_items_conversation_is_refused_in_both_directions()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _connection.Execute(
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO conversations (id, state, opened_utc) VALUES ('rm1-1', 'OPEN', '2026-11-03T09:00:00Z');
            INSERT INTO roadmap_item_conversations (item_id, conversation_id) VALUES (1, 'rm1-1');
            PRAGMA foreign_keys = ON;
            """);

        Assert.Contains("roadmap", (await MoveAsync("rm1-1", false, going)).Refused, StringComparison.Ordinal);
        Assert.Contains("roadmap", (await _console.MoveExchangesAsync("rm1-1", "out", true, [going], T0, Ct)).Refused, StringComparison.Ordinal);
        Assert.Contains("roadmap", (await _console.PreviewMoveAsync("rm1-1", [going], Ct)).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_emptied_conversation_stays_listed_and_says_what_left()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var pricing = (await _console.ListConversationsAsync(cancellationToken: Ct)).Single(c => c.Id == "pricing");

        Assert.Equal(0, pricing.Messages);
        Assert.Empty(pricing.Participants);
        Assert.Single((await _console.ExportAsync("pricing", Ct)).Moves!);
        Assert.Equal("OPEN", Row("SELECT state FROM conversations WHERE id = 'pricing';"));
    }

    [Fact]
    public async Task Membership_follows_and_a_file_placer_stays()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        var placerGoes = await AskAsync(Inventory, Person, "and the rest?");
        PlaceFile("pricing", Person);

        Assert.True((await MoveAsync("tax-questions", true, going, placerGoes)).Moved);

        var here = _connection.Query<string>("SELECT agent FROM conversation_participants WHERE conversation_id = 'pricing' ORDER BY agent;");
        var there = _connection.Query<string>("SELECT agent FROM conversation_participants WHERE conversation_id = 'tax-questions' ORDER BY agent;");

        // The person has no exchange left here, and stays for the file they placed. Billing has neither, and goes.
        Assert.Equal(["human-user@test", "inventory@test", "orders@test"], here);
        Assert.Equal(["billing@test", "human-user@test", "inventory@test", "orders@test"], there);
    }

    [Fact]
    public async Task A_file_placed_somewhere_else_keeps_nobody_in_the_source()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        _ = await AskAsync(Person, Inventory, "and elsewhere?", "stock");
        PlaceFile("stock", Billing);

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        Assert.Equal(0, _connection.ExecuteScalar<int>(
            "SELECT count(*) FROM conversation_participants WHERE conversation_id = 'pricing' AND agent = 'billing@test';"));
    }

    [Fact]
    public async Task A_move_into_a_conversation_tells_only_the_parties_to_what_moved()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Person, Inventory, "how many?", "stock");

        Assert.True((await MoveAsync("stock", false, going)).Moved);

        Assert.Equal(
            ["billing@test", "orders@test"],
            _connection.Query<string>("SELECT agent FROM move_notices ORDER BY agent;"));
    }

    [Fact]
    public async Task A_poll_in_either_conversation_is_voided()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        _ = await AskAsync(Orders, Inventory, "how much?", "stock");
        _connection.Execute("UPDATE conversations SET state = 'CLOSING' WHERE id IN ('pricing', 'stock');");

        Assert.True((await MoveAsync("stock", false, going)).Moved);

        Assert.Equal("OPEN", Row("SELECT state FROM conversations WHERE id = 'pricing';"));
        Assert.Equal("OPEN", Row("SELECT state FROM conversations WHERE id = 'stock';"));
    }

    [Fact]
    public async Task A_recorded_cycle_follows_its_exchange_and_length_records_follow_their_messages()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        _connection.Execute(
            "INSERT INTO conversation_cycles (exchange_id, conversation_id, path, recorded_utc) VALUES (@going, 'pricing', 'a,b', '2026-11-03T09:00:00Z');",
            new { going });
        var lengths = _connection.ExecuteScalar<int>("SELECT count(*) FROM message_lengths;");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        Assert.Equal("tax-questions", Row("SELECT conversation_id FROM conversation_cycles WHERE exchange_id = @going;", new { going }));
        Assert.Equal(lengths, _connection.ExecuteScalar<int>("SELECT count(*) FROM message_lengths;"));
        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM message_lengths WHERE conversation_id = 'tax-questions' AND position = 1;"));
    }

    [Fact]
    public async Task An_agent_on_open_work_is_told_once_at_its_next_collection_and_its_answer_lands_in_the_new_place()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var inbox = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct);
        var notice = Assert.Single(inbox.MoveNotices);

        Assert.Equal((going, "pricing", "tax-questions"), (notice.Exchange, notice.From, notice.To));
        Assert.Equal("tax-questions", Assert.Single(inbox.Messages).Message.ConversationId);
        Assert.Empty((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(7), cancellationToken: Ct)).MoveNotices);

        _ = await _broker.ClarifyAsync(going, Billing, "model-a", """{"text":"which ledger?"}""", T0.AddMinutes(8), Ct);

        Assert.Equal(2, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages WHERE conversation_id = 'tax-questions';"));
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages WHERE conversation_id = 'pricing';"));
    }

    [Fact]
    public async Task A_collection_not_acknowledged_tells_again_and_a_second_collection_in_flight_does_not_take_it()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var holds = new InboxHolds();
        var (first, hold) = await _broker.CollectInboxAsync(Billing, holds, null, Ct);
        var (second, other) = await _broker.CollectInboxAsync(Billing, holds, null, Ct);

        Assert.Single(first.MoveNotices);
        Assert.Empty(second.MoveNotices);

        holds.Release(hold);
        holds.Release(other);

        Assert.Single((await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct)).MoveNotices);
    }

    [Fact]
    public async Task A_finished_exchange_tells_nobody_nor_does_the_person_or_a_retired_agent_get_a_notice()
    {
        var finished = await AskAsync(Orders, Billing, "is it gross or net?");
        await CloseAsync(finished, Billing);
        var toPerson = await AskAsync(Orders, Person, "which rate?");
        var retired = await AskAsync(Orders, Inventory, "how many?");
        _connection.Execute("UPDATE agents SET status = 'retired' WHERE name = 'inventory@test';");

        Assert.True((await MoveAsync("tax-questions", true, finished, toPerson, retired)).Moved);

        var told = _connection.Query<(string Agent, long Exchange)>(
            "SELECT agent AS Agent, exchange_id AS Exchange FROM move_notices ORDER BY exchange_id, agent;").ToList();

        Assert.Equal([("orders@test", toPerson), ("orders@test", retired)], told);
    }

    [Fact]
    public async Task An_agent_left_holding_requests_in_two_conversations_has_to_name_one()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Person, Billing, "how much?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        await _broker.StartAsync(Billing, T0, Ct);

        var refused = await _broker.RequestAsync(Billing, Inventory, "model-a", """{"ask":"anything new?"}""", T0.AddMinutes(9), null, Ct);

        Assert.False(refused.Succeeded);

        var named = await _broker.RequestAsync(Billing, Inventory, "model-a", """{"ask":"anything new?"}""", T0.AddMinutes(9), "tax-questions", Ct);

        Assert.True(named.Succeeded);
    }

    [Fact]
    public async Task Waiting_on_the_person_still_reads_after_a_move()
    {
        var going = await AskAsync(Orders, Person, "which rate?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var export = await _console.ExportAsync("tax-questions", Ct);
        var edges = await _console.DescribeWaitingAsync(Ct);

        Assert.Contains(edges, e => e.ExchangeId == going && e.WaitedOn == Person);
        Assert.Equal(going, Assert.Single(export.Exchanges).ExchangeId);
    }

    [Fact]
    public async Task Retention_counts_a_move_as_activity_though_no_message_time_changes()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        // Collected first, or the floor holds them and the rule under test is never reached.
        await _broker.StartAsync(Billing, T0, Ct);
        _started.Add(Billing);
        _ = await _broker.DrainInboxAsync(Billing, T0.AddSeconds(30), cancellationToken: Ct);
        await CloseAsync(going, Billing);
        _ = await _broker.DrainInboxAsync(Orders, T0.AddMinutes(2), cancellationToken: Ct);

        var later = T0.AddDays(20);
        Assert.True((await _console.MoveExchangesAsync("pricing", "tax-questions", true, [going], later, Ct)).Moved);

        var policy = new RetentionPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(365));

        _ = await _console.PruneAsync(policy, later.AddDays(1), Ct);
        Assert.Equal(2, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages;"));

        _ = await _console.PruneAsync(policy, later.AddDays(11), Ct);
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages;"));

        // The record holds no words, and outlives them. So does the link it made.
        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversation_moves;"));
        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversation_links;"));
    }

    [Fact]
    public async Task Destroying_a_conversation_takes_its_notices_and_leaves_the_records_and_links()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        _ = await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(9), Ct);

        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM move_notices;"));
        Assert.Single((await _console.ExportAsync("pricing", Ct)).Moves!);
        Assert.Equal(["tax-questions"], (await _console.ExportAsync("pricing", Ct)).Links!.Children);
    }

    [Fact]
    public async Task A_spin_off_that_reached_nobody_is_taken_back_and_the_name_is_free()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.Null(await _console.SpinOffAsync("pricing", "never-sent", T0, Ct));

        Assert.True(await _console.AbandonSpinOffAsync("pricing", "never-sent", Ct));

        Assert.Empty((await _console.ExportAsync("pricing", Ct)).Links!.Children);
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversations WHERE id = 'never-sent';"));

        var going = await AskAsync(Orders, Inventory, "how many?");

        Assert.True((await MoveAsync("never-sent", true, going)).Moved);
    }

    [Fact]
    public async Task A_child_with_a_file_a_participant_or_a_child_of_its_own_is_not_taken_back()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.Null(await _console.SpinOffAsync("pricing", "with-file", T0, Ct));
        PlaceFile("with-file", Orders);
        Assert.False(await _console.AbandonSpinOffAsync("pricing", "with-file", Ct));

        Assert.Null(await _console.SpinOffAsync("pricing", "with-participant", T0, Ct));
        _connection.Execute(
            "INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES ('with-participant', 'orders@test', '2026-11-03T09:00:00Z');");
        Assert.False(await _console.AbandonSpinOffAsync("pricing", "with-participant", Ct));

        Assert.Null(await _console.SpinOffAsync("pricing", "with-child", T0, Ct));
        Assert.Null(await _console.SpinOffAsync("with-child", "grandchild", T0, Ct));
        Assert.False(await _console.AbandonSpinOffAsync("pricing", "with-child", Ct));
    }

    [Fact]
    public async Task A_child_holding_anything_is_not_taken_back_nor_a_link_it_does_not_have()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.Null(await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct));
        _ = await AskAsync(Person, Orders, "what filled the disk?", "disk-cleanup");

        Assert.False(await _console.AbandonSpinOffAsync("pricing", "disk-cleanup", Ct));
        Assert.False(await _console.AbandonSpinOffAsync("elsewhere", "disk-cleanup", Ct));
        Assert.Equal("pricing", (await _console.ExportAsync("disk-cleanup", Ct)).Links!.Parent);
    }

    [Fact]
    public async Task A_conversation_entries_moved_out_of_cannot_be_called_a_spin_off()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        Assert.Contains("already", await _console.SpinOffAsync("tax-questions", "pricing", T0, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_everything_moved_out_of_to_an_existing_one_cannot_be_called_a_spin_off()
    {
        // No link is left behind by a move to something that already existed, and no participant: only the record says so.
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");

        Assert.True((await MoveAsync("stock", false, going)).Moved);
        Assert.Empty((await _console.ListConversationsAsync(cancellationToken: Ct)).Single(c => c.Id == "pricing").Participants);
        Assert.Empty((await _console.ExportAsync("pricing", Ct)).Links!.Children);

        Assert.Contains("already a conversation called pricing", await _console.SpinOffAsync("stock", "pricing", T0, Ct), StringComparison.Ordinal);
        Assert.Null((await _console.ExportAsync("pricing", Ct)).Links!.Parent);
    }

    [Fact]
    public async Task A_stub_entries_passed_through_is_not_a_stub_any_more()
    {
        // In and straight back out: empty again, with no exchange, file or participant, and only the records of the moves to say it held anything.
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.Null(await _console.SpinOffAsync("pricing", "through", T0, Ct));

        Assert.True((await MoveAsync("through", false, going)).Moved);
        Assert.True((await _console.MoveExchangesAsync("through", "pricing", false, [going], T0.AddMinutes(6), Ct)).Moved);

        Assert.Contains("already a conversation called through", await _console.SpinOffAsync("pricing", "through", T0, Ct), StringComparison.Ordinal);
        Assert.False(await _console.AbandonSpinOffAsync("pricing", "through", Ct));
    }

    [Fact]
    public async Task Taking_back_an_empty_stub_with_the_wrong_parent_leaves_it_and_the_right_parent_takes_it()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");
        Assert.Null(await _console.SpinOffAsync("pricing", "never-sent", T0, Ct));

        Assert.False(await _console.AbandonSpinOffAsync("stock", "never-sent", Ct));

        Assert.Equal("pricing", (await _console.ExportAsync("never-sent", Ct)).Links!.Parent);
        Assert.Equal(1, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversations WHERE id = 'never-sent';"));

        Assert.True(await _console.AbandonSpinOffAsync("pricing", "never-sent", Ct));

        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversations WHERE id = 'never-sent';"));
        Assert.Empty((await _console.ExportAsync("pricing", Ct)).Links!.Children);
    }

    [Fact]
    public async Task A_preview_does_not_list_a_file_in_another_conversation_as_staying()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");
        PlaceFile("stock", Orders);

        var preview = await _console.PreviewMoveAsync("pricing", [going], Ct);

        Assert.Null(preview.Refused);
        Assert.Empty(preview.FilesStaying);
        Assert.Equal(["notes.md"], (await _console.PreviewMoveAsync("stock", [.. (await _console.ExportAsync("stock", Ct)).Exchanges.Select(e => e.ExchangeId)], Ct)).FilesStaying);
    }

    [Fact]
    public async Task Asking_again_for_a_stub_that_has_since_had_work_put_in_it_is_refused()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.Null(await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct));
        _ = await AskAsync(Person, Orders, "what filled the disk?", "disk-cleanup");

        Assert.Contains("already a conversation called disk-cleanup", await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ownership_conversation_is_refused_both_ways_with_a_sentence_rather_than_an_exception()
    {
        // Storage refuses these too, and that surfaces as an exception. The sentence is what a person is meant to read.
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _connection.Execute("INSERT INTO conversations (id, state, opened_utc) VALUES ('owns-billing', 'OPEN', '2026-11-03T09:00:00Z');");

        Assert.Contains("is an ownership conversation", (await MoveAsync("owns-billing", false, going)).Refused, StringComparison.Ordinal);
        Assert.Contains("is an ownership conversation", (await _console.MoveExchangesAsync("owns-billing", "out", true, [going], T0, Ct)).Refused, StringComparison.Ordinal);
        Assert.Contains("is an ownership conversation", (await _console.PreviewMoveAsync("owns-billing", [going], Ct)).Refused, StringComparison.Ordinal);
        Assert.Equal("pricing", Row("SELECT conversation_id FROM exchanges WHERE id = @going;", new { going }));
    }

    [Fact]
    public async Task A_new_name_reserved_for_a_roadmap_item_is_refused_and_says_so()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _connection.Execute(
            """
            PRAGMA foreign_keys = OFF;
            INSERT INTO roadmap_item_conversations (item_id, conversation_id) VALUES (1, 'rm1-1');
            PRAGMA foreign_keys = ON;
            """);

        Assert.Contains("reserved for a roadmap item", (await MoveAsync("rm1-1", true, going)).Refused, StringComparison.Ordinal);
        Assert.Contains("reserved for a roadmap item", await _console.SpinOffAsync("pricing", "rm1-1", T0, Ct), StringComparison.Ordinal);
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversations WHERE id = 'rm1-1';"));
    }

    [Fact]
    public async Task A_preview_of_an_exchange_that_has_since_moved_says_so_and_lists_nothing()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var preview = await _console.PreviewMoveAsync("pricing", [going], Ct);

        Assert.Contains("may have moved already", preview.Refused, StringComparison.Ordinal);
        Assert.Empty(preview.Exchanges);
    }

    [Fact]
    public async Task A_move_record_says_the_first_message_that_moved_and_how_many_did()
    {
        // The note is drawn where the entries were, by this record, so the first moved has to be the first and not the last.
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var first = _connection.ExecuteScalar<long>("SELECT min(id) FROM messages WHERE exchange_id = @going;", new { going });
        _ = await AskAsync(Orders, Inventory, "how many?");
        _ = await _broker.ClarifyAsync(going, Billing, "model-a", """{"text":"which ledger?"}""", T0.AddMinutes(1), Ct);

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var record = Assert.Single((await _console.ExportAsync("tax-questions", Ct)).Moves!);

        Assert.Equal(first, record.FirstMessage);
        Assert.Equal(2, record.Messages);
        Assert.Equal([going], record.Exchanges);
    }

    [Fact]
    public async Task A_move_that_fails_part_way_changes_nothing_at_all()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");
        _ = await _broker.ClarifyAsync(going, Billing, "model-a", """{"text":"which ledger?"}""", T0.AddMinutes(1), Ct);
        _connection.Execute(
            "INSERT INTO conversation_cycles (exchange_id, conversation_id, path, recorded_utc) VALUES (@going, 'pricing', 'a,b', '2026-11-03T09:00:00Z');",
            new { going });

        var before = EveryRowAMoveTouches();

        // The notices are the last thing a move writes, so everything else has been done by the time this refuses.
        _connection.Execute(
            "CREATE TEMP TRIGGER tr_test_fails_the_move BEFORE INSERT ON main.move_notices BEGIN SELECT RAISE(ABORT, 'the test failed this'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() => MoveAsync("tax-questions", true, going));

        Assert.Equal(before, EveryRowAMoveTouches());

        _connection.Execute("DROP TRIGGER tr_test_fails_the_move;");

        // The same move with nothing in its way goes, so it was the failure that stopped it.
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.NotEqual(before, EveryRowAMoveTouches());
    }

    private List<string> EveryRowAMoveTouches()
    {
        string[] tables =
        [
            "conversations", "conversation_links", "conversation_moves", "conversation_move_exchanges", "conversation_participants",
            "conversation_cycles", "exchanges", "messages", "message_lengths", "move_notices",
        ];

        return
        [
            .. tables.SelectMany(table => _connection.Query($"SELECT * FROM {table} ORDER BY 1, 2;").Select(row =>
                table + ": " + string.Join("|", ((IDictionary<string, object>)row).Select(cell => cell.Key + "=" + cell.Value)))),
        ];
    }

    [Fact]
    public async Task A_length_record_takes_its_messages_place_in_the_broker_order()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");
        _ = await AskAsync(Orders, Inventory, "how much?", "stock");

        Assert.True((await MoveAsync("stock", false, going)).Moved);

        var positions = _connection.Query<long>(
            "SELECT position FROM message_lengths WHERE conversation_id = 'stock' ORDER BY message_id;").ToList();

        Assert.Equal([1L, 2L, 3L], positions);
    }

    [Fact]
    public async Task A_length_record_left_behind_closes_up_too()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        Assert.Equal(
            [1L],
            _connection.Query<long>("SELECT position FROM message_lengths WHERE conversation_id = 'pricing';"));
    }

    [Fact]
    public async Task Membership_follows_out_of_a_closed_conversation_too()
    {
        var inClosed = await AskAsync(Orders, Inventory, "how many?", "stock");
        Assert.True(await _console.ForceConcludeAsync("stock", null, T0.AddMinutes(1), Ct));

        Assert.True((await _console.MoveExchangesAsync("stock", "elsewhere", true, [inClosed], T0.AddMinutes(2), Ct)).Moved);

        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM conversation_participants WHERE conversation_id = 'stock';"));
    }

    [Fact]
    public async Task A_move_out_counts_as_activity_in_the_source_too()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var staying = await AskAsync(Orders, Inventory, "how many?");

        // Collected first, or the floor holds them and the rule under test is never reached.
        await _broker.StartAsync(Billing, T0, Ct);
        await _broker.StartAsync(Inventory, T0, Ct);
        _started.Add(Billing);
        _started.Add(Inventory);
        _ = await _broker.DrainInboxAsync(Billing, T0.AddSeconds(30), cancellationToken: Ct);
        _ = await _broker.DrainInboxAsync(Inventory, T0.AddSeconds(30), cancellationToken: Ct);
        await CloseAsync(staying, Inventory);
        await CloseAsync(going, Billing);
        _ = await _broker.DrainInboxAsync(Orders, T0.AddMinutes(2), cancellationToken: Ct);

        var later = T0.AddDays(20);
        Assert.True((await _console.MoveExchangesAsync("pricing", "tax-questions", true, [going], later, Ct)).Moved);

        _ = await _console.PruneAsync(new RetentionPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(365)), later.AddDays(1), Ct);

        Assert.Equal(2, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages WHERE conversation_id = 'pricing';"));
    }

    [Fact]
    public async Task A_delivered_notice_is_pruned_with_age_and_a_waiting_one_is_not()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        _ = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct);

        _ = await _console.PruneAsync(new RetentionPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2)), T0.AddDays(5), Ct);

        Assert.Equal(["orders@test"], _connection.Query<string>("SELECT agent FROM move_notices;"));
    }

    [Fact]
    public async Task A_collection_carries_at_most_fifty_notices_and_counts_the_rest()
    {
        var conversations = Enumerable.Range(0, 53).Select(i => $"from-{i}").ToList();

        foreach (var name in conversations)
        {
            var exchange = await AskAsync(Person, Billing, $"question {name}", name);

            Assert.True((await _console.MoveExchangesAsync(name, name + "-moved", true, [exchange], T0, Ct)).Moved);
        }

        var first = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(50, first.MoveNotices.Count);
        Assert.Equal(3, first.MoveNoticesWaiting);

        var second = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(2), cancellationToken: Ct);

        Assert.Equal(3, second.MoveNotices.Count);
        Assert.Equal(0, second.MoveNoticesWaiting);
    }

    [Fact]
    public async Task Storage_refuses_moving_an_exchange_an_ownership_question_hangs_from()
    {
        var exchange = await AskAsync(Orders, Billing, "is it gross or net?");

        // In a conversation with an ordinary name, moving to another, so only the question can refuse this. A question has
        // no business being here, which is what its own trigger says, so that one is out of the way for the test.
        _connection.Execute(
            """
            DROP TRIGGER tr_question_is_the_facilitators;
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
            VALUES ('billing@test', 'pricing', @exchange, 'asked', '2026-11-03T09:00:00Z');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('plain', 'OPEN', '2026-11-03T09:00:00Z');
            """,
            new { exchange });

        var failure = Assert.Throws<SqliteException>(() =>
        {
            using var transaction = _connection.BeginTransaction();

            _connection.Execute("PRAGMA defer_foreign_keys = ON;", transaction: transaction);
            _connection.Execute("UPDATE exchanges SET conversation_id = 'plain' WHERE id = @exchange;", new { exchange }, transaction);
        });

        Assert.Contains("ownership", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_an_agent_takes_the_notices_about_exchanges_that_went_with_it()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.Equal(2, _connection.ExecuteScalar<int>("SELECT count(*) FROM move_notices;"));

        await _console.RetireAsync(Billing, Ct);
        _ = await _console.PurgeAsync(Billing, Ct);

        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM move_notices;"));
        Assert.Single((await _console.ExportAsync("pricing", Ct)).Moves!);
        Assert.Equal(["tax-questions"], (await _console.ExportAsync("pricing", Ct)).Links!.Children);
    }

    [Fact]
    public async Task A_result_on_a_moved_exchange_closes_it_in_the_new_place()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        await CloseAsync(going, Billing);

        var there = await _console.ExportAsync("tax-questions", Ct);

        Assert.Equal(2, there.Messages.Count);
        Assert.Equal(ExchangeState.Closed, Assert.Single(there.Exchanges).State);
        Assert.Empty((await _console.ExportAsync("pricing", Ct)).Messages);
    }

    [Fact]
    public async Task Destroying_what_settled_a_question_elsewhere_leaves_it_closed_with_no_link()
    {
        var (persons, question) = await SettledPairAsync();

        Assert.True((await MoveAsync("tax-questions", true, persons)).Moved);

        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(10), Ct)).Verdict);

        var left = Assert.Single((await _console.ExportAsync("pricing", Ct)).Exchanges, e => e.ExchangeId == question);

        Assert.Equal((ExchangeState.Closed, ExchangeOutcome.Closed, (long?)null), (left.State, left.Outcome, left.SettledBy));
        Assert.Empty((await _console.ExportAsync("pricing", Ct)).SettledElsewhere!);
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));

        // And the rest of it can still go.
        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("pricing", T0.AddMinutes(11), Ct)).Verdict);
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Destroying_where_a_question_went_leaves_what_settled_it_alone()
    {
        var (persons, question) = await SettledPairAsync();

        Assert.True((await MoveAsync("tax-questions", true, question)).Moved);

        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(10), Ct)).Verdict);

        Assert.Single((await _console.ExportAsync("pricing", Ct)).Exchanges, e => e.ExchangeId == persons);
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task A_pair_each_side_of_a_move_can_be_destroyed_in_either_order()
    {
        var (persons, _) = await SettledPairAsync();

        Assert.True((await MoveAsync("tax-questions", true, persons)).Moved);

        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("pricing", T0.AddMinutes(10), Ct)).Verdict);
        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(11), Ct)).Verdict);
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Purging_somebody_unrelated_to_a_split_pair_leaves_the_question_closed_with_no_link()
    {
        // The purge dooms the conversation holding what settled it, for a reason that has nothing to do with the pair.
        var (persons, question) = await SettledPairAsync();

        await AskAsync(Person, Billing, "something else");

        Assert.True((await MoveAsync("tax-questions", true, question)).Moved);

        Assert.Equal(PurgeVerdict.Gone, (await _console.PurgeAsync(Billing, Ct)).Verdict);

        var left = Assert.Single((await _console.ExportAsync("tax-questions", Ct)).Exchanges, e => e.ExchangeId == question);

        Assert.Equal((ExchangeState.Closed, ExchangeOutcome.Closed, (long?)null), (left.State, left.Outcome, left.SettledBy));
        Assert.Empty(_connection.Query("SELECT id FROM exchanges WHERE id = @persons;", new { persons }));
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task Destroying_what_settled_a_question_elsewhere_leaves_every_other_settled_question_settled()
    {
        var (_, untouched) = await SettledPairAsync("unrelated");
        var (persons, _) = await SettledPairAsync();

        Assert.True((await MoveAsync("tax-questions", true, persons)).Moved);
        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(10), Ct)).Verdict);

        var left = Assert.Single((await _console.ExportAsync("unrelated", Ct)).Exchanges, e => e.ExchangeId == untouched);

        Assert.Equal(ExchangeOutcome.Settled, left.Outcome);
        Assert.NotNull(left.SettledBy);
    }

    [Fact]
    public async Task Purging_somebody_leaves_every_settled_question_they_had_no_part_in_settled()
    {
        var (_, untouched) = await SettledPairAsync("unrelated");
        var (_, question) = await SettledPairAsync();

        await AskAsync(Person, Billing, "something else");

        Assert.True((await MoveAsync("tax-questions", true, question)).Moved);
        Assert.Equal(PurgeVerdict.Gone, (await _console.PurgeAsync(Billing, Ct)).Verdict);

        var left = Assert.Single((await _console.ExportAsync("unrelated", Ct)).Exchanges, e => e.ExchangeId == untouched);

        Assert.Equal(ExchangeOutcome.Settled, left.Outcome);
        Assert.NotNull(left.SettledBy);
    }

    [Fact]
    public async Task Destroying_a_conversation_leaves_the_notices_about_exchanges_that_went_somewhere_else()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var other = await AskAsync(Orders, Inventory, "how many?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.True((await MoveAsync("stock-questions", true, other)).Moved);

        _ = await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(9), Ct);

        Assert.Equal(
            ["inventory@test", "orders@test"],
            _connection.Query<string>("SELECT agent FROM move_notices ORDER BY agent;"));
    }

    [Fact]
    public async Task Purging_an_agent_leaves_the_notices_about_exchanges_it_had_no_part_in()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        var other = await AskAsync(Orders, Inventory, "how many?");

        // Apart, because a purge takes the whole of any conversation the agent was in.
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.True((await MoveAsync("stock-questions", true, other)).Moved);

        await _console.RetireAsync(Billing, Ct);
        _ = await _console.PurgeAsync(Billing, Ct);

        Assert.Equal(
            ["inventory@test", "orders@test"],
            _connection.Query<string>("SELECT agent FROM move_notices ORDER BY agent;"));
    }

    [Fact]
    public async Task A_delivered_notice_younger_than_the_limit_is_not_pruned()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        _ = await _broker.DrainInboxAsync(Billing, T0.AddMinutes(6), cancellationToken: Ct);

        _ = await _console.PruneAsync(new RetentionPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2)), T0.AddMinutes(7), Ct);

        Assert.Equal(
            ["billing@test", "orders@test"],
            _connection.Query<string>("SELECT agent FROM move_notices ORDER BY agent;"));
    }

    [Fact]
    public async Task A_preview_says_how_many_messages_each_exchange_holds()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        _ = await _broker.ClarifyAsync(going, Billing, "model-a", """{"text":"which market?"}""", T0.AddMinutes(1), Ct);

        Assert.Equal(2, Assert.Single((await _console.PreviewMoveAsync("pricing", [going], Ct)).Exchanges).Messages);
    }

    private async Task<(long Persons, long Question)> SettledPairAsync(string conversation = "pricing")
    {
        var persons = await AskAsync(Person, Orders, "drop the old rate", conversation);
        var question = await AskAsync(Orders, Person, "which rate?", conversation);

        _ = await _broker.ResultAsync(
            persons, Orders, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"done"}]}""", T0.AddMinutes(1), Ct, [question]);

        return (persons, question);
    }

    [Theory]
    [InlineData("owns-x", "ownership")]
    [InlineData("rm1-1", "roadmap")]
    public async Task Storage_refuses_moving_out_of_what_it_refuses_moving_into(string kept, string says)
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        // Put there past the trigger, which is what is being asked about: nothing else can put it there.
        var trigger = _connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = 'tr_a_moved_exchange_may_go_there';")!;

        _connection.Execute(
            """
            DROP TRIGGER tr_a_moved_exchange_may_go_there;
            PRAGMA foreign_keys = OFF;
            INSERT INTO conversations (id, state, opened_utc) VALUES (@kept, 'OPEN', '2026-11-03T09:00:00Z');
            UPDATE exchanges SET conversation_id = @kept WHERE id = @going;
            UPDATE messages SET conversation_id = @kept WHERE exchange_id = @going;
            PRAGMA foreign_keys = ON;
            """,
            new { kept, going });

        if (kept.StartsWith("rm", StringComparison.Ordinal))
        {
            _connection.Execute(
                """
                PRAGMA foreign_keys = OFF;
                INSERT INTO roadmap_item_conversations (item_id, conversation_id) VALUES (1, @kept);
                PRAGMA foreign_keys = ON;
                """,
                new { kept });
        }

        _connection.Execute(trigger);

        var failure = Assert.Throws<SqliteException>(() =>
        {
            using var transaction = _connection.BeginTransaction();

            _connection.Execute("PRAGMA defer_foreign_keys = ON;", transaction: transaction);
            _connection.Execute("UPDATE exchanges SET conversation_id = 'pricing' WHERE id = @going;", new { going }, transaction);
        });

        Assert.Contains(says, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archived_conversation_with_everything_moved_out_is_counted_as_it_is_listed()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.True(await _console.SetConversationArchivedAsync("pricing", true, T0.AddMinutes(9), Ct));

        Assert.Contains((await _console.ListConversationsAsync(includeArchived: true, cancellationToken: Ct)), c => c.Id == "pricing");
        Assert.Equal(1, await _console.CountArchivedConversationsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_lone_surrogate_in_a_payload_does_not_stop_the_preview()
    {
        var going = await AskAsync(Orders, Billing, """hello \ud800 there""");

        var preview = await _console.PreviewMoveAsync("pricing", [going], Ct);

        Assert.Null(preview.Refused);
        Assert.Equal(string.Empty, Assert.Single(preview.Exchanges).FirstWords);
    }

    [Fact]
    public async Task A_name_holding_two_dots_is_refused_in_a_sentence_by_a_move_and_a_spin_off()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.Contains("no '..'", (await MoveAsync("a..b", true, going)).Refused, StringComparison.Ordinal);
        Assert.Contains("no '..'", await _console.SpinOffAsync("pricing", "a..b", T0, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_whose_messages_have_gone_can_still_be_previewed_and_moved()
    {
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        _connection.Execute("DELETE FROM messages WHERE exchange_id = @going;", new { going });

        var preview = await _console.PreviewMoveAsync("pricing", [going], Ct);

        Assert.Null(preview.Refused);
        Assert.Equal((string.Empty, 0), (Assert.Single(preview.Exchanges).FirstWords, preview.Exchanges[0].Messages));

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);

        var record = Assert.Single((await _console.ExportAsync("pricing", Ct)).Moves!);

        Assert.Null(record.FirstMessage);
        Assert.Equal(0, record.Messages);
    }

    [Fact]
    public async Task A_destroyed_conversations_own_export_still_gives_its_moves_and_its_split_pairs()
    {
        var (persons, _) = await SettledPairAsync();

        Assert.True((await MoveAsync("tax-questions", true, persons)).Moved);
        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("pricing", T0.AddMinutes(10), Ct)).Verdict);

        var gone = await _console.ExportAsync("pricing", Ct);

        Assert.NotNull(gone.Destroyed);
        Assert.Equal("tax-questions", Assert.Single(gone.Moves!).Destination);
        Assert.Equal(["tax-questions"], gone.Links!.Children);
    }

    [Fact]
    public async Task A_rename_carries_a_waiting_notice_to_the_new_name()
    {
        var payments = AgentName.Create("payments@test");
        var going = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, going)).Moved);
        Assert.True(await new AgentRepository(_connection).RenameAsync(Billing, payments, cancellationToken: Ct));

        Assert.Equal(
            ["orders@test", "payments@test"],
            _connection.Query<string>("SELECT agent FROM move_notices ORDER BY agent;"));
    }

    [Fact]
    public async Task A_notices_id_is_never_given_again_so_a_late_acknowledgement_marks_nobody_elses()
    {
        var first = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.True((await MoveAsync("tax-questions", true, first)).Moved);

        var highest = _connection.ExecuteScalar<long>("SELECT max(id) FROM move_notices;");

        Assert.Equal(DestroyVerdict.Gone, (await _console.DestroyConversationAsync("tax-questions", T0.AddMinutes(9), Ct)).Verdict);
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM move_notices;"));

        var second = await AskAsync(Orders, Inventory, "how many?", "stock");

        Assert.True((await _console.MoveExchangesAsync("stock", "stock-questions", true, [second], T0.AddMinutes(10), Ct)).Moved);
        Assert.True(_connection.ExecuteScalar<long>("SELECT min(id) FROM move_notices;") > highest);
    }

    [Fact]
    public async Task A_settled_pair_no_move_has_split_is_not_said_to_be_elsewhere()
    {
        _ = await SettledPairAsync();

        Assert.Empty((await _console.ExportAsync("pricing", Ct)).SettledElsewhere!);
    }

    [Fact]
    public async Task A_settled_pair_split_by_a_move_is_still_said_from_both_sides()
    {
        // The person asks; the agent's own question to the person is settled by what the person said.
        var persons = await AskAsync(Person, Orders, "drop the old rate");
        var question = await AskAsync(Orders, Person, "which rate?");
        var settled = await _broker.ResultAsync(
            persons, Orders, "model-a", $$"""{"outcomes":[{"id":"{{PayloadShape.WholeRequest}}","outcome":"done","text":"done"}]}""", T0.AddMinutes(1), Ct, [question]);

        Assert.Equal(persons, _connection.ExecuteScalar<long>("SELECT settled_by FROM exchanges WHERE id = @question;", new { question }));
        _ = settled;

        Assert.True((await MoveAsync("tax-questions", true, question)).Moved);

        var from = Assert.Single((await _console.ExportAsync("pricing", Ct)).SettledElsewhere!);
        var to = Assert.Single((await _console.ExportAsync("tax-questions", Ct)).SettledElsewhere!);

        Assert.Equal((question, persons, "tax-questions"), (from.Exchange, from.SettledBy, from.Conversation));
        Assert.Equal((question, persons, "pricing"), (to.Exchange, to.SettledBy, to.Conversation));
    }

    [Fact]
    public async Task A_spin_off_records_the_link_sends_nothing_and_appears_in_no_list_until_something_is_in_it()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");

        Assert.Null(await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct));

        Assert.DoesNotContain(await _console.ListConversationsAsync(cancellationToken: Ct), c => c.Id == "disk-cleanup");
        Assert.Equal(0, _connection.ExecuteScalar<int>("SELECT count(*) FROM messages WHERE conversation_id = 'disk-cleanup';"));
        Assert.Equal("pricing", (await _console.ExportAsync("disk-cleanup", Ct)).Links!.Parent);
        Assert.Equal(ConversationLinkKind.SpinOff, (await _console.ExportAsync("disk-cleanup", Ct)).Links!.How);

        // Asked again after a send that was refused, and the person's first Request then goes in and lists it.
        Assert.Null(await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct));
        _ = await AskAsync(Person, Orders, "what filled the disk?", "disk-cleanup");

        var listed = (await _console.ListConversationsAsync(cancellationToken: Ct)).Single(c => c.Id == "disk-cleanup");

        Assert.Equal("pricing", listed.Parent);
    }

    [Fact]
    public async Task A_spin_off_of_a_name_that_cannot_be_used_is_refused()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        _ = await AskAsync(Orders, Inventory, "how many?", "stock");
        _ = await AskAsync(Orders, Inventory, "how much?", "gone");
        Assert.Null(await _console.SpinOffAsync("stock", "taken-child", T0, Ct));
        _ = await _console.DestroyConversationAsync("gone", T0, Ct);

        Assert.Contains("own parent", await _console.SpinOffAsync("pricing", "pricing", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("no conversation", await _console.SpinOffAsync("nowhere", "x", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("no conversation", await _console.SpinOffAsync("gone", "x", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("already", await _console.SpinOffAsync("pricing", "stock", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("already", await _console.SpinOffAsync("pricing", "taken-child", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("destroyed", await _console.SpinOffAsync("pricing", "gone", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("owns-", await _console.SpinOffAsync("pricing", "owns-x", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("letters", await _console.SpinOffAsync("pricing", "has space", T0, Ct), StringComparison.Ordinal);
        Assert.Contains("letters", await _console.SpinOffAsync("pricing", "new\n", T0, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_cascades_along_a_link()
    {
        _ = await AskAsync(Orders, Billing, "is it gross or net?");
        Assert.Null(await _console.SpinOffAsync("pricing", "disk-cleanup", T0, Ct));
        _ = await AskAsync(Person, Orders, "what filled the disk?", "disk-cleanup");

        Assert.True(await _console.ForceConcludeAsync("pricing", null, T0.AddMinutes(1), Ct));
        Assert.Equal("OPEN", Row("SELECT state FROM conversations WHERE id = 'disk-cleanup';"));

        _ = await _console.DestroyConversationAsync("pricing", T0.AddMinutes(2), Ct);

        Assert.Equal("OPEN", Row("SELECT state FROM conversations WHERE id = 'disk-cleanup';"));
        Assert.Equal("pricing", (await _console.ExportAsync("disk-cleanup", Ct)).Links!.Parent);
    }
}
