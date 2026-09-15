using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 009 brings a working 0.5 store to schema 9 without touching what it holds.
/// </summary>
/// <remarks>
/// <para>
/// 009 only adds tables, so what can go wrong is quiet: a reference to a table that is not there yet, a
/// trigger that fails to parse against a real schema rather than against the document's, or a key that
/// resolves on an empty store and not on one with rows. A fresh database exercises none of that.
/// </para>
/// <para>
/// So this builds a store at schema 8 from the embedded migrations, writes the traffic 0.5 writes, and only
/// then lets <see cref="SchemaMigrator"/> bring it up.
/// </para>
/// </remarks>
public sealed class FileStoreMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileStoreMigrationTests()
    {
        _connection = AtSchemaEight();

        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));

        Work.OpenAsync(
                _connection,
                AgentName.Create("orders@test"),
                AgentName.Create("billing@test"),
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                conversationId: "tax-inclusive-pricing",
                cancellationToken: CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(SchemaMigrator.LatestVersion - 8, SchemaMigrator.Migrate(_connection));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_store_is_at_the_latest_schema()
    {
        Assert.Equal(9, SchemaMigrator.GetUserVersion(_connection));
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(_connection));
    }

    [Fact]
    public void The_store_is_intact_and_every_key_resolves()
    {
        Assert.Equal("ok", Scalar<string>("PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Theory]
    [InlineData("conversation_folders")]
    [InlineData("shared_files")]
    [InlineData("file_fetches")]
    [InlineData("file_notices")]
    [InlineData("file_placements")]
    public void Each_new_table_exists_and_starts_empty(string table)
    {
        Assert.Equal(0L, Scalar<long>(string.Create(CultureInfo.InvariantCulture, $"SELECT count(*) FROM {table};")));
    }

    [Fact]
    public void What_0_5_wrote_is_unchanged()
    {
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM conversations WHERE id = 'tax-inclusive-pricing';"));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM exchanges WHERE state = 'OPEN';"));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM messages WHERE type = 'request';"));
    }

    [Fact]
    public async Task A_conversation_0_5_started_can_hold_files_once_migrated()
    {
        // The case a migration tested only on an empty store would miss: the new tables referencing rows that
        // existed before them.
        var files = new SharedFileRepository(_connection);

        var placed = await files.PlaceAsync(
            "tax-inclusive-pricing",
            SharedFileName.Create("price-snapshot.md"),
            ContentHash.Of("net of tax"u8),
            sizeBytes: 10,
            AgentName.Create("billing@test"),
            modifiedTicks: 1,
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            takenOnDisk: _ => false,
            cancellationToken: Ct);

        Assert.Equal(PlaceOutcome.Placed, placed.Outcome);
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM shared_files;"));
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection AtSchemaEight()
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
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 8)
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
        version.CommandText = "PRAGMA user_version = 8;";
        version.ExecuteNonQuery();

        return connection;
    }
}
