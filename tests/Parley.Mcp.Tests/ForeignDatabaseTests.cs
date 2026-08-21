using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A mistyped <c>PARLEY_DB</c>.
/// </summary>
/// <remarks>
/// <para>
/// The path lives in a launch configuration, and paths in launch configurations get mistyped.
/// Pointed at an application's own SQLite file, the migration wrote Parley's seven tables, their
/// indexes and their triggers into it and moved <c>user_version</c> from zero to one — silently,
/// and reporting success. That last part is what makes it worth a guard rather than a warning:
/// <c>user_version</c> is where most migration tooling keeps its place, so the damage is not
/// only the tables.
/// </para>
/// <para>
/// Tables present with a schema version of zero is the signature. A Parley database carries a
/// version from its first migration onwards, and that migration is transactional, so tables and
/// version commit together and there is no half-state to mistake for this.
/// </para>
/// </remarks>
public sealed class ForeignDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-foreign-" + Guid.NewGuid().ToString("N"));

    public ForeignDatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere: pooled connections keep the file open.
        }
    }

    private string MakeForeignDatabase()
    {
        var path = Path.Combine(_directory, "app.db");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);" +
            "INSERT INTO customers (name) VALUES ('acme');";
        command.ExecuteNonQuery();

        return path;
    }

    private static (long Version, long Tables) Inspect(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT count(*) FROM sqlite_master " +
            "         WHERE type = 'table' AND name NOT LIKE 'sqlite_%');";

        var tables = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        return (version, tables);
    }

    [Fact]
    public async Task A_server_will_not_write_its_schema_into_somebody_elses_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = MakeForeignDatabase();

        var options = new ServerOptions(
            path, Parley.Core.AgentName.Create("orders"), "test-model",
            TimeSpan.FromMinutes(1), Parley.Core.RetentionPolicy.Default, null);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BrokerSession.StartAsync(options, new StubClock(T0), ct));

        Assert.Contains("did not create", refusal.Message, StringComparison.Ordinal);

        // And the file is exactly as it was.
        SqliteConnection.ClearAllPools();
        var (version, tables) = Inspect(path);

        Assert.Equal(0, version);
        Assert.Equal(1, tables);
    }

    [Fact]
    public async Task An_empty_file_is_the_ordinary_first_run_and_is_migrated()
    {
        // The other side. A project whose database does not exist yet, or exists and is empty,
        // is what every first launch looks like — refusing those would refuse every new project.
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "empty.db");

        await File.WriteAllBytesAsync(path, [], ct);

        var options = new ServerOptions(
            path, Parley.Core.AgentName.Create("orders"), "test-model",
            TimeSpan.FromMinutes(1), Parley.Core.RetentionPolicy.Default, null);

        var session = await BrokerSession.StartAsync(options, new StubClock(T0), ct);
        await session.DisposeAsync();

        SqliteConnection.ClearAllPools();
        var (version, _) = Inspect(path);

        Assert.Equal(1, version);
    }
}
