using Microsoft.Data.Sqlite;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What an operator sees when they ask what clarification cost.
/// </summary>
/// <remarks>
/// <para>
/// The output is the deliverable, because of what it will be used for: deciding whether the cap
/// of SPEC.md §5.2 should exist. A histogram printed without saying where it was truncated is a
/// censored measurement presented as a natural one, and somebody would read a number off it and
/// change the spec.
/// </para>
/// <para>
/// So these assert the reading and not only the figures. The distinction the report has to carry
/// is between "nothing reached the cap, so this is the real distribution" and "everything is
/// stopping at the cap, so the real distribution is not in this data" — those look nearly
/// identical as a table of counts and mean opposite things.
/// </para>
/// </remarks>
public sealed class ClarificationReportTests : IAsyncDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;
    private readonly StringWriter _output = new();
    private readonly LoopbackOperator _broker;
    private readonly OperatorCommands _commands;

    public ClarificationReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
        _commands = new OperatorCommands(_broker.Operators, _output);

        Execute("INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders@test','test','full','active'),('billing@test','test','full','active');"
              + "INSERT INTO conversations (id, state, opened_utc) VALUES ('c1', 'OPEN', '" + Stamp + "');");
    }

    public async ValueTask DisposeAsync()
    {
        // The broker first: it borrowed this connection and its shutdown writes to it.
        await _broker.DisposeAsync();

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

    private void Exchange(string request, int depth, int cap) => Execute(
        "INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, clarify_depth, "
      + "clarify_depth_cap, open_pair_cap, uncollected_flag_ms, stalled_flag_ms, "
      + "opened_utc, request_hash, conversation_id) VALUES "
      + "('billing@test' || char(31) || 'orders@test','orders@test','billing@test','billing@test','OPEN'," + depth + ","
      + cap + ",5,900000,14400000,'" + Stamp + "','" + request + "','c1');");

    [Fact]
    public async Task An_empty_deployment_says_there_is_no_evidence_rather_than_printing_a_table()
    {
        // The ordinary state of a new deployment, and the one where somebody is most likely to be
        // looking because they want the number changed. "Nothing has happened yet" answers that;
        // an empty histogram does not.
        Assert.Equal(0, await _commands.ClarificationsAsync(Ct));

        Assert.Contains("No exchanges yet", Printed, StringComparison.Ordinal);
        Assert.Contains("no evidence", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deployment_that_never_clarifies_is_told_the_cap_is_not_what_is_keeping_depth_low()
    {
        // The most likely real finding, and the one a bare histogram would bury. Three rows of
        // "depth 0" is not obviously an argument for removing a rule; saying so is.
        Exchange("a", depth: 0, cap: 3);
        Exchange("b", depth: 0, cap: 3);

        await _commands.ClarificationsAsync(Ct);

        Assert.Contains("Nothing has clarified even once", Printed, StringComparison.Ordinal);
        Assert.Contains("nothing here to tune", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncapped_exchanges_are_reported_as_having_no_cap_rather_than_a_huge_one()
    {
        // The default is no clarification cap, stored as the largest integer. Printed as a number,
        // the report would tell somebody deciding whether to set a cap that one was already set, at
        // a value nobody chose. Depth past the old storage band is included so the "real one"
        // reading is exercised at the default rather than only under a small cap.
        Exchange("a", depth: 0, cap: int.MaxValue);
        Exchange("b", depth: 5000, cap: int.MaxValue);

        Assert.Equal(0, await _commands.ClarificationsAsync(Ct));

        Assert.Contains("Caps in force: none.", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("2147483647", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("censored", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_distribution_that_piles_up_at_the_cap_is_reported_as_censored()
    {
        // The failure this whole report exists to prevent. Exchanges stopping at the cap means
        // the depth they would have reached is not in the data, so the histogram cannot be read
        // as the truth about how deep clarification goes.
        Exchange("a", depth: 3, cap: 3);
        Exchange("b", depth: 3, cap: 3);

        await _commands.ClarificationsAsync(Ct);

        Assert.Contains("censored", Printed, StringComparison.Ordinal);
        Assert.Contains("Raise the cap", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_distribution_that_never_reaches_the_cap_is_reported_as_the_real_one()
    {
        // The other side, and the one that licenses acting on the number. Without this the report
        // would hedge identically whatever the data said, which is the same as saying nothing.
        Exchange("a", depth: 1, cap: 200);
        Exchange("b", depth: 2, cap: 200);

        await _commands.ClarificationsAsync(Ct);

        Assert.Contains("real one", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("censored", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_caps_in_force_are_printed_beside_the_distribution()
    {
        // Without them the histogram cannot be read at all, so their absence would not be a
        // missing detail — it would make every other number in the report unusable.
        Exchange("a", depth: 0, cap: 3);
        Exchange("b", depth: 1, cap: 200);

        await _commands.ClarificationsAsync(Ct);

        Assert.Contains("Caps in force: 3 to 200", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_message_text_can_appear_because_the_report_reads_no_payloads()
    {
        // A clarification is a message under the retention policy of §9.4, and it is one agent
        // speaking to another. This report is read by an operator outside the §13 envelope that
        // marks a peer's words as data, so it must count and never quote.
        Exchange("a", depth: 2, cap: 3);

        await _commands.ClarificationsAsync(Ct);

        Assert.DoesNotContain("payload", Printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("orders@test", Printed, StringComparison.Ordinal);
    }
}
