using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a decision on the proposal a surface shows is never a decision on another agent's, after a purge (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <b>Found by review, round 35.</b> A proposal's id was the highest plus one, so purging the agent whose proposal held
/// the highest gave the next proposal that id, and a person declining or confirming what was on screen decided another
/// agent's plan, crediting it with the first one's words. Migration 002 makes the id one that is never used twice.
/// </remarks>
public sealed class APurgedProposalsIdGoesToNoOtherTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;

    public APurgedProposalsIdGoesToNoOtherTests()
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
        _ = agents.EnsurePersonAsync(project).GetAwaiter().GetResult();

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.ServeFrom(T0);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_proposal_open_on_a_surface_is_not_another_agents_after_a_purge()
    {
        var first = await _broker.ProposeRoadmapAsync(
            Orders,
            new RoadmapContent("Orders plan", [new RoadmapItemDraft(null, "Ship the orders thing", null, [])]),
            null, null, T0, Ct);

        // The person opens orders' proposal on a surface: what they see, and the identifier the surface holds.
        var shown = (await _console.GetProposalAsync(first.Id!.Value, Ct))!;

        // Meanwhile orders is purged (its proposals cascade), and billing proposes.
        _ = await _console.PurgeAsync(Orders, Ct);

        var second = await _broker.ProposeRoadmapAsync(
            Billing,
            new RoadmapContent("Billing plan", [new RoadmapItemDraft(null, "Do the billing thing", null, [])]),
            null, null, T0.AddMinutes(1), Ct);

        // The person declines what they were shown, by the identifier the surface holds.
        var declined = await _console.DeclineProposalAsync(shown.Id, T0.AddMinutes(2), Ct);

        var billings = await _console.GetProposalAsync(second.Id!.Value, Ct);

        Assert.True(
            !declined.Done,
            $"the person declined orders' proposal '{shown.Content.Title}' (id {shown.Id}); billing's '{billings?.Content.Title}' "
            + $"(id {second.Id}) was the one declined, and billing is told so: state {billings?.State}");
    }

    [Fact]
    public async Task A_confirmation_of_what_was_shown_does_not_confirm_another_agents_proposal_after_a_purge()
    {
        var first = await _broker.ProposeRoadmapAsync(
            Orders,
            new RoadmapContent("Orders plan", [new RoadmapItemDraft(null, "Ship the orders thing", null, [])]),
            null, null, T0, Ct);
        var shown = (await _console.GetProposalAsync(first.Id!.Value, Ct))!;

        _ = await _console.PurgeAsync(Orders, Ct);

        var second = await _broker.ProposeRoadmapAsync(
            Billing,
            new RoadmapContent("Billing plan", [new RoadmapItemDraft(null, "Do the billing thing", null, [])]),
            null, null, T0.AddMinutes(1), Ct);

        var confirmed = await _console.ConfirmProposalAsync(shown.Id, shown.Content, null, T0.AddMinutes(2), Ct);
        var roadmap = confirmed.RoadmapId is { } id ? await _console.GetRoadmapAsync(id, Ct) : null;
        var billings = await _console.GetProposalAsync(second.Id!.Value, Ct);

        Assert.True(
            !confirmed.Done,
            $"confirming orders' '{shown.Content.Title}' (id {shown.Id}) confirmed billing's proposal (id {second.Id}, now {billings?.State}); "
            + $"the roadmap '{roadmap?.Title}' records proposer {roadmap?.Proposer}, origin {roadmap?.Origin}");
    }
}
