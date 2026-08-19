using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

public sealed class EngineVersionTests : IDisposable
{
    // The floor 001_initial.sql declares: STRICT tables arrived in 3.37, generated columns in
    // 3.31, and unixepoch() in 3.38.
    private static readonly Version Required = new(3, 38);

    private readonly SqliteConnection _connection;

    public EngineVersionTests() => _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task The_engine_meets_the_floor_the_schema_declares()
    {
        // Which package supplies the native engine is not stable across SQLitePCLRaw major
        // lines, and neither is its version number: on 2.x it is lib.e_sqlite3, numbered
        // independently of the SQLite inside it, and on 3.x it is a package named SQLite,
        // numbered as the engine. A dependency change can therefore move the engine while
        // every version number in the graph still looks reasonable.
        //
        // Without this, an engine below the floor surfaces as a CREATE TABLE failure that
        // names neither the feature it objected to nor the version it wanted, and the whole
        // suite fails at once with nothing pointing at the cause. Asking the engine what it
        // is costs one query and makes the answer unambiguous.
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var reported = (string)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;

        Assert.True(
            Version.Parse(reported) >= Required,
            $"schema requires SQLite {Required} or later, engine reports {reported}");
    }
}
