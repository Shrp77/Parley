using Dapper;
using Microsoft.Data.Sqlite;
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
    public void A_move_recorded_before_it_is_placed_after_the_newest_message_of_its_time_and_the_index_is_made()
    {
        _connection.Execute(
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '2026-11-05T09:00:00Z');
            INSERT INTO conversation_moves (source, destination, moved_by, moved_utc, first_message, messages)
            VALUES ('pricing', 'elsewhere', 'human-user@acme', '2026-11-05T09:05:00.0000000+00:00', NULL, 0);
            """);

        var messages = _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversation_moves;");

        SchemaMigrator.Migrate(_connection);

        Assert.Equal(8, _connection.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(messages, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversation_moves;"));

        // No messages yet: none came before the move, so it records none.
        Assert.Null(_connection.ExecuteScalar<long?>("SELECT last_message FROM conversation_moves;"));
        Assert.Equal(
            1,
            _connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_conversation_move_exchanges_exchange';"));
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
