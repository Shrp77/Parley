using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That raising the message cap carried the database across rather than rebuilding it wrongly.
/// </summary>
/// <remarks>
/// <para>
/// SQLite cannot alter a <c>CHECK</c>, and §9.3 puts the cap in one on purpose, so moving the
/// number means rebuilding <c>messages</c> and <c>instructions</c> — the busiest table in the
/// store and the one holding what a person typed. Every failure mode of that rebuild is silent:
/// rows lost, a trigger not put back, a counter doubled. None of them raises anything.
/// </para>
/// <para>
/// The doubling is the one worth naming. Two <c>AFTER INSERT</c> triggers on <c>messages</c>
/// write to other tables, and copying rows into a table that already carried them would count
/// every referral and every notification a second time — into the two reports §17 question 1
/// is being decided from. The migration copies before it attaches them, and this proves it.
/// </para>
/// </remarks>
public sealed class MessageCapMigrationTests
{
    /// <summary>The tables and counters an upgrade must carry across untouched.</summary>
    /// <returns>A database migrated to the current version, holding traffic written at v2.</returns>
    private static SqliteConnection Upgraded(out int notifies, out long largestId)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Configure(connection);
        SchemaMigrator.Migrate(connection);

        Register(connection, "orders@test");
        Register(connection, "billing@test");

        // Notifications, because they are what the doubling would have inflated.
        for (var i = 0; i < 5; i++)
        {
            Notify(connection, "orders@test", "billing@test", $"notice {i}", i);
        }

        notifies = 5;
        largestId = Scalar<long>(connection, "SELECT max(id) FROM messages;");

        return connection;
    }

    [Fact]
    public void The_cap_the_schema_enforces_is_the_one_the_code_states()
    {
        // The whole point of the constant. An adapter refusing at a different number than the
        // store means either messages refused that would have stored, or a SQL constraint
        // reaching a model instead of a sentence about being terser.
        using var connection = Upgraded(out _, out _);

        Notify(connection, "orders@test", "billing@test",
            new string('x', MessageLimits.MaxPayloadCharacters - 20), at: 90);

        var over = Assert.Throws<SqliteException>(() => Notify(
            connection, "orders@test", "billing@test",
            new string('y', MessageLimits.MaxPayloadCharacters + 1), at: 91));

        Assert.Contains("CHECK constraint failed", over.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_instruction_is_capped_at_the_same_number_as_a_message()
    {
        // Deliberately the same number rather than a second one: both land in an agent's
        // context, and 002's own comment says the two would otherwise drift "at the worst
        // moment -- when evidence moves the message cap".
        using var connection = Upgraded(out _, out _);

        Instruct(connection, new string('x', MessageLimits.MaxPayloadCharacters));

        Assert.Throws<SqliteException>(() =>
            Instruct(connection, new string('x', MessageLimits.MaxPayloadCharacters + 1)));
    }

    [Fact]
    public void Rebuilding_the_table_did_not_double_what_its_triggers_count()
    {
        // The silent one. tr_notify_volume_recorded fires AFTER INSERT, so a copy made into a
        // table that already had it would add a second row's worth of every notification ever
        // sent -- and notify_volume is what `parley notifications` reports, which is half the
        // evidence §17 question 1 is waiting on.
        using var connection = Upgraded(out var notifies, out _);

        Assert.Equal(
            notifies,
            Scalar<long>(connection, "SELECT sum(sent) FROM notify_volume;"));

        Assert.Equal(
            notifies,
            Scalar<long>(connection, "SELECT count(*) FROM messages WHERE type = 'notify';"));
    }

    [Theory]
    [InlineData("tr_gates_are_syntactic")]
    [InlineData("tr_ghost_is_the_brokers")]
    [InlineData("tr_referral_targets_exist")]
    [InlineData("tr_referrals_recorded")]
    [InlineData("tr_notify_volume_recorded")]
    [InlineData("tr_ghost_needs_no_answer")]
    public void Every_trigger_the_rebuild_dropped_was_put_back(string trigger)
    {
        // Dropping the table drops its triggers, and the last of these lives on `exchanges` but
        // reads `messages` — so it has to be dropped by hand before the rename and restored
        // after. A guard that is not put back fails open: nothing errors, and the rule it
        // carried is simply gone.
        using var connection = Upgraded(out _, out _);

        Assert.Equal(1, Scalar<long>(
            connection,
            $"SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name = '{trigger}';"));
    }

    [Theory]
    [InlineData("ux_message_dedup")]
    [InlineData("ix_instructions_agent")]
    public void Every_index_the_rebuild_dropped_was_put_back(string index)
    {
        // ux_message_dedup is what enforces §13.1's distinctness. Losing it admits the duplicate
        // messages the index exists to refuse, and nothing would report that either.
        using var connection = Upgraded(out _, out _);

        Assert.Equal(1, Scalar<long>(
            connection,
            $"SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = '{index}';"));
    }

    [Fact]
    public void Identity_keeps_counting_from_where_it_was()
    {
        // messages.id is AUTOINCREMENT, and DROP TABLE takes its sqlite_sequence row with it.
        // A sequence that restarted would hand a new message an identifier an old one already
        // used, which every exchange and every report keys on.
        using var connection = Upgraded(out _, out var largestId);

        Assert.Equal(
            largestId,
            Scalar<long>(connection, "SELECT seq FROM sqlite_sequence WHERE name = 'messages';"));

        Notify(connection, "orders@test", "billing@test", "after the rebuild", at: 60);

        Assert.True(
            Scalar<long>(connection, "SELECT max(id) FROM messages;") > largestId,
            "a message written after the rebuild reused an identifier");
    }

    [Fact]
    public void The_rebuild_left_the_database_sound()
    {
        using var connection = Upgraded(out _, out _);

        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));

        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";

        using var reader = check.ExecuteReader();

        Assert.False(reader.Read(), "the rebuild left a row referring to something absent");
    }

    /// <summary>Registers an agent so messages may reference it.</summary>
    /// <param name="connection">The database.</param>
    /// <param name="name">The agent.</param>
    private static void Register(SqliteConnection connection, string name)
    {
        // Through the repository rather than by hand, so this cannot disagree with the schema
        // about which columns exist or which of them are generated.
        new AgentRepository(connection).UpsertAsync(
            new AgentRecord(
                AgentName.Create(name), "test", AgentTier.Pull,
                null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>Writes one notification straight into the store.</summary>
    /// <param name="connection">The database.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="text">What it says.</param>
    /// <param name="at">A minute offset, to keep content hashes and dedup buckets apart.</param>
    private static void Notify(
        SqliteConnection connection, string from, string to, string text, int at)
    {
        var payload = $"{{\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}";

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO messages (type, from_agent, to_agent, responder_model, payload_json, " +
            "content_hash, created_utc) VALUES ('notify', $f, $t, 'test', $p, $h, $w);";
        command.Parameters.AddWithValue("$f", from);
        command.Parameters.AddWithValue("$t", to);
        command.Parameters.AddWithValue("$p", payload);
        command.Parameters.AddWithValue("$h", Hash(payload + at.ToString(CultureInfo.InvariantCulture)));

        // Arithmetic rather than formatting. Interpolating the offset into the minutes field
        // produced "00:90:00Z" past the hour, which is not a time — and the store said so, via
        // the CHECK that exists because a timestamp nothing can parse is a row no report can
        // order.
        command.Parameters.AddWithValue(
            "$w",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMinutes(at)
                .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

        command.ExecuteNonQuery();
    }

    /// <summary>Writes one operator instruction straight into the store.</summary>
    /// <param name="connection">The database.</param>
    /// <param name="text">What it says.</param>
    private static void Instruct(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO instructions (agent, text, outcome, issued_utc) " +
            "VALUES ('orders@test', $t, 'pending', '2026-01-01T00:00:00Z');";
        command.Parameters.AddWithValue("$t", text);
        command.ExecuteNonQuery();
    }

    /// <summary>A content hash of the right shape.</summary>
    /// <param name="text">What to hash.</param>
    /// <returns>Sixty-four lower-case hexadecimal characters.</returns>
    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Reads one value.</summary>
    /// <typeparam name="T">What to read it as.</typeparam>
    /// <param name="connection">The database.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The value.</returns>
    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)Convert.ChangeType(
            command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
