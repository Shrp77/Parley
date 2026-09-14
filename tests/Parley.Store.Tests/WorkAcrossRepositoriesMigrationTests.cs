using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 008 carries a working 0.4 store into 0.5 rather than rebuilding it wrongly.
/// </summary>
/// <remarks>
/// <para>
/// 008 rebuilds <c>exchanges</c> and <c>messages</c>, the two tables every other one hangs from, on stores
/// holding real work. Every failure mode of that is silent: a row mapped to the wrong outcome, an open
/// exchange closed by the dedupe, a trigger not put back, an identifier handed out twice. None raises
/// anything, and a fresh database — which is what every other test here opens — exercises none of them.
/// </para>
/// <para>
/// So these build a store at schema 7 from the embedded migrations, write the traffic 0.4 wrote, and only
/// then let <see cref="SchemaMigrator"/> bring it up.
/// </para>
/// </remarks>
public sealed class WorkAcrossRepositoriesMigrationTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000Z";
    private const string Later = "2026-01-01T00:05:00.0000000Z";

    private const string Question =
        """{"intent":"where tax is added","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"If gross I stop adding tax at invoice time; if net I keep it."}""";

    private readonly SqliteConnection _connection;
    private readonly long _largestMessageId;

    public WorkAcrossRepositoriesMigrationTests()
    {
        _connection = AtSchemaSeven();

        Work.Register(
            _connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"), AgentName.Create("inventory@test"));

        Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{Stamp}');");

        Exchange(1, "orders@test", "billing@test", "OPEN", null, "h-open", "pricing");
        Exchange(2, "orders@test", "billing@test", "CLOSED", "answered", "h-answered", "pricing");
        Exchange(3, "billing@test", "inventory@test", "CLOSED", "out_of_scope", "h-scope", null);
        Exchange(4, "orders@test", "inventory@test", "CLOSED", "ghosted", "h-ghost", "pricing");

        // A question asked again while its first asking was orphaned: 0.4 held both, and the
        // distinctness index of 0.5 admits one.
        Exchange(5, "orders@test", "billing@test", "ORPHANED", null, "h-reask", "pricing");
        Exchange(6, "orders@test", "billing@test", "OPEN", null, "h-reask", "pricing");

        Message(1, "question", "orders@test", "billing@test", Question, "pricing");
        Message(1, "clarify", "billing@test", "orders@test", """{"text":"gross of which tax?"}""", "pricing");
        Message(2, "answer", "billing@test", "orders@test", """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""", "pricing");

        // Written last, so it holds the highest identifier: the one a sequence restarted from what was
        // copied would hand out again.
        Message(null, "notify", "orders@test", "billing@test", """{"text":"deploy finished"}""", "pricing");

        Execute(
            $"""
            INSERT INTO size_refusals (agent, type, refusals, worst, total, cap, last_utc) VALUES
                ('orders@test', 'question', 2, 9000, 17500, 8000, '{Stamp}'),
                ('orders@test', 'notify',   3, 9500, 27000, 8000, '{Later}'),
                ('billing@test', 'answer',  1, 8100, 8100, 8000, '{Stamp}');
            """);

        _largestMessageId = Scalar<long>("SELECT max(id) FROM messages;");

        Assert.Equal(1, SchemaMigrator.Migrate(_connection));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_store_is_at_the_latest_schema()
    {
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(_connection));
    }

    [Fact]
    public void An_open_question_becomes_an_open_request_with_its_terms()
    {
        Assert.Equal(
            "orders@test|billing@test|OPEN||pricing|900000|14400000|h-open",
            Row("SELECT requester, receiver, state, coalesce(outcome, ''), conversation_id, uncollected_flag_ms, stalled_flag_ms, request_hash FROM exchanges WHERE id = 1;"));
    }

    [Theory]
    [InlineData(2, "complete")]
    [InlineData(3, "blocked")]
    [InlineData(4, "closed")]
    public void A_closed_exchange_keeps_what_its_resolution_meant(long id, string outcome)
    {
        // answered finished the work; out_of_scope and unknown said it could not be done by the one asked;
        // ghosted said nobody came back, which is what a person closing it says now.
        Assert.Equal($"CLOSED|{outcome}", Row($"SELECT state, outcome FROM exchanges WHERE id = {id};"));
    }

    [Fact]
    public void Of_a_question_asked_twice_the_one_0_4_held_open_stays_open()
    {
        // The probe of this migration's first draft closed the live one and kept the orphan open.
        Assert.Equal("CLOSED|closed", Row("SELECT state, outcome FROM exchanges WHERE id = 5;"));
        Assert.Equal("OPEN", Row("SELECT state FROM exchanges WHERE id = 6;"));
    }

    [Fact]
    public void An_exchange_that_stood_alone_is_given_a_conversation_of_its_own()
    {
        Assert.Equal("standalone-exchange-3", Row("SELECT conversation_id FROM exchanges WHERE id = 3;"));
        Assert.Equal("CLOSED", Row("SELECT state FROM conversations WHERE id = 'standalone-exchange-3';"));
        Assert.Equal(2L, Scalar<long>("SELECT count(*) FROM conversation_participants WHERE conversation_id = 'standalone-exchange-3';"));
    }

    [Fact]
    public void Messages_on_exchanges_are_carried_under_their_new_types_and_nothing_else_is()
    {
        // Notifications belonged to no exchange and nothing answered them, so there is nothing in 0.5 they
        // could be without inventing a Result nobody sent.
        Assert.Equal(
            "clarify:1,request:1,result:1",
            Row("SELECT group_concat(type || ':' || n, ',') FROM (SELECT type, count(*) AS n FROM messages GROUP BY type ORDER BY type);"));

        Assert.Equal(Question, Row("SELECT payload_json FROM messages WHERE type = 'request';"));
    }

    [Fact]
    public void The_tables_0_4_kept_for_notifications_and_referrals_are_gone()
    {
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE name IN ('notify_volume', 'message_referrals');"));
    }

    [Fact]
    public void Refusals_of_a_question_and_a_notification_are_one_request_row()
    {
        Assert.Equal(
            "orders@test|request|5|9500|44500",
            Row("SELECT agent, type, refusals, worst, total FROM size_refusals WHERE agent = 'orders@test';"));

        Assert.Equal("result", Row("SELECT type FROM size_refusals WHERE agent = 'billing@test';"));
    }

    [Theory]
    [InlineData("trigger", "tr_request_is_well_formed")]
    [InlineData("trigger", "tr_result_is_well_formed")]
    [InlineData("trigger", "tr_result_recorded")]
    [InlineData("trigger", "tr_an_outcome_has_its_result")]
    [InlineData("trigger", "tr_closed_exchange_stays_closed")]
    [InlineData("trigger", "tr_closed_conversation_stays_closed")]
    [InlineData("trigger", "tr_open_pair_cap")]
    [InlineData("index", "ux_open_request")]
    [InlineData("index", "ux_message_dedup")]
    [InlineData("index", "ux_exchange_conversation")]
    public void Every_guard_is_in_place_after_the_rebuild(string type, string name)
    {
        // A guard not put back fails open: nothing errors, and the rule it carried is simply gone.
        Assert.Equal(1L, Scalar<long>($"SELECT count(*) FROM sqlite_schema WHERE type = '{type}' AND name = '{name}';"));
    }

    [Fact]
    public void A_message_identifier_is_never_handed_out_twice()
    {
        // The notification holding the highest identifier was not carried. A sequence restarted from what
        // was copied would give its identifier to the next message.
        Assert.True(Scalar<long>("SELECT seq FROM sqlite_sequence WHERE name = 'messages';") >= _largestMessageId);
    }

    [Fact]
    public async Task Work_in_flight_can_be_finished_after_the_upgrade()
    {
        // The point of carrying it: billing still owes orders on exchange 1, and sends the Result 0.5 asks for.
        var exchanges = new ExchangeRepository(_connection);
        var open = await exchanges.GetAsync(1, cancellationToken: TestContext.Current.CancellationToken);

        var result = await Work.CloseAsync(
            _connection, open!, DateTimeOffset.Parse(Later, CultureInfo.InvariantCulture),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Id > _largestMessageId);
        Assert.Equal(ExchangeOutcome.Complete, (await exchanges.GetAsync(1, cancellationToken: TestContext.Current.CancellationToken))!.Outcome);
    }

    [Fact]
    public void The_cap_the_schema_enforces_is_the_one_the_code_states()
    {
        Message(1, "clarify", "orders@test", "billing@test", $$"""{"text":"{{new string('x', MessageLimits.MaxPayloadCharacters - 20)}}"}""", "pricing", Later);

        Assert.Throws<SqliteException>(() => Message(
            1, "clarify", "orders@test", "billing@test", $$"""{"text":"{{new string('y', MessageLimits.MaxPayloadCharacters)}}"}""", "pricing", Later));
    }

    [Fact]
    public void The_rebuild_left_the_database_sound()
    {
        Assert.Equal("ok", Row("PRAGMA integrity_check;"));

        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        using var reader = check.ExecuteReader();

        Assert.False(reader.Read(), "the rebuild left a row referring to something absent");
    }

    /// <summary>A store built from the embedded migrations up to 007, exactly as a 0.4 broker left it.</summary>
    private static SqliteConnection AtSchemaSeven()
    {
        const string Prefix = "Parley.Store.Migrations.";

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 7)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version = 7;";
        version.ExecuteNonQuery();

        return connection;
    }

    private void Exchange(long id, string asker, string answerer, string state, string? resolution, string hash, string? conversation) =>
        Execute(
            """
            INSERT INTO exchanges
                (id, pair_key, asker, answerer, owed_by, state, delivery_window_ms, working_window_ms,
                 grace_period_ms, question_hash, opened_utc, expires_utc, closed_utc, resolution, conversation_id)
            VALUES
                ($id, min($asker, $answerer) || char(31) || max($asker, $answerer), $asker, $answerer, $answerer,
                 $state, 900000, 14400000, 10800000, $hash, $opened, $opened,
                 CASE WHEN $state = 'CLOSED' THEN $closed END, $resolution, $conversation);
            """,
            ("$id", id), ("$asker", asker), ("$answerer", answerer), ("$state", state), ("$hash", hash),
            ("$opened", Stamp), ("$closed", Later), ("$resolution", resolution), ("$conversation", conversation));

    private int _hashes;

    private void Message(long? exchange, string type, string from, string to, string payload, string? conversation, string at = Stamp) =>
        Execute(
            """
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                 conversation_id, created_utc, delivered_utc)
            VALUES ($exchange, $type, $from, $to, 'claude-opus-5', $payload, $hash, $conversation, $at, $at);
            """,
            ("$exchange", exchange), ("$type", type), ("$from", from), ("$to", to), ("$payload", payload),
            ("$hash", (++_hashes).ToString("x64", CultureInfo.InvariantCulture)), ("$conversation", conversation), ("$at", at));

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private string Row(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), $"no row for: {sql}");

        return string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)));
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
