using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What storage refuses in a record of an agent reading the past (SPEC.md §9.2, §5.8).
/// </summary>
/// <remarks>
/// The record is shown to the person, so what it holds is held here and not by the code that writes it: a kind that is
/// neither of the two, a read of an exchange that names none, a search with no words, and words that could break, hide or
/// reorder a line. Each check was watched failing with the check removed.
/// </remarks>
[Integration]
public sealed class HistoryReadsSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public HistoryReadsSchemaTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _connection.Execute(
            "INSERT INTO agents (name, adapter, tier, status) VALUES ('billing@acme', 'claude-code', 'full', 'active');");
    }

    public void Dispose() => _connection.Dispose();

    private int Insert(string kind, string exchange, string? words, string? matches, string total = "1", string reader = "billing@acme") =>
        _connection.Execute(
            $"INSERT INTO history_reads (reader, kind, exchange_id, words, matches, total, first_utc, last_utc) VALUES (@Reader, @Kind, {exchange}, @Words, {matches ?? "NULL"}, {total}, '2026-11-17T09:30:00Z', '2026-11-17T09:30:00Z');",
            new { Reader = reader, Kind = kind, Words = words });

    [Fact]
    public void A_tally_of_an_exchange_and_of_searches_are_the_two_good_shapes()
    {
        Assert.Equal(1, Insert("exchange", "5", null, null));
        Assert.Equal(1, Insert("search", "0", "invoice total", "3", total: "4"));
    }

    [Theory]
    [InlineData("other", "5", null, null, "1")]
    [InlineData("exchange", "0", null, null, "1")]
    [InlineData("exchange", "-1", null, null, "1")]
    [InlineData("exchange", "5", "words", null, "1")]
    [InlineData("exchange", "5", null, "1", "1")]
    [InlineData("search", "5", "words", "1", "1")]
    [InlineData("search", "0", null, "1", "1")]
    [InlineData("search", "0", "words", null, "1")]
    [InlineData("search", "0", "words", "-1", "1")]
    [InlineData("exchange", "5", null, null, "0")]
    public void A_tally_of_any_other_shape_is_refused(string kind, string exchange, string? words, string? matches, string total) =>
        Assert.Throws<SqliteException>(() => Insert(kind, exchange, words, matches, total));

    [Fact]
    public void One_row_for_an_agent_and_an_exchange_and_one_for_its_searches()
    {
        Insert("exchange", "5", null, null);
        Insert("search", "0", "words", "1");

        Assert.Throws<SqliteException>(() => Insert("exchange", "5", null, null));
        Assert.Throws<SqliteException>(() => Insert("search", "0", "more", "2"));
        Assert.Equal(1, Insert("exchange", "6", null, null));
    }

    [Fact]
    public void Words_are_bounded_at_two_hundred_characters()
    {
        Assert.Equal(1, Insert("search", "0", new string('a', 200), "0"));
        _connection.Execute("DELETE FROM history_reads;");
        Assert.Throws<SqliteException>(() => Insert("search", "0", new string('a', 201), "0"));
        Assert.Throws<SqliteException>(() => Insert("search", "0", string.Empty, "0"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(27)]
    [InlineData(0x85)]
    [InlineData(0x202e)]
    [InlineData(0x2028)]
    [InlineData(0x200b)]
    public void Words_cannot_break_hide_or_reorder_a_line(int character) =>
        Assert.Throws<SqliteException>(() => Insert("search", "0", "before" + char.ConvertFromUtf32(character) + "after", "1"));

    [Fact]
    public void A_reader_must_be_registered()
    {
        Assert.Throws<SqliteException>(() => Insert("exchange", "5", null, null, reader: "nobody@acme"));
    }

    [Fact]
    public void Removing_an_agent_removes_what_it_read()
    {
        Insert("exchange", "5", null, null);

        _connection.Execute("DELETE FROM agents WHERE name = 'billing@acme';");

        Assert.Equal(0, _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM history_reads;"));
    }

    [Fact]
    public void An_existing_store_upgrades_and_keeps_every_row()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("parley-history-migration").FullName, "parley.db");

        using var connection = ParleyDatabase.Open(path);
        SchemaMigrator.Migrate(connection);

        connection.Execute("DROP TABLE history_reads; PRAGMA user_version = 8;");
        connection.Execute("INSERT INTO conversations (id, state, opened_utc) VALUES ('kept', 'OPEN', '2026-11-05T09:00:00Z');");

        Assert.Equal(SchemaMigrator.LatestVersion - 8, SchemaMigrator.Migrate(connection));
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(connection));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversations WHERE id = 'kept';"));
        Assert.Empty(StoreShape.Problems(connection));
    }

    [Fact]
    public async Task An_upsert_counts_up_keeps_the_first_time_and_takes_the_newest_words()
    {
        var reader = Parley.Core.AgentName.Create("billing@acme");
        var repository = new HistoryRepository(_connection);
        _connection.Execute("INSERT INTO conversations (id, state, opened_utc) VALUES ('c', 'OPEN', '2026-11-17T09:00:00Z');");

        await repository.SearchAsync(reader, ["one"], "one", new Parley.Core.HistoryFilter(), 0, new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        await repository.SearchAsync(reader, ["two"], "two", new Parley.Core.HistoryFilter(), 0, new DateTimeOffset(2026, 11, 17, 9, 45, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        var (total, first, last, words) = _connection.QuerySingle<(long, string, string, string)>("SELECT total, first_utc, last_utc, words FROM history_reads;");

        Assert.Equal(2, total);
        Assert.StartsWith("2026-11-17T09:30:00", first, StringComparison.Ordinal);
        Assert.StartsWith("2026-11-17T09:45:00", last, StringComparison.Ordinal);
        Assert.Equal("two", words);
    }

    [Fact]
    public async Task Retention_removes_a_row_by_when_it_was_last_added_to_and_not_when_it_began()
    {
        _connection.Execute("INSERT INTO agents (name, adapter, tier, status) VALUES ('old@acme', 'claude-code', 'full', 'active');");
        _connection.Execute("INSERT INTO history_reads (reader, kind, exchange_id, total, first_utc, last_utc) VALUES ('billing@acme', 'exchange', 5, 9, '2026-01-01T00:00:00Z', '2026-11-16T00:00:00Z');");
        _connection.Execute("INSERT INTO history_reads (reader, kind, exchange_id, total, first_utc, last_utc) VALUES ('old@acme', 'exchange', 5, 9, '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z');");

        await new Pruner(_connection).PruneAsync(Parley.Core.RetentionPolicy.Default, new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Equal("billing@acme", _connection.ExecuteScalar<string>("SELECT reader FROM history_reads;"));
    }
}
