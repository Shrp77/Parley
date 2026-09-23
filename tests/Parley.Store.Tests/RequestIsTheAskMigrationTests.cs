using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 011 changes what may be written and nothing that was.
/// </summary>
/// <remarks>
/// <para>
/// This is FR-009, and it is the cheapest thing in the change to get wrong: the owner's store holds conversations
/// written under 0.5 and 0.6, and a migration that reshaped them into the new form would destroy words a participant
/// wrote. So this builds a store at schema 10 from the embedded migrations, writes the traffic those versions wrote —
/// a Request with an intent, items and a <c>done_when</c>, and a Result answering each item by identifier — and only
/// then lets <see cref="SchemaMigrator"/> bring it up.
/// </para>
/// <para>
/// Written as SQL rather than through the repository, because the repository of this build will not write what 0.6
/// wrote, which is the point of the change.
/// </para>
/// </remarks>
public sealed class RequestIsTheAskMigrationTests : IDisposable
{
    private const string Stamp = "2026-09-16T00:00:00.0000000Z";

    private const string OldRequest =
        """{"intent":"deciding where tax is added","items":[{"id":"r1","kind":"work","text":"use the order's price"},{"id":"r2","kind":"question","gate":{"on":"r1","equals":"done"},"text":"which invoice version?"}],"done_when":"invoices show the order's price and r2 names the version"}""";

    private const string OldResult =
        """{"outcomes":[{"id":"r1","outcome":"done","text":"merged"},{"id":"r2","outcome":"answered","sources":["orders@test"],"text":"schema 12"}]}""";

    private readonly SqliteConnection _connection;

    public RequestIsTheAskMigrationTests()
    {
        _connection = AtSchemaTen();

        Execute($"""
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '{Stamp}') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'),
                ('billing@test', 'test', 'full', 'active');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{Stamp}');
            INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, open_pair_cap,
                uncollected_flag_ms, stalled_flag_ms, opened_utc, request_hash, conversation_id)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                'OPEN', 5, 900000, 14400000, '{Stamp}', 'aa01', 'pricing'),
                   ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'orders@test',
                'OPEN', 5, 900000, 14400000, '{Stamp}', 'aa02', 'pricing');
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                content_hash, conversation_id, created_utc)
            SELECT id, 'request', 'orders@test', 'billing@test', 'claude-opus-5', @request,
                   printf('%064x', id), conversation_id, '{Stamp}' FROM exchanges;
            """,
            ("@request", OldRequest));

        // The Result closes the first exchange; the second stays open, which is the case that matters:
        // nothing times out, so an exchange opened under 0.6 can outlive the upgrade and must stay answerable.
        Execute($"""
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                content_hash, conversation_id, created_utc)
            SELECT id, 'result', 'billing@test', 'orders@test', 'claude-opus-5', @result,
                   'ff' || printf('%062x', id), conversation_id, '{Stamp}'
              FROM exchanges ORDER BY id LIMIT 1;
            UPDATE exchanges SET state = 'CLOSED', outcome = 'complete', closed_utc = '{Stamp}'
             WHERE request_hash = 'aa01';
            """,
            ("@result", OldResult));

        Assert.Equal(SchemaMigrator.LatestVersion - 10, SchemaMigrator.Migrate(_connection));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_store_is_at_the_latest_schema_intact_and_every_key_resolves()
    {
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(_connection));
        Assert.Equal("ok", Scalar<string>("PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void Every_stored_payload_is_exactly_what_was_written()
    {
        // Not "still readable" — identical. A migration that tidied an old payload would be rewriting somebody's words.
        Assert.Equal(
            2L,
            Scalar<long>("SELECT count(*) FROM messages WHERE type = 'request' AND payload_json = @p;", ("@p", OldRequest)));

        Assert.Equal(
            1L,
            Scalar<long>("SELECT count(*) FROM messages WHERE type = 'result' AND payload_json = @p;", ("@p", OldResult)));
    }

    [Fact]
    public void The_items_of_a_stored_request_are_still_what_a_result_must_answer()
    {
        // The half that keeps an exchange opened under 0.6 answerable after the upgrade.
        var stored = Scalar<string>(
            "SELECT payload_json FROM messages WHERE type = 'request' ORDER BY id LIMIT 1;");

        Assert.Equal(["r1", "r2"], PayloadShape.ItemIds(stored));
    }

    [Fact]
    public void The_result_rows_a_stored_result_produced_are_untouched()
    {
        Assert.Equal(
            "r1:done|r2:answered",
            Scalar<string>(
                "SELECT group_concat(item || ':' || outcome, '|') FROM (SELECT item, outcome FROM result_items ORDER BY item);"));

        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM result_sources WHERE source = 'orders@test';"));
    }

    [Fact]
    public void A_request_in_the_new_shape_is_admitted_afterwards()
    {
        Execute("""
            INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, open_pair_cap,
                uncollected_flag_ms, stalled_flag_ms, opened_utc, request_hash, conversation_id)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                'OPEN', 5, 900000, 14400000, '2026-09-17T00:00:00.0000000Z', 'bb01', 'pricing');
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                content_hash, conversation_id, created_utc)
            SELECT id, 'request', 'orders@test', 'billing@test', 'claude-opus-5',
                   json_object('ask', 'who owns the invoice total?'), 'cc' || printf('%062x', id), conversation_id,
                   '2026-09-17T00:00:00.0000000Z' FROM exchanges WHERE request_hash = 'bb01';
            """);

        Assert.Equal(3L, Scalar<long>("SELECT count(*) FROM messages WHERE type = 'request';"));
    }

    [Fact]
    public void The_shape_that_was_required_before_is_refused_afterwards()
    {
        // The same payload the constructor wrote at schema 10, now refused: what changed is what may be written next.
        Execute("""
            INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, open_pair_cap,
                uncollected_flag_ms, stalled_flag_ms, opened_utc, request_hash, conversation_id)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test', 'billing@test',
                'OPEN', 5, 900000, 14400000, '2026-09-17T00:00:00.0000000Z', 'dd01', 'pricing');
            """);

        var refused = Assert.Throws<SqliteException>(() => Execute("""
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                content_hash, conversation_id, created_utc)
            SELECT id, 'request', 'orders@test', 'billing@test', 'claude-opus-5', @request,
                   'ee' || printf('%062x', id), conversation_id, '2026-09-17T00:00:00.0000000Z'
              FROM exchanges WHERE request_hash = 'dd01';
            """,
            ("@request", OldRequest)));

        Assert.Contains("carries an ask", refused.Message, StringComparison.Ordinal);
    }

    private void Execute(string sql, params (string Name, string Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, string Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection AtSchemaTen()
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
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 10)
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
        version.CommandText = "PRAGMA user_version = 10;";
        version.ExecuteNonQuery();

        return connection;
    }
}
