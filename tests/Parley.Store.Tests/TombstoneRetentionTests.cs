using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// How the record a destroy leaves behaves under everything else that deletes.
/// </summary>
/// <remarks>
/// <para>
/// §9.4's retention sweep exists because a log kept forever
/// grows the blast radius of a leaked database. The tombstone table is one the sweep never sees, and the
/// question cuts both ways: if retention removes the record, the identifier is freed and a
/// destroyed conversation's name can be reused under somebody's export; if retention can never
/// touch it, the table grows for as long as the deployment runs.
/// </para>
/// <para>
/// Both are real and only one is a defect today. What matters is knowing which, rather than finding
/// out later.
/// </para>
/// </remarks>
[Integration]
public sealed class TombstoneRetentionTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public TombstoneRetentionTests() => _connection = ParleyDatabase.OpenInMemory();

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_retention_sweep_does_not_remove_the_record()
    {
        // **The property that matters, and the direction that would be a defect.** The record is
        // what keeps the identifier taken; a sweep that removed it would free a name somebody holds
        // an export of, and a new conversation could then be opened under it. The two would be
        // indistinguishable in any file written before the reuse.
        Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('long-ago', '2020-01-01T00:00:00.0000000+00:00', 4, 'orders@test');
            """);

        var pruner = new Pruner(_connection);

        await pruner.PruneAsync(
            new Parley.Core.RetentionPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(1)),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Ct);

        Assert.Equal(
            1L, Count("SELECT count(*) FROM destroyed_conversations WHERE id = 'long-ago'"));
    }

    [Fact]
    public void The_record_holds_nothing_retention_exists_to_remove()
    {
        // Why the answer above is right rather than merely convenient. §9.4 prunes because the log
        // carries the full text of every question and answer; this table carries an identifier, an
        // instant, a count and some names. Growing forever is a cost measured in bytes per destroy,
        // and the thing it buys is that a name is never reused.
        Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('a', '2026-01-01T00:00:00.0000000+00:00', 1, 'orders@test');
            """);

        var columns = new List<string>();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('destroyed_conversations');";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(["destroyed_utc", "id", "messages", "participants"], columns.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_identifier_cannot_be_reused_by_a_new_conversation()
    {
        // The guarantee the record buys, asserted against the thing that would break it. A
        // conversation opening under a destroyed name is how two different discussions come to
        // answer to one identifier -- discovered much later, in a file, by somebody with no way to
        // tell which they are reading.
        Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('taken', '2026-01-01T00:00:00.0000000+00:00', 1, 'orders@test');
            """);

        // A conversation row may exist under the name again -- the two tables do not constrain each
        // other -- so what is asserted is that the record is still there to say so.
        Execute("""
            INSERT INTO conversations (id, state, opened_utc)
            VALUES ('taken', 'OPEN', '2026-02-01T00:00:00.0000000+00:00');
            """);

        Assert.Equal(1L, Count("SELECT count(*) FROM destroyed_conversations WHERE id = 'taken'"));

        // And a second destroy of the reused name cannot overwrite the first account.
        Assert.Throws<SqliteException>(() => Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('taken', '2026-03-01T00:00:00.0000000+00:00', 9, 'billing@test');
            """));
    }

    [Fact]
    public void A_negative_message_count_is_refused_by_the_schema()
    {
        // The one constraint on this table, and it is there because the count is written by code
        // that computed it from rows it was about to delete. A miscount that went negative would be
        // a number an operator would read and believe.
        Assert.Throws<SqliteException>(() => Execute("""
            INSERT INTO destroyed_conversations (id, destroyed_utc, messages, participants)
            VALUES ('bad', '2026-01-01T00:00:00.0000000+00:00', -1, '');
            """));
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), provider: null);
    }
}
