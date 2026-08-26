using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The bound on <c>clarify_depth_cap</c> that SPEC.md §9.3 argued for and then did not have.
/// </summary>
/// <remarks>
/// <para>
/// §9.3's case for bounding <c>open_pair_cap</c> is that a broker bug writing a large value would
/// disable the rule while leaving every constraint satisfied and nothing to notice. It then says
/// the neighbouring column has the same shape and no such bound. These are the probes for the
/// bound that closes it — written as attempts to defeat the cap rather than as a reading of the
/// trigger, because a trigger with a wrong comparison is invisible to inspection and obvious to
/// an insert.
/// </para>
/// <para>
/// The reason it stopped being theoretical is that the cap became a launch setting in the same
/// change. A number that used to be the constant 3 now arrives from an environment variable,
/// which is the path a typo travels.
/// </para>
/// </remarks>
public sealed class ClarifyCapBoundsTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;

    public ClarifyCapBoundsTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Execute("INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders','test','full','offline'),('billing','test','full','offline');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Write(string question, int clarifyDepthCap) => Execute(
        "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
      + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
      + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
      + "('billing' || char(31) || 'orders','orders','billing','billing','OPEN',0,"
      + clarifyDepthCap.ToString(System.Globalization.CultureInfo.InvariantCulture)
      + ",5,900000,14400000,86400000,'" + Stamp + "','" + Stamp + "','" + question + "');");

    [Fact]
    public void The_default_the_domain_ships_is_inside_the_band()
    {
        // The band and the default are decided in different files, so this is the check that
        // they were decided compatibly. A band excluding the default would refuse every
        // exchange in a deployment that configured nothing.
        Write("q", ExchangePolicy.Default.ClarifyDepthCap);
    }

    [Fact]
    public void A_cap_of_zero_is_admitted_because_it_is_a_policy_and_not_a_mistake()
    {
        // Zero means the answerer must answer with what it has. Somebody may want that, and the
        // domain permits it, so storage refusing it would make the strictest available setting
        // the one value that cannot be deployed.
        Write("q", 0);
    }

    [Fact]
    public void The_measurement_setting_is_admitted()
    {
        // 200 is the value SPEC.md §17 question 4 proposes running to read an uncensored
        // distribution. A bound that refused it would make the bound the reason the question
        // stays open.
        Write("q", 200);
    }

    [Fact]
    public void The_top_of_the_band_is_admitted_and_one_past_it_is_not()
    {
        Write("top", 1000);

        Assert.Throws<SqliteException>(() => Write("over", 1001));
    }

    [Fact]
    public void A_negative_cap_is_refused()
    {
        // Worth probing separately from the upper bound. `clarify_depth <= clarify_depth_cap`
        // was already there, and at depth zero a negative cap violates it — so a bound checked
        // only at the top would have looked correct here for the wrong reason. This asserts the
        // row is refused, not that some constraint somewhere objected.
        Assert.Throws<SqliteException>(() => Write("negative", -1));
    }

    [Fact]
    public void An_enormous_cap_is_refused_rather_than_silently_disabling_the_rule()
    {
        // The failure §9.3 describes, written out: a value that switches the cap off while every
        // constraint still passes. Before the bound this insert succeeded.
        Assert.Throws<SqliteException>(() => Write("huge", int.MaxValue));
    }

    [Fact]
    public void An_update_cannot_raise_the_cap_out_of_the_band_either()
    {
        // No operation does this today. The trigger exists because "no operation does this" is a
        // fact about the current broker, and an INSERT-only guard would be defeated by the first
        // one that did.
        Write("q", 3);

        Assert.Throws<SqliteException>(() => Execute(
            "UPDATE exchanges SET clarify_depth_cap = 100000 WHERE question_hash = 'q';"));
    }

    [Fact]
    public void An_update_within_the_band_is_left_alone()
    {
        // The guard has to be a bound and not a freeze. If this failed, the trigger would be
        // refusing legitimate writes and the previous test would pass for the wrong reason.
        Write("q", 3);

        Execute("UPDATE exchanges SET clarify_depth_cap = 200 WHERE question_hash = 'q';");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT clarify_depth_cap FROM exchanges WHERE question_hash = 'q';";

        Assert.Equal(200L, (long)command.ExecuteScalar()!);
    }
}
