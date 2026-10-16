using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That §7.1's open-request cap is off by default, and still held by storage when set.
/// </summary>
/// <remarks>
/// <para>
/// The cap was 5 and is now unbounded. Two things have to be true at once and they pull against
/// each other: nothing may refuse a sixth question under the default, and a deployment that sets
/// a cap must have it enforced where §9.3 says invariants belong — in the schema, not in broker
/// code a bug can route around.
/// </para>
/// <para>
/// <b>The trigger is probed directly rather than through the repository</b>, which is the whole
/// point. The repository checks the cap too, so a test going through it would pass with the
/// trigger dropped, missing exactly the failure §9.3 exists to prevent: application code and
/// storage disagreeing about a rule, with only one of them consulted.
/// </para>
/// <para>
/// <b>Unbounded is spelled as the top of the band because 0 cannot be stored.</b> The column
/// carries <c>CHECK (open_pair_cap BETWEEN 1 AND 50)</c>, SQLite cannot alter a CHECK, and
/// <c>exchanges</c> cannot be rebuilt inside this migrator — <c>DROP TABLE</c> under an enabled
/// foreign key fails on the first child row, and a migration runs in a transaction where the
/// pragma cannot be turned off. <c>006_unbounded_pair_cap.sql</c> carries the probe output.
/// </para>
/// </remarks>
public sealed class UnboundedPairCapTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Enough open exchanges on one pair that the old trigger would have refused.</summary>
    /// <remarks>
    /// Above <see cref="ExchangePolicy.Unbounded"/> rather than merely above the old default of
    /// five. A count below the sentinel cannot distinguish a trigger that stands down from one
    /// that is simply not reached, and a test that cannot fail is not one.
    /// </remarks>
    private const int Past = ExchangePolicy.Unbounded + 5;

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();

    public UnboundedPairCapTests()
    {
        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "test", AgentTier.Full, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task The_default_admits_far_more_than_the_old_cap()
    {
        // Twelve, which is well past 5 and past any number somebody would mistake for a new cap.
        for (var i = 0; i < 12; i++)
        {
            // Work.OpenAsync throws on a refusal, naming it.
            await Work.OpenAsync(
                _connection, Orders, Billing, T0, Work.Request($"request {i}"),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void The_trigger_stands_down_at_the_sentinel()
    {
        // Written straight into the table, so the only thing that could refuse these is the
        // trigger.
        //
        // **Past the sentinel rather than merely past the default cap, and that distinction is
        // what makes this a test.** Opening ten at the sentinel would pass against a trigger with
        // a cap of fifty too — ten never reaches it, so it would prove nothing about
        // whether the trigger stands down. These rows are only admitted if it does.
        for (var i = 0; i < Past; i++)
        {
            Open(i + 1, ExchangePolicy.Unbounded);
        }

        Assert.Equal(Past, Scalar("SELECT count(*) FROM exchanges WHERE state = 'OPEN';"));
    }

    [Fact]
    public void The_trigger_still_holds_a_cap_a_deployment_sets()
    {
        // The half that must survive the change. Three admitted, the fourth refused by storage
        // with nothing else in the way.
        for (var i = 0; i < 3; i++)
        {
            Open(i + 1, cap: 3);
        }

        var refused = Assert.Throws<SqliteException>(() => Open(4, cap: 3));

        Assert.Contains("open_pair_cap reached", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cap_one_below_the_sentinel_is_still_a_cap()
    {
        // The sentinel must not have swallowed the top of the band. 49 is a real cap, so a
        // forty-ninth open exchange is refused rather than admitted as though unbounded.
        for (var i = 0; i < ExchangePolicy.Unbounded - 1; i++)
        {
            Open(i + 1, cap: ExchangePolicy.Unbounded - 1);
        }

        Assert.Throws<SqliteException>(() => Open(ExchangePolicy.Unbounded, cap: ExchangePolicy.Unbounded - 1));
    }

    /// <summary>Writes one open exchange straight into the table, bypassing every check but the triggers.</summary>
    /// <param name="id">The identifier, so the rows are distinguishable.</param>
    /// <param name="cap">The terms to stamp on it.</param>
    private void Open(int id, int cap)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing', 'OPEN', $at)
              ON CONFLICT DO NOTHING;
            INSERT INTO exchanges
              (id, requester, receiver, pair_key, state, owed_by, request_hash,
               clarify_depth, clarify_depth_cap, open_pair_cap,
               uncollected_flag_ms, stalled_flag_ms, opened_utc, conversation_id)
            VALUES
              ($id, $asker, $answerer, $pair, 'OPEN', $answerer, $hash,
               0, 200, $cap, 900000, 14400000, $at, 'pricing');
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$asker", Orders.Value);
        command.Parameters.AddWithValue("$answerer", Billing.Value);
        command.Parameters.AddWithValue("$pair", PairKey.For(Orders, Billing).Value);
        command.Parameters.AddWithValue("$cap", cap);

        // Distinctness is keyed on the Request, and ux_open_request would otherwise refuse the
        // second row for a reason that has nothing to do with the cap.
        command.Parameters.AddWithValue(
            "$hash",
            id.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0'));
        command.Parameters.AddWithValue("$at", T0.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));

        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
