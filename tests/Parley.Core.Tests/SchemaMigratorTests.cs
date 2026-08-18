using Microsoft.Data.Sqlite;
using Parley.Store;
using Xunit;

namespace Parley.Core.Tests;

public sealed class SchemaMigratorTests
{
    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Seed(SqliteConnection connection, params string[] names)
    {
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO agents (name, adapter, tier, status)
                VALUES ($name, 'test', 'full', 'registered');
                """;
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertExchange(
        SqliteConnection connection,
        string pairKey,
        string asker,
        string answerer,
        string state = "OPEN")
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, ttl_seconds,
                 opened_utc, expires_utc, closed_utc)
            VALUES
                ($pair, $asker, $answerer, $answerer, $state, 900,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:15:00Z',
                 CASE WHEN $state = 'CLOSED' THEN '2026-01-01T00:05:00Z' ELSE NULL END);
            """;
        command.Parameters.AddWithValue("$pair", pairKey);
        command.Parameters.AddWithValue("$asker", asker);
        command.Parameters.AddWithValue("$answerer", answerer);
        command.Parameters.AddWithValue("$state", state);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Migrate_applies_and_is_idempotent()
    {
        using var connection = OpenInMemory();

        Assert.True(SchemaMigrator.Migrate(connection) > 0);
        Assert.Equal(0, SchemaMigrator.Migrate(connection));
        Assert.True(SchemaMigrator.GetUserVersion(connection) >= 1);
    }

    [Fact]
    public void One_open_exchange_per_pair_is_enforced_by_the_index()
    {
        using var connection = OpenInMemory();
        SchemaMigrator.Migrate(connection);
        Seed(connection, "orders", "billing");

        InsertExchange(connection, "billing\u001Forders", "orders", "billing");

        // Second open exchange on the same pair, in the other direction.
        var ex = Assert.Throws<SqliteException>(
            () => InsertExchange(connection, "billing\u001Forders", "billing", "orders"));

        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Closed_exchanges_do_not_hold_the_pair_slot()
    {
        using var connection = OpenInMemory();
        SchemaMigrator.Migrate(connection);
        Seed(connection, "orders", "billing");

        InsertExchange(connection, "billing\u001Forders", "orders", "billing", "CLOSED");
        InsertExchange(connection, "billing\u001Forders", "billing", "orders");
    }

    [Fact]
    public void Owed_by_must_be_a_participant()
    {
        using var connection = OpenInMemory();
        SchemaMigrator.Migrate(connection);
        Seed(connection, "orders", "billing", "inventory");

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO exchanges
                (pair_key, asker, answerer, owed_by, state, ttl_seconds, opened_utc, expires_utc)
            VALUES
                ('billing\u001Forders', 'orders', 'billing', 'inventory', 'OPEN', 900,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:15:00Z');
            """;

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Notify_must_not_reference_an_exchange()
    {
        using var connection = OpenInMemory();
        SchemaMigrator.Migrate(connection);
        Seed(connection, "orders", "billing");
        InsertExchange(connection, "billing\u001Forders", "orders", "billing");

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO messages (exchange_id, type, from_agent, to_agent, payload_json, created_utc)
            VALUES (1, 'notify', 'orders', 'billing', '{}', '2026-01-01T00:00:00Z');
            """;

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var connection = OpenInMemory();
        SchemaMigrator.Migrate(connection);

        // 'orders' was never registered.
        Assert.Throws<SqliteException>(
            () => InsertExchange(connection, "billing\u001Forders", "orders", "billing"));
    }
}
