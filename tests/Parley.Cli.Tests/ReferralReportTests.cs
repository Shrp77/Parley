using System.Globalization;
using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What an operator sees when they ask where the ownership map is wrong.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §15 calls the <c>out_of_scope</c> rate per pair the signal that the map is wrong
/// somewhere. What makes it usable is that it points at a pair rather than at an agent: a
/// referral is two repositories disagreeing about a boundary, and reading it as one agent
/// behaving badly sends the fix to the wrong place.
/// </para>
/// <para>
/// The halves also have different reach, and the report has to be honest about it. The rate is
/// counted from exchanges, which retention never touches. The names come from the referral record
/// kept beside the messages. A test that only checked the numbers would pass while the report
/// implied both covered the same period.
/// </para>
/// </remarks>
public sealed class ReferralReportTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly OperatorCommands _commands;
    private int _rows;

    public ReferralReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _commands = new OperatorCommands(new OperatorConsole(_connection), _output);

        Execute("INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders','test','full','registered'),('billing','test','full','registered'),"
              + "('inventory','test','full','registered'),('pricing','test','full','registered');");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _output.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Printed => _output.ToString();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Writes a closed exchange, returning its id.</summary>
    private long Closed(string asker, string answerer, string resolution)
    {
        var n = ++_rows;

        Execute(
            "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
          + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
          + "grace_period_ms, opened_utc, expires_utc, closed_utc, resolution, question_hash) "
          + "VALUES (min('" + asker + "','" + answerer + "') || char(31) || max('" + asker
          + "','" + answerer + "'),'" + asker + "','" + answerer + "','" + answerer + "',"
          + "'CLOSED',0,3,5,900000,14400000,86400000,'" + Stamp + "','" + Stamp + "','" + Stamp
          + "','" + resolution + "','h" + n.ToString(CultureInfo.InvariantCulture) + "');");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT max(id) FROM exchanges;";
        return (long)command.ExecuteScalar()!;
    }

    private void Referral(long exchangeId, string answerer, string asker, string target)
    {
        var n = ++_rows;

        Execute(
            "INSERT INTO message_referrals (message_id, exchange_id, sub_question, from_agent, "
          + "to_agent, redirected_to, created_utc) VALUES (" + n.ToString(CultureInfo.InvariantCulture)
          + "," + exchangeId.ToString(CultureInfo.InvariantCulture) + ",'q1','" + answerer + "','"
          + asker + "','" + target + "','" + Stamp + "');");
    }

    [Fact]
    public async Task An_empty_deployment_says_which_of_the_two_reasons_it_is()
    {
        // "Nothing referred" is ambiguous in a way that matters: it is either a project whose
        // ownership map is right, or one that has barely been used. Reporting only the count
        // would let the second be read as the first.
        Assert.Equal(0, await _commands.ReferralsAsync(Ct));

        Assert.Contains("Nothing has been referred", Printed, StringComparison.Ordinal);
        Assert.Contains("too little has been asked", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rate_is_counted_over_every_closed_exchange_and_not_only_the_referrals()
    {
        // The denominator is the point. One referral out of two is a boundary worth a
        // conversation; one out of fifty is noise, and a report that printed only the numerator
        // could not tell them apart.
        Closed("orders", "billing", "answered");
        Closed("orders", "billing", "answered");
        Closed("orders", "billing", "answered");
        var referred = Closed("orders", "billing", "out_of_scope");

        Referral(referred, "billing", "orders", "inventory");

        await _commands.ReferralsAsync(Ct);

        Assert.Contains("orders", Printed, StringComparison.Ordinal);
        Assert.Contains("billing", Printed, StringComparison.Ordinal);
        Assert.Contains("25.0%", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pair_that_never_refers_is_not_listed_at_all()
    {
        // The report is a list of places to look. Every healthy pair in it is noise that makes
        // the unhealthy one harder to find.
        Closed("orders", "billing", "answered");
        Closed("orders", "billing", "answered");

        await _commands.ReferralsAsync(Ct);

        Assert.Contains("Nothing has been referred", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Where_the_work_was_sent_is_reported_beside_the_rate()
    {
        // A rate says a boundary is wrong; the target says which boundary. Without the second
        // half an operator knows there is a problem and not where to take it.
        var a = Closed("orders", "billing", "out_of_scope");
        var b = Closed("orders", "billing", "out_of_scope");

        Referral(a, "billing", "orders", "inventory");
        Referral(b, "billing", "orders", "inventory");

        await _commands.ReferralsAsync(Ct);

        Assert.Contains("inventory", Printed, StringComparison.Ordinal);
        Assert.Contains("where they were sent instead", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_owners_named_for_one_sub_question_are_both_reported()
    {
        // The shape §5.3 admits and the tool can now send. If the report collapsed them, the
        // second boundary would be invisible in exactly the case where a question spans two.
        var a = Closed("orders", "billing", "out_of_scope");

        Referral(a, "billing", "orders", "inventory");
        Referral(a, "billing", "orders", "pricing");

        await _commands.ReferralsAsync(Ct);

        Assert.Contains("inventory", Printed, StringComparison.Ordinal);
        Assert.Contains("pricing", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_says_a_high_rate_is_not_an_agent_behaving_badly()
    {
        // The reading, and it is load-bearing rather than decoration. An operator who takes this
        // as a fault in the answerer will go and change that repository, when the thing to change
        // is where ownership is written down and the asker's idea of who owns what.
        var a = Closed("orders", "billing", "out_of_scope");
        Referral(a, "billing", "orders", "inventory");

        await _commands.ReferralsAsync(Ct);

        Assert.Contains("not an agent behaving badly", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_message_text_can_appear_because_none_is_read()
    {
        // The referral record keeps names, a sub-question identifier and a timestamp. What was
        // said is a message under §9.4, and an operator reading this is outside the §13 envelope
        // that marks a peer's words as somebody else's.
        var a = Closed("orders", "billing", "out_of_scope");
        Referral(a, "billing", "orders", "inventory");

        await _commands.ReferralsAsync(Ct);

        Assert.DoesNotContain("payload", Printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not mine", Printed, StringComparison.OrdinalIgnoreCase);
    }
}
