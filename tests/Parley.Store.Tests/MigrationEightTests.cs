using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>Migration 008: where a move falls among the messages, so an export can tell where a moved message was written.</summary>
/// <remarks>
/// A move now records the newest message id when it was made. One recorded before keeps the best its row allows, the
/// newest message at or before its time, and every row is kept.
/// </remarks>
[Integration]
public sealed class MigrationEightTests : IDisposable
{
    private readonly SqliteConnection _connection = SchemaSeven();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void A_move_recorded_before_it_with_no_messages_yet_records_none_and_the_index_is_made()
    {
        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '2026-11-05T09:00:00Z');
            INSERT INTO conversation_moves (source, destination, moved_by, moved_utc, first_message, messages)
            VALUES ('pricing', 'elsewhere', 'human-user@acme', '2026-11-05T09:05:00.0000000+00:00', NULL, 0);
            """);

        var moves = _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversation_moves;");

        SchemaMigrator.Migrate(_connection);

        Assert.Equal(8, _connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(moves, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversation_moves;"));

        // No messages yet: none came before the move, so it records none.
        Assert.Null(_connection.ExecuteScalar<long?>("SELECT last_message FROM conversation_moves;"));
        Assert.Equal(
            1,
            _connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_conversation_move_exchanges_exchange';"));
    }

    [Fact]
    public void A_move_recorded_before_it_is_placed_after_the_newest_message_at_or_before_its_time()
    {
        // Three messages: at 09:04, at 09:05 (spelled another way from the moves) and at 09:06. A move at 09:05 is placed
        // after the second: times are compared as instants, not as text, and the instant itself counts. A move at 09:05:30
        // falls between messages and is placed after the newest before it, not the first after it; one older than every
        // message records none.
        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));

        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '2026-11-05T09:00:00Z');
            INSERT INTO exchanges
                (pair_key, requester, receiver, owed_by, state, conversation_id, opened_utc,
                 uncollected_flag_ms, stalled_flag_ms, request_hash)
            VALUES ('billing@test' || char(31) || 'orders@test', 'orders@test', 'billing@test',
                    'billing@test', 'OPEN', 'pricing', '2026-11-05T09:00:00Z', 900000, 14400000, 'hash-1');
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, payload_json, content_hash,
                                  conversation_id, created_utc)
            SELECT x.id, 'clarify', 'orders@test', 'billing@test', 'test', json_object('text', w.at),
                   '0000000000000000000000000000000000000000000000000000000000000001', 'pricing', w.at
              FROM exchanges x,
                   (SELECT '2026-11-05T09:04:00.0000000+00:00' AS at UNION ALL
                    SELECT '2026-11-05T09:05:00Z' UNION ALL
                    SELECT '2026-11-05T09:06:00.0000000+00:00') w
             ORDER BY w.at;
            INSERT INTO conversation_moves (source, destination, moved_by, moved_utc, first_message, messages) VALUES
              ('pricing', 'elsewhere', 'human-user@acme', '2026-11-05T09:05:00.0000000+00:00', NULL, 0),
              ('elsewhere', 'pricing', 'human-user@acme', '2026-11-05T09:05:30.0000000+00:00', NULL, 0),
              ('pricing', 'earlier', 'human-user@acme', '2026-11-05T09:01:00.0000000+00:00', NULL, 0);
            """);

        var atTheMove = _connection.ExecuteScalar<long>("SELECT id FROM messages WHERE created_utc = '2026-11-05T09:05:00Z';");

        SchemaMigrator.Migrate(_connection);

        Assert.Equal(
            [atTheMove, atTheMove, null],
            _connection.Query<long?>("SELECT last_message FROM conversation_moves ORDER BY id;").ToArray());
    }

    private static SqliteConnection SchemaSeven()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var file in new[]
        {
            ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql", ".003_file_problems.sql", ".004_asked_once.sql",
            ".005_soft_message_limit.sql", ".006_conversation_moves.sql", ".007_images_can_be_placed.sql",
        })
        {
            using var stream = assembly.GetManifestResourceStream(
                assembly.GetManifestResourceNames().Single(name => name.EndsWith(file, StringComparison.Ordinal)))!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();

            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        connection.Execute("PRAGMA user_version = 7;");

        return connection;
    }
}
