using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The rules <c>ownership_questions</c> holds in storage (SPEC.md §9.2, data-model.md).
/// </summary>
/// <remarks>
/// <b>"Never asked twice" is an index rather than a check in the loop that asks.</b> The loop runs on a sweep, a
/// broker can restart between reading and writing, and two brokers once raced on one store (0.25.6). A check the
/// asking code made would be right until any of those happened; the partial unique index is right regardless.
/// Each case here was watched to fail with its rule removed.
/// </remarks>
public sealed class OwnershipQuestionTests : IDisposable
{
    private readonly FacilitatorStore _store = new();

    public void Dispose() => _store.Dispose();

    private long Asked(string conversation = "owns-orders-1", string agent = "orders@test") =>
        _store.Asked(conversation, FacilitatorStore.Facilitator, agent);

    [Fact]
    public void A_second_live_question_for_one_agent_is_refused()
    {
        _store.Question("orders@test", "owns-orders-1", Asked());
        var second = Asked("owns-orders-2");

        Assert.Throws<SqliteException>(() => _store.Question("orders@test", "owns-orders-2", second));
    }

    [Fact]
    public void A_settled_question_does_not_stop_a_person_asking_again()
    {
        // History is kept so "asked again" does not erase what was said; only live questions are unique.
        _store.Question("orders@test", "owns-orders-1", Asked());
        _store.Execute($"""
            UPDATE ownership_questions
               SET state = 'discarded', answer = 'orders', flattened = 'orders', settled_utc = '{FacilitatorStore.Stamp}';
            """);

        _store.Question("orders@test", "owns-orders-2", Asked("owns-orders-2"));

        Assert.Equal(2L, _store.Count("SELECT count(*) FROM ownership_questions WHERE agent = 'orders@test';"));
    }

    [Fact]
    public void Waiting_names_why()
    {
        _store.Question("orders@test", "owns-orders-1", Asked());

        Assert.Throws<SqliteException>(() => _store.Execute(
            "UPDATE ownership_questions SET state = 'waiting', answer = 'orders', flattened = 'orders';"));
    }

    [Fact]
    public void Accepted_needs_an_accepted_line()
    {
        _store.Question("orders@test", "owns-orders-1", Asked());

        Assert.Throws<SqliteException>(() => _store.Execute($"""
            UPDATE ownership_questions
               SET state = 'accepted', answer = 'orders', flattened = 'orders', settled_utc = '{FacilitatorStore.Stamp}';
            """));
    }

    [Fact]
    public void A_settled_question_stays_settled()
    {
        _store.Question("orders@test", "owns-orders-1", Asked());
        _store.Execute($"""
            UPDATE ownership_questions
               SET state = 'discarded', answer = 'orders', flattened = 'orders', settled_utc = '{FacilitatorStore.Stamp}';
            """);

        // Reopening it would make a discarded answer live again with nobody having asked.
        Assert.Throws<SqliteException>(() => _store.Execute(
            "UPDATE ownership_questions SET state = 'waiting', waiting_because = 'reopened', settled_utc = NULL;"));
    }

    [Fact]
    public void Only_the_facilitators_own_request_is_a_question()
    {
        // Research finding 11: an agent can open an `owns-` conversation itself. Recognising a question by the
        // conversation's name would let it answer itself and have the answer accepted automatically.
        var own = _store.Asked("owns-orders-1", "billing@test", "orders@test");

        Assert.Throws<SqliteException>(() => _store.Question("orders@test", "owns-orders-1", own));

        // The control, without which the refusal above passes against a store with no table at all.
        _store.Question("orders@test", "owns-orders-2", Asked("owns-orders-2"));
    }

    [Fact]
    public void Neither_a_seat_nor_the_facilitator_is_asked()
    {
        var seat = _store.Asked("owns-arvind-1", FacilitatorStore.Facilitator, "arvind@test");

        Assert.Throws<SqliteException>(() => _store.Question("arvind@test", "owns-arvind-1", seat));

        // The control: an agent is.
        _store.Question("orders@test", "owns-orders-1", Asked());
    }

    [Fact]
    public void A_question_starts_asked()
    {
        var exchange = Asked();

        Assert.Throws<SqliteException>(() => _store.Execute($"""
            INSERT INTO ownership_questions (agent, conversation_id, exchange_id, state, answer, flattened, asked_utc)
            VALUES ('orders@test', 'owns-orders-1', {exchange}, 'answered', 'orders', 'orders', '{FacilitatorStore.Stamp}');
            """));

        // The control: the same row in its first state is admitted.
        _store.Question("orders@test", "owns-orders-1", exchange);
    }

    [Fact]
    public void An_automatic_line_names_its_discussion_and_its_length()
    {
        Assert.Throws<SqliteException>(() => _store.Execute($"""
            INSERT INTO agent_ownership_accepted (agent, statement, source, proposed_length, conversation_id, accepted_utc)
            VALUES ('orders@test', 'orders', 'automatic', 6, NULL, '{FacilitatorStore.Stamp}');
            """));

        Asked();
        Assert.Throws<SqliteException>(() => _store.Execute($"""
            INSERT INTO agent_ownership_accepted (agent, statement, source, proposed_length, conversation_id, accepted_utc)
            VALUES ('orders@test', 'orders', 'automatic', NULL, 'owns-orders-1', '{FacilitatorStore.Stamp}');
            """));

        // The control: with both, it is admitted. Before 022 `automatic` was refused outright.
        _store.Accepted("orders@test", "owns-orders-1", "automatic");
    }

    [Fact]
    public void The_refresh_is_sent_at_most_once_per_agent()
    {
        _store.Question("orders@test", "owns-orders-1", Asked());
        _store.Accepted("orders@test", "owns-orders-1", "automatic");
        _store.Execute($"""
            UPDATE ownership_questions
               SET state = 'accepted', answer = 'orders', flattened = 'orders', settled_utc = '{FacilitatorStore.Stamp}',
                   restart_sent = 1;
            """);

        _store.Question("orders@test", "owns-orders-2", Asked("owns-orders-2"));
        _store.Execute($"""
            UPDATE ownership_questions
               SET state = 'accepted', answer = 'orders', flattened = 'orders', settled_utc = '{FacilitatorStore.Stamp}'
             WHERE conversation_id = 'owns-orders-2';
            """);

        Assert.Throws<SqliteException>(() => _store.Execute(
            "UPDATE ownership_questions SET restart_sent = 1 WHERE conversation_id = 'owns-orders-2';"));
    }
}
