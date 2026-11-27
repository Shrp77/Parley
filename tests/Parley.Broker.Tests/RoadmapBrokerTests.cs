using Dapper;
using Microsoft.Data.Sqlite;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Broker.Tests;

/// <summary>
/// That a roadmap is the person's from the broker's side: an agent proposes and is told what became of it, a
/// person decides, and nothing an agent does can make a roadmap or capture an item's conversation (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// Driven through the two real types, <see cref="Broker"/> for the agent and <see cref="OperatorConsole"/> for the
/// person, in the order the protocol runs them. The refusals are the point: each was watched to fail first.
/// </remarks>
[Integration]
public sealed class RoadmapBrokerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    private readonly SqliteConnection _connection = ParleyDatabase.OpenInMemory();
    private readonly Broker _broker;
    private readonly OperatorConsole _console;
    private readonly AgentName _person;
    private readonly AgentName _parley;

    public RoadmapBrokerTests()
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

        _parley = agents.EnsureFacilitatorAsync(project).GetAwaiter().GetResult();
        _person = agents.EnsurePersonAsync(project).GetAwaiter().GetResult();

        _broker = new Broker(_connection, BrokerOptions.Default);
        _broker.ServeFrom(T0);
        _console = new OperatorConsole(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoadmapContent Plan(string title = "Billing export") => new(title,
    [
        new RoadmapItemDraft(null, "Add CSV export", Billing, []),
        new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
    ]);

    private async Task<long> ProposedAsync(AgentName by, string title = "Billing export", string? revises = null)
    {
        var outcome = await _broker.ProposeRoadmapAsync(by, Plan(title), null, revises, T0, Ct);

        Assert.Null(outcome.Problem);

        return outcome.Id!.Value;
    }

    [Fact]
    public async Task A_proposal_is_stored_and_makes_no_roadmap_and_no_exchange()
    {
        var id = await ProposedAsync(Orders);

        Assert.True(id > 0);
        Assert.Empty(await _console.ListRoadmapsAsync(Ct));
        Assert.Equal(0, await _connection.ExecuteScalarAsync<int>("SELECT count(*) FROM exchanges;"));
        Assert.Equal(0, await _connection.ExecuteScalarAsync<int>("SELECT count(*) FROM messages;"));

        var waiting = Assert.Single(await _console.ListProposalsAsync(ProposalState.Waiting, Ct));
        Assert.Equal(Orders, waiting.Proposer);
    }

    [Fact]
    public async Task The_person_and_Parley_are_refused_with_a_sentence()
    {
        var person = await _broker.ProposeRoadmapAsync(_person, Plan(), null, null, T0, Ct);
        var parley = await _broker.ProposeRoadmapAsync(_parley, Plan(), null, null, T0, Ct);

        Assert.Null(person.Id);
        Assert.Contains("Only an agent", person.Problem, StringComparison.Ordinal);
        Assert.Null(parley.Id);
        Assert.Empty(await _console.ListProposalsAsync(null, Ct));
    }

    [Fact]
    public async Task An_unregistered_caller_and_an_unstarted_broker_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _broker.ProposeRoadmapAsync(AgentName.Create("nobody@test"), Plan(), null, null, T0, Ct));

        using var other = ParleyDatabase.OpenInMemory();
        var unstarted = new Broker(other, BrokerOptions.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unstarted.ProposeRoadmapAsync(Orders, Plan(), null, null, T0, Ct));
    }

    [Fact]
    public async Task A_proposal_that_is_empty_or_breaks_a_line_comes_back_as_a_sentence_and_stores_nothing()
    {
        var empty = await _broker.ProposeRoadmapAsync(Orders, new RoadmapContent("Plan", []), null, null, T0, Ct);
        var broken = await _broker.ProposeRoadmapAsync(
            Orders, new RoadmapContent("Plan", [new RoadmapItemDraft(null, "one\ntwo", null, [])]), null, null, T0, Ct);

        Assert.NotNull(empty.Problem);
        Assert.NotNull(broken.Problem);
        Assert.Empty(await _console.ListProposalsAsync(null, Ct));
    }

    [Fact]
    public async Task What_became_of_a_proposal_reaches_its_proposer_in_its_inbox_once_and_only_its_proposer()
    {
        var id = await ProposedAsync(Orders, "My roadmap");
        var declined = await _console.DeclineProposalAsync(id, T0, Ct);

        Assert.True(declined.Done, declined.Problem);

        var other = await _broker.DrainInboxAsync(Billing, T0, cancellationToken: Ct);
        Assert.True(other.IsEmpty);

        var told = await _broker.DrainInboxAsync(Orders, T0, cancellationToken: Ct);
        var notice = Assert.Single(told.RoadmapNotices);
        Assert.False(told.IsEmpty);
        Assert.Equal("My roadmap", notice.Title);
        Assert.Equal(ProposalState.Declined, notice.Outcome);

        Assert.True((await _broker.DrainInboxAsync(Orders, T0, cancellationToken: Ct)).IsEmpty);
    }

    [Fact]
    public async Task A_notice_is_not_a_message_so_collecting_one_collects_no_message()
    {
        var id = await ProposedAsync(Orders);
        await _console.DeclineProposalAsync(id, T0, Ct);

        var told = await _broker.DrainInboxAsync(Orders, T0, cancellationToken: Ct);

        Assert.Empty(told.Messages);
        Assert.Empty(told.ClosurePolls);
    }

    [Fact]
    public async Task Confirming_makes_the_roadmap_and_tells_the_proposer_it_was_confirmed()
    {
        var id = await ProposedAsync(Orders);
        var content = (await _console.ListProposalsAsync(ProposalState.Waiting, Ct)).Single().Content;

        var change = await _console.ConfirmProposalAsync(id, content, null, T0, Ct);

        Assert.True(change.Done, change.Problem);
        Assert.Equal(RoadmapOrigin.Proposed, change.Origin);

        var roadmap = Assert.Single(await _console.ListRoadmapsAsync(Ct));
        Assert.Equal("Billing export", roadmap.Title);
        Assert.Equal(2, roadmap.LiveItems);
        Assert.Equal(0, roadmap.StartedItems);

        var told = await _broker.DrainInboxAsync(Orders, T0, cancellationToken: Ct);
        Assert.Equal(ProposalState.Confirmed, Assert.Single(told.RoadmapNotices).Outcome);
    }

    // ---- an item's conversation is the person's to open

    private async Task<long> ConfirmedAsync()
    {
        var id = await ProposedAsync(Orders);
        var content = (await _console.ListProposalsAsync(ProposalState.Waiting, Ct)).Single().Content;

        return (await _console.ConfirmProposalAsync(id, content, null, T0, Ct)).RoadmapId!.Value;
    }

    [Fact]
    public async Task The_person_starting_an_item_makes_the_conversation_and_the_export_names_the_item()
    {
        var roadmapId = await ConfirmedAsync();

        var started = await _broker.RequestAsync(
            _person, Billing, Requests.Model, Requests.Request("Add CSV export"), T0, "rm1-1", Ct);

        Assert.True(started.Succeeded, started.Rejection.ToString());

        var export = await _console.ExportAsync("rm1-1", Ct);
        Assert.NotNull(export.Item);
        Assert.Equal(roadmapId, export.Item.RoadmapId);
        Assert.Equal(1, export.Item.ItemNumber);

        var roadmap = await _console.GetRoadmapAsync(roadmapId, Ct);
        var facts = roadmap!.Items[0].Facts!;
        Assert.True(facts.ConversationExists);
        Assert.Equal(1, facts.OpenExchanges);
        Assert.Equal(1, Assert.Single(await _console.ListRoadmapsAsync(Ct)).StartedItems);
    }

    [Fact]
    public async Task An_agent_naming_the_reserved_conversation_first_is_not_admitted_and_nothing_is_written()
    {
        await ConfirmedAsync();

        // Assignment refuses a reserved name to an agent, before anything reaches storage, whose trigger backs it up
        // (held in the store's own tests). What matters is that the person's conversation was not captured.
        var outcome = await _broker.RequestAsync(
            Orders, Billing, Requests.Model, Requests.Request("take this item"), T0, "rm1-1", Ct);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RequestRejection.ConversationNotAssignable, outcome.Admission.Rejection);

        Assert.Equal(0, await _connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM messages WHERE conversation_id = 'rm1-1';"));
    }

    [Fact]
    public async Task An_ordinary_conversation_has_no_item_in_its_export()
    {
        await ConfirmedAsync();

        var started = await _broker.RequestAsync(
            Orders, Billing, Requests.Model, Requests.Request("ordinary work"), T0, null, Ct);

        Assert.True(started.Succeeded);
        Assert.Null((await _console.ExportAsync(started.Request!.ConversationId, Ct)).Item);
    }
}
