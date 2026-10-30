using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 003 upgrades a schema 2 store in place: the change notices builds before placed files were immutable
/// left are deleted, gone notices are kept, and file problems have a table (SPEC.md §9.2, §9.5).
/// </summary>
/// <remarks>
/// A notice's <c>sha256_now</c> now says a file was tampered with. A change notice left from before would be read as
/// that, telling an agent a file it changed through an old build was tampered with, so the migration deletes them.
/// </remarks>
[Integration]
public sealed class MigrationThreeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = SchemaTwo();

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_notice_goes_a_gone_notice_stays_and_problems_can_be_raised()
    {
        Work.Register(_connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"));
        var stamp = ExchangeRow.FormatTimestamp(Now);
        Execute(
            "INSERT INTO file_notices (agent, conversation_id, name_key, name, sha256_had, sha256_now, size_bytes, changed_utc) VALUES "
            + $"('orders@test', 'pricing', 'A.MD', 'a.md', '{ContentHash.Of("v0"u8).Value}', '{ContentHash.Of("v1"u8).Value}', 2, '{stamp}'), "
            + $"('billing@test', 'pricing', 'B.MD', 'b.md', '{ContentHash.Of("v0"u8).Value}', NULL, NULL, '{stamp}');");

        Assert.Equal(1, SchemaMigrator.Migrate(_connection));

        Assert.Equal(3, SchemaMigrator.GetUserVersion(_connection));
        Assert.Empty(StoreShape.Problems(_connection));
        Assert.Equal(["billing@test"], Query("SELECT agent FROM file_notices;"));

        Assert.True(await new FileProblemRepository(_connection).RaiseAsync(
            FileProblemKind.Locked, "pricing", "pricing/a.md", null, null, null, "held", Now, cancellationToken: Ct));
    }

    private static SqliteConnection SchemaTwo()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);

        var assembly = typeof(SchemaMigrator).Assembly;

        foreach (var file in new[] { ".001_initial.sql", ".002_proposal_ids_and_item_openings.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = 2;";
            version.ExecuteNonQuery();
        }

        return connection;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private List<string> Query(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();

        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
