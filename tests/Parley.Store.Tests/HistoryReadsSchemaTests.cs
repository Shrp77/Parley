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

    private int Insert(string kind, string? exchange, string? words, string? matches, string reader = "billing@acme") =>
        _connection.Execute(
            $"INSERT INTO history_reads (reader, kind, exchange_id, words, matches, created_utc) VALUES (@Reader, @Kind, {exchange ?? "NULL"}, @Words, {matches ?? "NULL"}, '2026-11-17T09:30:00Z');",
            new { Reader = reader, Kind = kind, Words = words });

    [Fact]
    public void A_read_of_an_exchange_and_a_search_are_the_two_good_shapes()
    {
        Assert.Equal(1, Insert("exchange", "5", null, null));
        Assert.Equal(1, Insert("search", null, "invoice total", "3"));
    }

    [Theory]
    [InlineData("other", "5", null, null)]
    [InlineData("exchange", null, null, null)]
    [InlineData("exchange", "5", "words", null)]
    [InlineData("exchange", "5", null, "1")]
    [InlineData("search", "5", "words", "1")]
    [InlineData("search", null, null, "1")]
    [InlineData("search", null, "words", null)]
    [InlineData("search", null, "words", "-1")]
    public void A_record_of_any_other_shape_is_refused(string kind, string? exchange, string? words, string? matches) =>
        Assert.Throws<SqliteException>(() => Insert(kind, exchange, words, matches));

    [Fact]
    public void Words_are_bounded_at_two_hundred_characters()
    {
        Assert.Equal(1, Insert("search", null, new string('a', 200), "0"));
        Assert.Throws<SqliteException>(() => Insert("search", null, new string('a', 201), "0"));
        Assert.Throws<SqliteException>(() => Insert("search", null, string.Empty, "0"));
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
        Assert.Throws<SqliteException>(() => Insert("search", null, "before" + char.ConvertFromUtf32(character) + "after", "1"));

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

        Assert.Equal(1, SchemaMigrator.Migrate(connection));
        Assert.Equal(9, SchemaMigrator.GetUserVersion(connection));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM conversations WHERE id = 'kept';"));
        Assert.Empty(StoreShape.Problems(connection));
    }
}
