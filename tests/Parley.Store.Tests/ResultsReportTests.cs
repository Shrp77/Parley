using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The report of SPEC.md §15: what Results said, across a deployment and across its whole life.
/// </summary>
/// <remarks>
/// It is read from rows the schema fills on every Result and retention never prunes, so the checks here are
/// that a Result lands in the right counts once — including the broker's own <c>closed</c> Results, which are
/// the record that a person ended work, and must not be mistaken for work an agent finished.
/// </remarks>
public sealed class ResultsReportTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ResultsReportRepository _report;

    public ResultsReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(_connection, Orders, Billing, Inventory);
        _report = new ResultsReportRepository(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_deployment_reports_nothing()
    {
        Assert.True((await _report.ReadAsync(Ct)).IsEmpty);
    }

    [Fact]
    public async Task Outcomes_are_counted_per_item_under_the_participant_and_responder_that_reported_them()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, Work.RequestOf("totals", "q1", "q2", "q3"), cancellationToken: Ct);
        await Work.CloseAsync(
            _connection, exchange, T0.AddMinutes(1), ExchangeOutcome.Blocked,
            Work.Result(("q1", "done"), ("q2", "done"), ("q3", "blocked:permission")), Ct);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(
            ["billing@test|claude-opus-5|Blocked|1", "billing@test|claude-opus-5|Done|2"],
            report.Outcomes.Select(o => $"{o.Agent}|{o.ResponderModel}|{o.Outcome}|{o.Items}").Order(StringComparer.Ordinal));

        var blocker = Assert.Single(report.Blockers);
        Assert.Equal(BlockerKind.Permission, blocker.Kind);
        Assert.Equal("billing@test", blocker.Agent);
    }

    [Fact]
    public async Task Sources_say_whose_word_a_result_rested_on()
    {
        var (exchange, _) = await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await Work.CloseAsync(
            _connection, exchange, T0.AddMinutes(1),
            payload: """{"outcomes":[{"id":"q1","outcome":"answered","text":"net","sources":["inventory@test"]}]}""",
            cancellationToken: Ct);

        var source = Assert.Single((await _report.ReadAsync(Ct)).Sources);

        Assert.Equal("billing@test|inventory@test|1", $"{source.Agent}|{source.Source}|{source.Items}");
    }

    [Fact]
    public async Task A_person_closing_work_is_counted_as_closed_and_never_as_done()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);
        await new ConversationRepository(_connection).ForceConcludeAsync(Work.Conversation, null, T0.AddHours(1), cancellationToken: Ct);

        var report = await _report.ReadAsync(Ct);

        Assert.All(report.Outcomes, o =>
        {
            Assert.Equal(Outcome.Closed, o.Outcome);
            Assert.Equal(Responder.Broker, o.ResponderModel);
        });

        Assert.Equal(
            ["billing@test|0|1", "orders@test|0|1"],
            report.Closures.Select(c => $"{c.Agent}|{c.ByConsensus}|{c.ByPerson}"));
    }

    [Fact]
    public async Task An_open_conversation_is_not_counted_as_closed_either_way()
    {
        await Work.OpenAsync(_connection, Orders, Billing, T0, cancellationToken: Ct);

        Assert.Empty((await _report.ReadAsync(Ct)).Closures);
    }
}
