using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Migration mechanics only: version tracking, idempotency, and resource discovery. The
/// constraints the migration creates are covered by <see cref="SchemaIntegrityTests"/>.
/// </summary>
public sealed class SchemaMigratorTests
{
    [Fact]
    public void Migrate_applies_and_reports_how_many()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.True(SchemaMigrator.Migrate(connection) > 0);
    }

    [Fact]
    public void A_store_written_by_a_newer_parley_is_refused()
    {
        // An older build pointed at a database a newer one has migrated must not open it and serve it
        // while announcing the schema version it expected rather than the one present. That might be
        // harmless for a widened CHECK, but a migration that dropped or renamed anything would be met
        // by code that believed none of it had happened.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SchemaMigrator.Migrate(connection);

        using (var ahead = connection.CreateCommand())
        {
            ahead.CommandText =
                $"PRAGMA user_version = {SchemaMigrator.LatestVersion + 1};";
            ahead.ExecuteNonQuery();
        }

        var refused = Assert.Throws<InvalidOperationException>(
            () => SchemaMigrator.Migrate(connection));

        // Naming both numbers, because the person reading it has to decide which of the two
        // things in front of them is out of date.
        Assert.Contains(
            (SchemaMigrator.LatestVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            refused.Message,
            StringComparison.Ordinal);

        Assert.Contains("newer version", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_at_the_version_this_build_knows_is_opened()
    {
        // The other side of it. A guard that refused its own version would refuse every start
        // after the first, which is the failure mode a version check most easily introduces.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SchemaMigrator.Migrate(connection);

        Assert.Equal(0, SchemaMigrator.Migrate(connection));
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SchemaMigrator.Migrate(connection);

        // Called on every start-up, so a second run must be a no-op rather than an error.
        Assert.Equal(0, SchemaMigrator.Migrate(connection));
    }

    [Fact]
    public void Migrate_records_the_schema_version()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Equal(0, SchemaMigrator.GetUserVersion(connection));

        SchemaMigrator.Migrate(connection);

        Assert.True(SchemaMigrator.GetUserVersion(connection) >= 1);
    }

    [Fact]
    public void Migrate_opens_a_closed_connection()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");

        Assert.True(SchemaMigrator.Migrate(connection) > 0);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public void Configure_applies_the_settings_the_broker_depends_on()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SchemaMigrator.Configure(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(
            1L,
            Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Migrate_rejects_a_null_connection()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaMigrator.Migrate(null!));
    }
}
