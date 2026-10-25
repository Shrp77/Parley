using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That an item sent to two agents reads blocked while either's latest part ended blocked, whichever finished last.
/// </summary>
/// <remarks>
/// <b>Found by review, round 20.</b> Blocked was read from the conversation's latest closed exchange alone, so billing
/// ending blocked and then orders completing read as done, and the item that needs it became next. It is read per
/// agent now, and asking the blocked agent again and completing clears it.
/// </remarks>
public sealed class AnItemSentToTwoAgentsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;

    public AnItemSentToTwoAgentsTests()
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

    [Fact]
    public async Task An_item_one_of_whose_two_agents_ended_blocked_does_not_read_as_done()
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
        var roadmapId = (await _console.ConfirmProposalAsync(proposed.Id!.Value, content, null, T0, Ct)).RoadmapId!.Value;

        // The person starts item 1 with both agents ticked: one Request each, in the item's conversation.
        var toBilling = await _broker.RequestAsync(_person, Billing, Requests.Model, Requests.Request("Add CSV export"), T0, "rm1-1", Ct);
        var toOrders = await _broker.RequestAsync(_person, Orders, Requests.Model, Requests.Request("Add CSV export"), T0, "rm1-1", Ct);
        Assert.True(toBilling.Succeeded, toBilling.Rejection.ToString());
        Assert.True(toOrders.Succeeded, toOrders.Rejection.ToString());

        // Billing's part ends blocked first; orders' part completes after it.
        Assert.True((await _broker.ResultAsync(
            toBilling.Exchange!.Id, Billing, Requests.Model,
            Requests.Result((PayloadShape.WholeRequest, "blocked:missing_information")), T0.AddMinutes(1), Ct)).Succeeded);
        Assert.True((await _broker.ResultAsync(
            toOrders.Exchange!.Id, Orders, Requests.Model, Requests.Result(), T0.AddMinutes(2), Ct)).Succeeded);

        var facts = (await _console.GetRoadmapAsync(roadmapId, Ct))!.Items.Single(i => i.Number == 1).Facts!;

        // ItemStates: no open exchange, BlockedExchanges 0, CompleteExchanges > 0 and ClosedExchanges 0 is Done, and
        // item 2, which needs it, becomes the roadmap's next item.
        Assert.False(
            facts is { OpenExchanges: 0, BlockedExchanges: 0, CompleteExchanges: > 0, ClosedExchanges: 0 },
            $"reads as done though billing's part ended blocked: {facts}");

        // Asked again, billing completes: no agent's latest part is blocked now.
        var again = await _broker.RequestAsync(
            _person, Billing, Requests.Model, Requests.Request("Add CSV export, with the column named"), T0.AddMinutes(3), "rm1-1", Ct);
        Assert.True(again.Succeeded, again.Rejection.ToString());
        Assert.True((await _broker.ResultAsync(
            again.Exchange!.Id, Billing, Requests.Model, Requests.Result(), T0.AddMinutes(4), Ct)).Succeeded);

        var cleared = (await _console.GetRoadmapAsync(roadmapId, Ct))!.Items.Single(i => i.Number == 1).Facts!;
        Assert.Equal(0, cleared.BlockedExchanges);
    }
}
