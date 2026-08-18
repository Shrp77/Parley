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
