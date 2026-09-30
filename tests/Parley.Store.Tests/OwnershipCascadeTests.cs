using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That destroying a conversation, or purging an agent, is not stopped by an ownership line agreed in it (research
/// finding 14).
/// </summary>
/// <remarks>
/// <para>
/// <b>A defect 020 shipped and 022 would have made ordinary.</b> <c>agent_ownership_accepted.conversation_id</c>
/// referenced its conversation with no action on delete, and a destroy runs with foreign keys deferred to commit —
/// so destroying the conversation a line was agreed in failed at the commit and rolled back the whole destroy.
/// Under 020 that needed a person to have accepted a line; under 022 every agent has one, each agreed in its own
/// conversation.
/// </para>
/// <para>
/// <b>The line stays and loses only its link.</b> The line is what peers route by, and the conversation is
/// evidence of how it was reached; destroying the evidence is the person's decision and must not take the line
/// with it. Watched to fail against the schema before 022.
/// </para>
/// </remarks>
public sealed class OwnershipCascadeTests : IDisposable
{
    private readonly FacilitatorStore _store = new();

    public void Dispose() => _store.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Destroying_the_conversation_a_line_was_agreed_in_keeps_the_line()
    {
        _store.Asked("owns-orders-1", FacilitatorStore.Facilitator, "orders@test");
        _store.Accepted("orders@test", "owns-orders-1");

        var outcome = await new ConversationRepository(_store.Connection)
            .DestroyAsync("owns-orders-1", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.Gone, outcome.Verdict);
        Assert.Equal(1L, _store.Count(
            "SELECT count(*) FROM agent_ownership_accepted WHERE agent = 'orders@test' AND conversation_id IS NULL;"));
    }

    [Fact]
    public async Task Destroying_a_questions_conversation_takes_the_question_with_it()
    {
        var exchange = _store.Asked("owns-orders-1", FacilitatorStore.Facilitator, "orders@test");
        _store.Question("orders@test", "owns-orders-1", exchange);

        var outcome = await new ConversationRepository(_store.Connection)
            .DestroyAsync("owns-orders-1", Now, cancellationToken: Ct);

        Assert.Equal(DestroyVerdict.Gone, outcome.Verdict);
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM ownership_questions;"));
    }

    [Fact]
    public async Task Purging_an_agent_that_was_asked_succeeds()
    {
        var exchange = _store.Asked("owns-orders-1", FacilitatorStore.Facilitator, "orders@test");
        _store.Question("orders@test", "owns-orders-1", exchange);
        _store.Accepted("orders@test", "owns-orders-1", "automatic");

        await new AgentRepository(_store.Connection).PurgeAsync(AgentName.Create("orders@test"), Now, cancellationToken: Ct);

        Assert.Equal(0L, _store.Count("SELECT count(*) FROM agents WHERE name = 'orders@test';"));
        Assert.Equal(0L, _store.Count("SELECT count(*) FROM ownership_questions;"));

        // The facilitator was a participant in that conversation and is not purged with it.
        Assert.Equal(1L, _store.Count($"SELECT count(*) FROM agents WHERE name = '{FacilitatorStore.Facilitator}';"));
    }
}
