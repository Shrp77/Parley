using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That migration 010 narrows a working 0.6 store's file names to the set without losing a row it may keep.
/// </summary>
/// <remarks>
/// <para>
/// 010 rebuilds <c>shared_files</c>, and a rebuild tested on an empty store tests nothing: the copy, the four
/// triggers made again, and the rows left behind only exist when there are rows. So this builds a store at schema 9
/// from the embedded migrations and writes what 0.6 admitted — a name with a space, an accented one, and one already
/// in the set, each fetched by an agent — before letting <see cref="SchemaMigrator"/> bring it up.
/// </para>
/// <para>
/// Written as SQL rather than through the repository, because the repository of this build will not place a name 0.6
/// accepted, which is the point of the change.
/// </para>
/// </remarks>
public sealed class FileNameSetMigrationTests : IDisposable
{
    private const string Stamp = "2026-09-14T00:00:00.0000000Z";
    private const string Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly SqliteConnection _connection;

    public FileNameSetMigrationTests()
    {
        _connection = AtSchemaNine();

        Execute($"""
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '{Stamp}') ON CONFLICT DO NOTHING;
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'test', 'full', 'active'),
                ('billing@test', 'test', 'full', 'active'),
                ('inventory@test', 'test', 'full', 'retired');
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', '{Stamp}');
            INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES ('pricing', 'pricing', '{Stamp}');
            INSERT INTO shared_files
                (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
            VALUES
                ('pricing', 'PRICE-SNAPSHOT.MD', 'price-snapshot.md', 1, '{Hash}', 'billing@test', '{Stamp}', '{Stamp}', 1),
                ('pricing', 'MEETING NOTES.MD', 'Meeting notes.md', 1, '{Hash}', 'billing@test', '{Stamp}', '{Stamp}', 1),
                ('pricing', @umlautKey, @umlaut, 1, '{Hash}', NULL, '{Stamp}', '{Stamp}', 1);
            INSERT INTO file_fetches (agent, conversation_id, name_key, name, sha256, fetched_utc) VALUES
                ('orders@test', 'pricing', 'PRICE-SNAPSHOT.MD', 'price-snapshot.md', '{Hash}', '{Stamp}'),
                ('orders@test', 'pricing', 'MEETING NOTES.MD', 'Meeting notes.md', '{Hash}', '{Stamp}'),
                ('inventory@test', 'pricing', 'MEETING NOTES.MD', 'Meeting notes.md', '{Hash}', '{Stamp}');
            """,
            ("@umlaut", "Übersicht.md"),
            ("@umlautKey", "ÜBERSICHT.MD"));

        // From 9 to whatever this build carries: 010 is the one under test, and 011 followed it.
        Assert.Equal(SchemaMigrator.LatestVersion - 9, SchemaMigrator.Migrate(_connection));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void The_store_is_at_the_latest_schema_intact_and_every_key_resolves()
    {
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetUserVersion(_connection));
        Assert.Equal("ok", Scalar<string>("PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void A_name_in_the_set_is_carried_forward_as_it_was()
    {
        Assert.Equal(
            "price-snapshot.md|billing@test|1",
            Scalar<string>("SELECT name || '|' || placed_by || '|' || modified_ticks FROM shared_files;"));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM file_fetches WHERE name_key = 'PRICE-SNAPSHOT.MD';"));
    }

    [Fact]
    public void A_name_outside_the_set_is_not_carried_forward_and_its_fetcher_is_told_it_is_gone()
    {
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM shared_files;"));
        Assert.Equal(
            "orders@test|Meeting notes.md|1",
            Scalar<string>(
                "SELECT agent || '|' || name || '|' || (sha256_now IS NULL AND size_bytes IS NULL) FROM file_notices;"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM file_fetches WHERE name_key = 'MEETING NOTES.MD';"));
    }

    [Fact]
    public void A_retired_fetcher_is_not_told_as_a_removal_would_not_tell_it()
    {
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM file_notices WHERE agent = 'inventory@test';"));
    }

    [Fact]
    public async Task The_notice_it_leaves_is_one_the_repository_can_hand_over()
    {
        // A timestamp written by strftime in SQL rather than by the broker, which a collection must still parse.
        var notices = await new FileNoticeRepository(_connection)
            .CollectAsync(AgentName.Create("orders@test"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(notices.Notices);
    }

    [Theory]
    [InlineData("tr_placer_is_registered")]
    [InlineData("tr_shared_file_stays_put")]
    [InlineData("tr_shared_file_path_fits")]
    [InlineData("tr_shared_file_path_fits_on_rename")]
    public void Every_trigger_on_the_rebuilt_table_is_there_again(string trigger)
    {
        Assert.Equal(
            1L,
            Scalar<long>(string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name = '{trigger}' AND tbl_name = 'shared_files';")));
    }

    [Fact]
    public void The_rebuilt_table_still_refuses_what_its_triggers_hold()
    {
        Assert.Throws<SqliteException>(() => Execute("UPDATE shared_files SET placed_by = 'orders@test';"));
    }

    private void Execute(string sql, params (string Name, string Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection AtSchemaNine()
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
            if (int.Parse(name.AsSpan(Prefix.Length, 3), CultureInfo.InvariantCulture) > 9)
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
        version.CommandText = "PRAGMA user_version = 9;";
        version.ExecuteNonQuery();

        return connection;
    }
}
