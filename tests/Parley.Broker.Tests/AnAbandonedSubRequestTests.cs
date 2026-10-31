using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// An agent's own request to another, still open when the item's conversation closes, after the person's request
/// was done.
/// </summary>
/// <remarks>
/// <b>Found by review, round 28.</b> Blocked counted only the person's requests (round 21), but closed counted every
/// exchange, so an agent's own onward request left open at the close made a done item closed for good. Both count
/// only the person's requests now.
/// </remarks>
[Integration]
public sealed class AnAbandonedSubRequestTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;

    public AnAbandonedSubRequestTests()
    {
        _connection.Execute(
            """
            INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-10-06T00:00:00.0000000Z');
            INSERT INTO agents (name, adapter, tier, status) VALUES
                ('orders@test', 'claude-code', 'full', 'active'),
                ('billing@test', 'claude-code', 'full', 'active');
            """);

        var agents = new AgentRepository(_connection);
        var project = ProjectName.Create("test");

        _ = agents.EnsureFacilitatorAsync(project).GetAwaiter().GetResult();
        _person = agents.EnsurePersonAsync(project).GetAwaiter().GetResult();

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.ServeFrom(T0);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> RoadmapAsync()
    {
        var proposed = await _broker.ProposeRoadmapAsync(
            Orders,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
            ]),
            null, null, T0, Ct);
        var content = (await _console.ListProposalsAsync(ProposalState.Waiting, Ct)).Single().Content;
        return (await _console.ConfirmProposalAsync(proposed.Id!.Value, content, null, T0, Ct)).RoadmapId!.Value;
    }

    private async Task<long> AskAsync(AgentName from, AgentName to, int minute, string text)
    {
        var sent = await _broker.RequestAsync(from, to, Requests.Model, Requests.Request(text), T0.AddMinutes(minute), "rm1-1", Ct);
        Assert.True(sent.Succeeded, sent.Rejection.ToString());
        return sent.Exchange!.Id;
    }

    private async Task<ItemFacts> FactsAsync(long roadmapId) =>
        (await _console.GetRoadmapAsync(roadmapId, Ct))!.Items.Single(i => i.Number == 1).Facts!;

    [Fact]
    public async Task The_persons_close_after_the_work_is_done_leaves_the_item_done()
    {
        var roadmap = await RoadmapAsync();

        var work = await AskAsync(_person, Billing, 0, "Add CSV export");
        Assert.True((await _broker.ResultAsync(
            work, Billing, Requests.Model, Requests.Result((PayloadShape.WholeRequest, "done")), T0.AddMinutes(1), Ct)).Succeeded);

        // Billing, done, tells orders about the new export; orders has not answered when the person closes.
        _ = await AskAsync(Billing, Orders, 2, "The CSV export is in; your report can call it");
        Assert.True(await _console.ForceConcludeAsync("rm1-1", null, T0.AddMinutes(3), Ct));

        var facts = await FactsAsync(roadmap);
        Assert.True(
            facts.ClosedExchanges == 0,
            $"the person's request was done, but billing's own notice to orders, open at the close, counts as work closed before its agent answered, so the item can never be done and item 2 is never next: {facts}");
    }

    [Fact]
    public async Task Two_agents_finished_over_an_open_sub_request_cannot_close_the_item_and_it_stays_done()
    {
        var roadmap = await RoadmapAsync();

        var work = await AskAsync(_person, Billing, 0, "Add CSV export");
        Assert.True((await _broker.ResultAsync(
            work, Billing, Requests.Model, Requests.Result((PayloadShape.WholeRequest, "done")), T0.AddMinutes(1), Ct)).Succeeded);

        _ = await AskAsync(Billing, Orders, 2, "The CSV export is in; your report can call it");
        // Only the person closes an item's conversation (SPEC.md section 5.7), so neither agent's word closes it.
        Assert.Equal(ClosureError.PersonCloses, (await _broker.ConcludeAsync("rm1-1", Billing, finished: true, T0.AddMinutes(3), Ct)).Error);
        Assert.Equal(ClosureError.PersonCloses, (await _broker.ConcludeAsync("rm1-1", Orders, finished: true, T0.AddMinutes(4), Ct)).Error);

        var facts = await FactsAsync(roadmap);
        Assert.Null(facts.ClosedBy);
        Assert.True(facts.ClosedExchanges == 0, $"two agents made a done item closed for good: {facts}");
    }
}
