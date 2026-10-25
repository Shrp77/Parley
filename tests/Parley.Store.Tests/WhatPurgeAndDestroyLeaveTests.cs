using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// What a purge or a destroy leaves of a roadmap item's conversation, and what it tells the operator went.
/// </summary>
/// <remarks>
/// <b>Found by review, round 15.</b> A purge destroyed the conversations its agent took part in without recording them as
/// destroyed, as a destroy does, so a finished item read as never started and was offered as next; and a destroy let the
/// cascade take a conversation's ownership questions without naming them (SPEC.md §14: the operator is told per table).
/// </remarks>
public sealed class WhatPurgeAndDestroyLeaveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly FacilitatorStore _store = new();

    public void Dispose() => _store.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RoadmapRepository Repository => new(_store.Connection);

    private static AgentName Orders => AgentName.Create("orders@test");

    private static AgentName Billing => AgentName.Create("billing@test");

    [Fact]
    public async Task Destroying_a_done_items_conversation_reads_as_destroyed()
    {
        var id = await RoadmapAsync();
        StartedAndDone();

        await new ConversationRepository(_store.Connection).DestroyAsync("rm1-1", Now, cancellationToken: Ct);

        Assert.True((await Repository.GetAsync(id, Ct))!.Items[0].Facts!.Destroyed);
    }

    [Fact]
    public async Task Purging_the_agent_a_done_item_was_started_with_leaves_it_reading_destroyed_not_unstarted()
    {
        var id = await RoadmapAsync();
        StartedAndDone();
        Assert.Equal(1, (await Repository.GetAsync(id, Ct))!.Items[0].Facts!.CompleteExchanges);

        var outcome = await new AgentRepository(_store.Connection).PurgeAsync(Billing, Now, cancellationToken: Ct);
        Assert.Contains(outcome.Destroyed, toll => toll.Table == "conversations" || toll.Table == "messages");

        // SPEC.md §5.7: a destroyed item conversation reads closed, never not started. A purge destroys it too.
        var facts = (await Repository.GetAsync(id, Ct))!.Items[0].Facts!;
        Assert.False(facts.ConversationExists);
        Assert.True(facts.Destroyed, "the done item now reads as not started");
        Assert.Equal(
            "billing@test, human-user@test",
            (string)_store.Scalar("SELECT participants FROM destroyed_conversations WHERE id = 'rm1-1';")!);
    }

    [Fact]
    public async Task After_the_purge_the_reserved_name_still_refuses_an_agent()
    {
        await RoadmapAsync();
        StartedAndDone();
        await new AgentRepository(_store.Connection).PurgeAsync(Billing, Now, cancellationToken: Ct);

        Assert.Equal(1L, _store.Count("SELECT count(*) FROM roadmap_item_conversations WHERE conversation_id = 'rm1-1';"));

        _store.Execute("BEGIN;");
        var refused = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => _store.Asked("rm1-1", "orders@test", "parley-facilitator@test"));
        _store.Execute("ROLLBACK;");

        Assert.Contains("opened by the person", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destroying_a_questions_conversation_tells_the_operator_the_question_went()
    {
        var exchange = _store.Asked("owns-orders-1", FacilitatorStore.Facilitator, "orders@test");
        _store.Question("orders@test", "owns-orders-1", exchange);

        var outcome = await new ConversationRepository(_store.Connection)
            .DestroyAsync("owns-orders-1", Now, cancellationToken: Ct);

        Assert.Equal(0L, _store.Count("SELECT count(*) FROM ownership_questions;"));
        Assert.Contains(outcome.Destroyed, toll => toll is { Table: "ownership_questions", Rows: 1 });
    }

    private async Task<long> RoadmapAsync()
    {
        var (id, problem) = await Repository.ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent("Billing export", [new RoadmapItemDraft(null, "Add CSV export", Billing, [])]),
            Now,
            cancellationToken: Ct);
        Assert.Null(problem);

        var proposal = await Repository.GetProposalAsync(id!.Value, Ct);
        var change = await Repository.ConfirmAsync(id.Value, proposal!.Content, null, Now, cancellationToken: Ct);

        return change.RoadmapId!.Value;
    }

    /// <summary>The person starts rm1-1 with billing, and billing finishes it.</summary>
    private void StartedAndDone()
    {
        var exchange = _store.Asked("rm1-1", "human-user@test", "billing@test");

        _store.Execute($$"""
            INSERT INTO messages
                (exchange_id, type, from_agent, to_agent, responder_model, payload_json,
                 content_hash, conversation_id, created_utc)
            VALUES ({{exchange}}, 'result', 'billing@test', 'human-user@test', 'claude-opus-5',
                    '{"outcomes":[{"id":"q1","outcome":"done","text":"ok"}]}',
                    '{{Guid.NewGuid():N}}{{Guid.NewGuid():N}}', 'rm1-1', '{{FacilitatorStore.Stamp}}');
            UPDATE exchanges SET state = 'CLOSED', closed_utc = '{{FacilitatorStore.Stamp}}', outcome = 'complete'
             WHERE id = {{exchange}};
            """);
    }
}
