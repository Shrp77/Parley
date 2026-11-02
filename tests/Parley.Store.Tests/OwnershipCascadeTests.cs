using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// That destroying a conversation, or purging an agent, is not stopped by an ownership line agreed in it (research
/// finding 14).
/// </summary>
/// <remarks>
/// <para>
/// <b>A line's link to its conversation must not block a destroy.</b> <c>agent_ownership_accepted.conversation_id</c>
/// referencing its conversation with no action on delete would fail, because a destroy runs with foreign keys deferred to commit —
/// so destroying the conversation a line was agreed in would fail at the commit and roll back the whole destroy.
/// Every agent has a line, each agreed in its own conversation.
/// </para>
/// <para>
/// <b>The line stays and loses only its link.</b> The line is what peers route by, and the conversation is
/// evidence of how it was reached; destroying the evidence is the person's decision and must not take the line
/// with it.
/// </para>
/// </remarks>
[Integration]
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
