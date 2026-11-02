using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 005 upgrades a schema 4 store in place, keeps every row, and leaves the two tables it rebuilds with
/// everything they had but the old limit (SPEC.md §9.2, rule 8.4).
/// </summary>
/// <remarks>
/// A live store holds work in flight, and this migration rebuilds its largest table, so each thing a rebuild can quietly
/// lose has its own test: a row, an id counter, a trigger, an index.
/// </remarks>
[Integration]
public sealed class MigrationFiveTests : IDisposable
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string When = "2026-10-28T09:00:00.0000000Z";

    private readonly SqliteConnection _connection = SchemaFour();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Every_row_is_kept_as_it_was_and_every_table_passes_its_checks()
    {
        Seed();

        var before = Dump();

        Migrate();

        var after = Dump();

        Assert.Equal(before.Keys, after.Keys);

        foreach (var table in before.Keys)
        {
            Assert.Equal(before[table], after[table]);
        }

        Assert.Equal("ok", _connection.ExecuteScalar<string>("PRAGMA integrity_check;"));
        Assert.Empty(_connection.Query("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public void Every_trigger_and_index_comes_back_with_its_text_unchanged()
    {
        const string Objects =
            """
            SELECT type || ' ' || name || ' ' || coalesce(sql, '') FROM sqlite_master
             WHERE type IN ('trigger', 'index') AND name <> 'tr_message_length_recorded'
               AND name <> 'ix_message_lengths_conversation'
             ORDER BY type, name;
            """;

        var before = _connection.Query<string>(Objects).Select(Normal).ToArray();

        Migrate();

        Assert.Equal(before, _connection.Query<string>(Objects).Select(Normal).ToArray());

        static string Normal(string sql) => sql.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_tables_differ_only_in_the_limit()
    {
        var before = Tables();

        Migrate();

        var after = Tables();

        Assert.Equal(before["messages"].Replace("<= 8000", "<= 512000", StringComparison.Ordinal), after["messages"]);

        Assert.Equal(
            before["ownership_questions"]
                .Replace("<= 8000", "<= 512000", StringComparison.Ordinal)
                .Replace("rule 8.4's\n  -- cap because", "rule 8.4's\n  -- hard limit because", StringComparison.Ordinal),
            after["ownership_questions"]);

        Dictionary<string, string> Tables() => _connection
            .Query<(string Name, string Sql)>(
                "SELECT name AS Name, sql AS Sql FROM sqlite_master WHERE name IN ('messages', 'ownership_questions');")
            .ToDictionary(
                t => t.Name,
                t => t.Sql.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("messages_005", "messages", StringComparison.Ordinal)
                    .Replace("ownership_questions_005", "ownership_questions", StringComparison.Ordinal)
                    .Replace("\"messages\"", "messages", StringComparison.Ordinal)
                    .Replace("\"ownership_questions\"", "ownership_questions", StringComparison.Ordinal));
    }

    [Fact]
    public void An_id_retention_removed_is_still_never_given_again()
    {
        var exchange = Seed();

        Message(exchange, "gone");
        var highest = _connection.ExecuteScalar<long>("SELECT max(id) FROM messages;");
        _connection.Execute("DELETE FROM messages WHERE id = @highest;", new { highest });

        Migrate();

        Message(exchange, "after");

        Assert.Equal(highest + 1, _connection.ExecuteScalar<long>("SELECT max(id) FROM messages;"));
    }

    [Fact]
    public void A_store_with_no_messages_still_counts_from_where_it_had_reached()
    {
        var exchange = Seed();
        var highest = _connection.ExecuteScalar<long>("SELECT max(id) FROM messages;");
        _connection.Execute("DELETE FROM messages;");

        Migrate();

        Message(exchange, "after");

        Assert.Equal(highest + 1, _connection.ExecuteScalar<long>("SELECT max(id) FROM messages;"));
    }

    [Fact]
    public void The_limit_is_the_number_the_code_states()
    {
        var exchange = Seed();

        Migrate();

        Message(exchange, Padded(MessageLimits.MaxPayloadCharacters));

        var over = Assert.Throws<SqliteException>(() => Message(exchange, Padded(MessageLimits.MaxPayloadCharacters + 1)));

        Assert.Contains("CHECK", over.Message, StringComparison.OrdinalIgnoreCase);

        static string Padded(int total) => new('x', total - """{"ask":""}""".Length);
    }

    [Fact]
    public void Lengths_are_recorded_for_what_the_store_already_held_in_order()
    {
        var exchange = Seed();

        Message(exchange, "second");

        Migrate();

        var rows = _connection.Query<(long Position, long Characters)>(
            "SELECT position AS Position, characters AS Characters FROM message_lengths ORDER BY message_id;").ToArray();

        Assert.Equal(_connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"), rows.Length);
        Assert.Equal(Enumerable.Range(1, rows.Length).Select(i => (long)i), rows.Select(r => r.Position));

        Assert.Equal(
            _connection.Query<long>("SELECT length(payload_json) FROM messages ORDER BY id;"),
            rows.Select(r => r.Characters));
    }

    [Fact]
    public void A_new_message_is_recorded_after_the_last_and_its_record_holds_no_words()
    {
        var exchange = Seed();

        Migrate();

        Message(exchange, "a secret nobody should keep");

        Assert.Equal(2, _connection.ExecuteScalar<long>("SELECT max(position) FROM message_lengths;"));

        var columns = _connection.Query<(string Name, string Type)>(
            "SELECT name AS Name, type AS Type FROM pragma_table_info('message_lengths');").ToArray();

        Assert.DoesNotContain(columns, c => c.Name.Contains("payload", StringComparison.Ordinal));

        var everything = string.Join(
            ' ',
            _connection.Query("SELECT * FROM message_lengths;")
                .SelectMany(row => ((IDictionary<string, object>)row).Values)
                .Select(value => value?.ToString()));

        Assert.DoesNotContain("secret", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_outlives_its_message_and_goes_with_its_agent()
    {
        var exchange = Seed();

        Migrate();

        _connection.Execute("DELETE FROM messages WHERE exchange_id = @exchange;", new { exchange });

        Assert.Equal(1, _connection.ExecuteScalar<long>("SELECT count(*) FROM message_lengths;"));

        // Position keeps counting from the record, not from what is left of the conversation.
        Message(exchange, "after retention");

        Assert.Equal(2, _connection.ExecuteScalar<long>("SELECT max(position) FROM message_lengths;"));
    }

    [Fact]
    public void Triggers_moved_by_the_rebuild_still_refuse_what_they_refused()
    {
        var exchange = Seed();

        Migrate();

        var empty = Assert.Throws<SqliteException>(() => Message(exchange, "   "));
        Assert.Contains("a request carries an ask", empty.Message, StringComparison.Ordinal);

        var duplicate = Assert.Throws<SqliteException>(() => _connection.Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                                  conversation_id, created_utc)
            SELECT exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash, conversation_id,
                   created_utc
              FROM messages LIMIT 1;
            """));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);

        var question = Assert.Throws<SqliteException>(() => _connection.Execute(
            """
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
            VALUES ('orders@test', 'pricing', @exchange, 'asked', @When);
            """,
            new { exchange, When }));
        Assert.Contains("a question is the facilitator", question.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_answer_to_the_ownership_question_can_be_stored()
    {
        Seed();
        SeedQuestion();

        var before = _connection.QuerySingle<(string State, string Asked)>(
            "SELECT state AS State, asked_utc AS Asked FROM ownership_questions;");

        Migrate();

        Assert.Equal(
            before,
            _connection.QuerySingle<(string State, string Asked)>(
                "SELECT state AS State, asked_utc AS Asked FROM ownership_questions;"));

        var words = new string('x', 100_000);

        _connection.Execute(
            "UPDATE ownership_questions SET state = 'answered', answer = @words, flattened = @words;",
            new { words });

        Assert.Equal(100_000, _connection.ExecuteScalar<long>("SELECT length(answer) FROM ownership_questions;"));
    }

    [Fact]
    public void A_hundred_thousand_messages_upgrade_inside_a_minute()
    {
        var exchange = Seed();

        using (var transaction = _connection.BeginTransaction())
        {
            _connection.Execute(
                """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100000)
                INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                                      conversation_id, created_utc)
                SELECT @exchange, 'clarify', 'orders@test', 'billing@test', 'test', '{"text":"' || printf('%0500d', i) || '"}',
                       printf('%064x', i + 1000000), 'pricing', @When
                  FROM n;
                """,
                new { exchange, When },
                transaction);

            transaction.Commit();
        }

        var clock = Stopwatch.StartNew();

        Migrate();

        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(1), $"Took {clock.Elapsed}.");
        Assert.Equal(100_001, _connection.ExecuteScalar<long>("SELECT count(*) FROM message_lengths;"));
    }

    private long Seed()
    {
        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));

        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', @When);
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES
              ('pricing', 'orders@test', @When),
              ('pricing', 'billing@test', @When);
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test',
                    'billing@test', 'OPEN', 'pricing', @When, 900000, 14400000, 'hash-1');
            """,
            new { When });

        var exchange = _connection.ExecuteScalar<long>("SELECT id FROM exchanges;");

        Message(exchange, "first");

        return exchange;
    }

    private void SeedQuestion()
    {
        _connection.Execute(
            """
            INSERT INTO agents (name, adapter, tier, status) VALUES ('parley-facilitator@test', 'parley-facilitator', 'pull', 'active');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('owns-orders', 'OPEN', @When);
            INSERT INTO conversation_participants (conversation_id, agent, joined_utc) VALUES
              ('owns-orders', 'parley-facilitator@test', @When),
              ('owns-orders', 'orders@test', @When);
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('orders@test' || char(31) || 'parley-facilitator@test', 'parley-facilitator@test', 'orders@test',
                    'orders@test', 'OPEN', 'owns-orders', @When, 900000, 14400000, 'hash-2');
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, asked_utc)
            SELECT 'orders@test', 'owns-orders', id, 'asked', @When FROM exchanges WHERE conversation_id = 'owns-orders';
            """,
            new { When });
    }

    private void Message(long exchange, string ask)
    {
        var hash = Hash[..^8] + _connection.ExecuteScalar<long>("SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'messages'), 0) + 1;")
            .ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

        _connection.Execute(
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                                  conversation_id, created_utc)
            VALUES (@exchange, 'request', 'orders@test', 'billing@test', 'test',
                    json_object('ask', @ask), @hash, 'pricing', @When);
            """,
            new { exchange, ask, hash, When });
    }

    private Dictionary<string, string[]> Dump()
    {
        var tables = _connection.Query<string>(
            """
            SELECT name FROM sqlite_master
             WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('message_lengths')
             ORDER BY name;
            """);

        return tables.ToDictionary(
            table => table,
            table => _connection.Query($"SELECT * FROM \"{table}\" ORDER BY 1, 2;")
                .Select(row => string.Join('|', ((IDictionary<string, object>)row).Select(c => $"{c.Key}={c.Value}")))
                .ToArray());
    }

    private void Migrate()
    {
        var script = Script(".005_soft_message_limit.sql");

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = script + "\nPRAGMA user_version = 5;";
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    private static string Script(string file)
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static SqliteConnection SchemaFour()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");

        foreach (var file in new[]
        {
            ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql", ".003_file_problems.sql", ".004_asked_once.sql"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = Script(file);
            command.ExecuteNonQuery();
        }

        connection.Execute("PRAGMA user_version = 4;");

        return connection;
    }
}
