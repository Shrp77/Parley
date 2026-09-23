using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The report SPEC.md §17 question 4 needs before a clarify cap can be argued about.
/// </summary>
/// <remarks>
/// <para>
/// The figures matter more than most reports because of what they will be used for: somebody deciding
/// whether a protocol rule should exist. A count that is plausible and wrong here does not produce a visible
/// failure — it produces a confident change to the spec.
/// </para>
/// <para>
/// So these probe the ways this query shape goes wrong: a join multiplying exchanges by their messages, a
/// cap of zero counted as reached, and a single-item Result counted as mixed.
/// </para>
/// </remarks>
public sealed class ClarifyReportTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ClarifyReportRepository _report;

    public ClarifyReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing);
        _report = new ClarifyReportRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Opens an exchange and clarifies it to a depth, the receiver and requester taking turns.</summary>
    private async Task<Exchange> Clarified(string subject, int depth, int cap = ExchangePolicy.UnboundedClarifications, params string[] items)
    {
        var payload = items.Length == 0 ? Work.Request(subject) : Work.RequestOf(subject, items);

        var (exchange, _) = await Work.OpenAsync(
            _connection, Orders, Billing, T0, payload, policy: ExchangePolicy.Default with { ClarifyDepthCap = cap },
            cancellationToken: Ct);

        for (var round = 0; round < depth; round++)
        {
            Assert.True(exchange.Clarify(Billing, T0.AddMinutes(round * 2 + 1)).Succeeded);
            Assert.True(exchange.Clarify(Orders, T0.AddMinutes(round * 2 + 2)).Succeeded);
        }

        Assert.True(await new ExchangeRepository(_connection).SaveAsync(exchange, cancellationToken: Ct));

        return exchange;
    }

    [Fact]
    public async Task An_empty_deployment_reports_nothing_rather_than_a_shape()
    {
        var report = await _report.ReadAsync(Ct);

        Assert.Equal(0, report.Exchanges);
        Assert.Empty(report.Distribution);
        Assert.Equal(0, report.DeepestReached);
    }

    [Fact]
    public async Task The_distribution_counts_exchanges_and_includes_the_ones_that_never_clarified()
    {
        // Depth zero is the decisive bucket. A GROUP BY over only clarified exchanges would drop it, and every
        // deployment would look like one where clarification is normal.
        await Clarified("a", depth: 0);
        await Clarified("b", depth: 0);
        await Clarified("c", depth: 2);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(3, report.Exchanges);
        Assert.Equal(2, report.Distribution.Single(b => b.Depth == 0).Exchanges);
        Assert.Equal(1, report.Distribution.Single(b => b.Depth == 2).Exchanges);
        Assert.Equal(2, report.DeepestReached);
    }

    [Fact]
    public async Task Messages_do_not_multiply_the_exchange_counts()
    {
        // One exchange carrying a Request, two clarifications and a Result is still one exchange; a join would
        // make it four and the distribution would stay plausible while every total was wrong.
        var exchange = await Clarified("a", depth: 1);
        var messages = new MessageRepository(_connection);
        await messages.AppendAsync(exchange.Id, MessageType.Clarify, Billing, Orders, Work.Model, """{"text":"which tax?"}""", T0.AddMinutes(1), cancellationToken: Ct);
        await messages.AppendAsync(exchange.Id, MessageType.Clarify, Orders, Billing, Work.Model, """{"text":"VAT"}""", T0.AddMinutes(2), cancellationToken: Ct);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(3), cancellationToken: Ct);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Exchanges);
        Assert.Equal(1, report.Distribution.Single(b => b.Depth == 1).Exchanges);
    }

    [Fact]
    public async Task Reaching_a_set_cap_is_counted_and_a_cap_of_zero_is_not_counted_as_reached()
    {
        // At a cap of zero every exchange satisfies `depth >= cap` at depth zero. Counted naively, the
        // strictest setting would report as universally breached when nothing has clarified at all.
        await Clarified("at", depth: 3, cap: 3);
        await Clarified("under", depth: 1, cap: 3);
        await Clarified("none", depth: 0, cap: 0);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.ExchangesAtCap);
    }

    [Fact]
    public async Task An_uncapped_exchange_never_counts_as_at_its_cap()
    {
        await Clarified("deep", depth: 5);

        Assert.Equal(0, (await _report.ReadAsync(Ct)).ExchangesAtCap);
    }

    [Fact]
    public async Task The_caps_in_force_are_reported_so_the_distribution_can_be_read()
    {
        // Without these the histogram is unreadable: a pile at the top means the measurement was truncated,
        // and you cannot see the top without being told what it was.
        await Clarified("a", depth: 0, cap: 3);
        await Clarified("b", depth: 0, cap: 200);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(3, report.LowestCapInForce);
        Assert.Equal(200, report.HighestCapInForce);
    }

    [Fact]
    public async Task A_result_giving_its_items_one_outcome_is_not_mixed()
    {
        var exchange = await Clarified("a", depth: 0, items: ["q1", "q2", "q3"]);
        await Work.CloseAsync(
            _connection, exchange, T0.AddMinutes(1),
            payload: Work.Result(("q1", "answered"), ("q2", "answered"), ("q3", "answered")), cancellationToken: Ct);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Results);
        Assert.Equal(0, report.ResultsWithMixedOutcomes);
    }

    [Fact]
    public async Task A_result_giving_its_items_different_outcomes_is()
    {
        // The test of whether items carry information. If no Result anywhere ever treats them differently,
        // the structure is formatting.
        var exchange = await Clarified("a", depth: 0, items: ["q1", "q2"]);
        await Work.CloseAsync(
            _connection, exchange, T0.AddMinutes(1), ExchangeOutcome.Blocked,
            Work.Result(("q1", "done"), ("q2", "blocked:permission")), Ct);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Results);
        Assert.Equal(1, report.ResultsWithMixedOutcomes);
    }

    [Fact]
    public async Task A_single_item_is_never_mixed()
    {
        // count(DISTINCT ...) > 1 over one element must be false; an off-by-one would report every Result as
        // mixed and settle a design question in the direction of more machinery.
        var exchange = await Clarified("a", depth: 0);
        await Work.CloseAsync(_connection, exchange, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(0, (await _report.ReadAsync(Ct)).ResultsWithMixedOutcomes);
    }
}
