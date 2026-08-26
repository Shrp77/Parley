using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The storage-level guards, exercised against the real schema.
/// </summary>
/// <remarks>
/// SPEC.md 9.3 argues that invariants belong in storage because application code can route
/// around them. That argument is worth exactly what the constraints are worth when a broken
/// broker reaches them, so these bypass ExchangeRepository entirely and write the rows such a
/// broker would write. Payloads are built by SQLite's json_object so that no JSON quoting is
/// nested inside a C# string, which is otherwise where the mistakes go.
/// </remarks>
public sealed class AdmissionGuardTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string Consequences =
        "json_object('consequences','If yes I drop the column; if no I add a snapshot field.')";

    private readonly SqliteConnection _connection;

    public AdmissionGuardTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Execute("INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders','test','full','offline'),('billing','test','full','offline');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InsertExchange(string question, string state = "OPEN", int cap = 5) => Execute(
        "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
      + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
      + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
      + "('billing' || char(31) || 'orders','orders','billing','billing','" + state + "',0,3,"
      + cap + ",900000,14400000,10800000,'" + Stamp + "','" + Stamp + "','" + question + "');");

    private void InsertMessage(string type, string from, string to, string payloadSql) => Execute(
        "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
      + "payload_json, content_hash, created_utc) SELECT id, '" + type + "', '" + from + "', '"
      + to + "', 'claude-opus-5', " + payloadSql + ", '" + Hash + "', '" + Stamp
      + "' FROM exchanges LIMIT 1;");

    private void InsertExchangeInConversation(string question, string conversation) => Execute(
        "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
      + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
      + "grace_period_ms, opened_utc, expires_utc, question_hash, conversation_id) VALUES "
      + "('billing' || char(31) || 'orders','orders','billing','billing','OPEN',0,3,5,"
      + "900000,14400000,10800000,'" + Stamp + "','" + Stamp + "','" + question + "','"
      + conversation + "');");

    [Fact]
    public void A_closed_conversation_cannot_be_reopened_by_a_new_exchange()
    {
        // SPEC.md 5.0: a closed conversation stays closed. Closure is by consensus, so a single
        // agent naming a concluded identifier would defeat the poll, the decline veto and the
        // silence rule at once. The broker refuses this with a stated reason; this is the
        // backstop for every path that is not the broker.
        Execute("INSERT INTO conversations (id, state, opened_utc, closed_utc) VALUES "
              + "('pricing','CLOSED','" + Stamp + "','" + Stamp + "');");

        var ex = Assert.Throws<SqliteException>(
            () => InsertExchangeInConversation("still-curious", "pricing"));

        Assert.Contains("conversation is closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vote_cannot_be_cast_once_the_conversation_has_closed()
    {
        // The repository refuses this in its WHERE clause. This is the backstop, and it exists
        // because SPEC.md 9.3 argues invariants belong in storage precisely where application
        // code can route around them -- and this one routes around nothing visible, since
        // closure is terminal and the vote changes no state. It only corrupts the log.
        Execute("INSERT INTO conversations (id, state, opened_utc, closed_utc) VALUES "
              + "('pricing','CLOSED','" + Stamp + "','" + Stamp + "');");
        Execute("INSERT INTO conversation_participants "
              + "(conversation_id, agent, joined_utc, polled_utc) VALUES "
              + "('pricing','billing','" + Stamp + "','" + Stamp + "');");

        var ex = Assert.Throws<SqliteException>(() => Execute(
            "UPDATE conversation_participants SET vote = 'not_done', voted_utc = '" + Stamp
          + "' WHERE conversation_id = 'pricing' AND agent = 'billing';"));

        Assert.Contains("conversation is closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vote_is_accepted_while_the_poll_is_running()
    {
        // The other half. A trigger that refused every vote would pass the test above while
        // making the whole mechanism inoperable, which is the failure mode a guard test exists
        // to rule out.
        Execute("INSERT INTO conversations (id, state, opened_utc) VALUES "
              + "('pricing','CLOSING','" + Stamp + "');");
        Execute("INSERT INTO conversation_participants "
              + "(conversation_id, agent, joined_utc, polled_utc) VALUES "
              + "('pricing','billing','" + Stamp + "','" + Stamp + "');");

        Execute("UPDATE conversation_participants SET vote = 'not_done', voted_utc = '" + Stamp
              + "' WHERE conversation_id = 'pricing' AND agent = 'billing';");
    }

    [Fact]
    public void An_open_conversation_still_accepts_exchanges()
    {
        // The other half, and the one that would fail if the trigger matched on the column
        // rather than on the state: a discussion in progress is exactly what conversations are
        // for, and a guard that refused those would be worse than none.
        Execute("INSERT INTO conversations (id, state, opened_utc) VALUES "
              + "('pricing','OPEN','" + Stamp + "');");

        InsertExchangeInConversation("still-curious", "pricing");
    }

    [Fact]
    public void The_cap_is_enforced_on_insert_even_when_the_broker_does_not_check()
    {
        for (var i = 0; i < 3; i++)
        {
            InsertExchange("question-" + i, cap: 3);
        }

        var ex = Assert.Throws<SqliteException>(() => InsertExchange("one-too-many", cap: 3));

        Assert.Contains("open_pair_cap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_is_enforced_on_resume_as_well()
    {
        // The failure that made two triggers necessary rather than one. Resuming an orphan
        // reaches OPEN by UPDATE, which a BEFORE INSERT trigger never sees, so a pair already
        // at its cap accepted the resume and went over.
        for (var i = 0; i < 3; i++)
        {
            InsertExchange("question-" + i, cap: 3);
        }

        InsertExchange("orphaned-one", state: "ORPHANED", cap: 3);

        var ex = Assert.Throws<SqliteException>(() => Execute(
            "UPDATE exchanges SET state = 'OPEN' WHERE question_hash = 'orphaned-one';"));

        Assert.Contains("open_pair_cap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ghost_cannot_be_recorded_on_an_exchange_that_was_answered()
    {
        // The forgery SPEC.md 15's health signals are worth committing. An agent reporting its
        // own answered exchanges as abandoned makes the infrastructure look broken, and by the
        // time anyone looks the messages proving otherwise may have been pruned.
        InsertExchange("answered-question");
        InsertMessage("answer", "billing", "orders", "json_object('text','yes')");
        Execute("UPDATE exchanges SET state = 'ORPHANED' WHERE question_hash = 'answered-question';");

        var ex = Assert.Throws<SqliteException>(() => Execute(
            "UPDATE exchanges SET state = 'CLOSED', closed_utc = '" + Stamp
          + "', resolution = 'ghosted' WHERE question_hash = 'answered-question';"));

        Assert.Contains("ghosted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ghost_is_permitted_when_nobody_answered()
    {
        InsertExchange("abandoned-question");

        Execute("UPDATE exchanges SET state = 'ORPHANED' WHERE question_hash = 'abandoned-question';");
        Execute("UPDATE exchanges SET state = 'CLOSED', closed_utc = '" + Stamp
              + "', resolution = 'ghosted' WHERE question_hash = 'abandoned-question';");
    }

    [Fact]
    public void A_referral_naming_an_unregistered_agent_is_refused()
    {
        // The only part of rule 3 the storage layer can hold. Prose cannot be checked, but a
        // referral is a name, and a name can be looked up.
        InsertExchange("q");

        var ex = Assert.Throws<SqliteException>(() => InsertMessage(
            "answer", "billing", "orders",
            "json_object('resolutions', json_array(json_object("
          + "'redirect_to', json_array('legacy-svc'))))"));

        Assert.Contains("redirect_to", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_referral_naming_its_own_sender_is_refused()
    {
        // out_of_scope says the boundary is not mine; naming yourself says it is.
        InsertExchange("q");

        var ex = Assert.Throws<SqliteException>(() => InsertMessage(
            "answer", "billing", "orders",
            "json_object('resolutions', json_array(json_object("
          + "'redirect_to', json_array('billing'))))"));

        Assert.Contains("sender", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_referral_to_a_registered_peer_is_admitted()
    {
        // Referring back to the asker is legitimate: that is your call, not mine.
        InsertExchange("q");

        InsertMessage("answer", "billing", "orders",
            "json_object('resolutions', json_array(json_object("
          + "'redirect_to', json_array('orders'))))");
    }

    [Fact]
    public void A_question_that_states_no_consequences_is_refused()
    {
        InsertExchange("q");

        var ex = Assert.Throws<SqliteException>(() => InsertMessage(
            "question", "orders", "billing", "json_object('intent','pricing')"));

        Assert.Contains("CHECK", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gate_naming_a_later_sub_question_is_refused()
    {
        // Requiring a gate to name an earlier sub-question makes the dependency graph a DAG by
        // construction, so a cycle cannot be expressed rather than being detected and rejected.
        InsertExchange("q");

        var ex = Assert.Throws<SqliteException>(() => InsertMessage(
            "question", "orders", "billing",
            "json_patch(" + Consequences + ", json_object('sub_questions', json_array("
          + "json_object('id','q1','gate',json_object('on','q2')), json_object('id','q2'))))"));

        Assert.Contains("earlier", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_naming_an_earlier_sub_question_is_admitted()
    {
        InsertExchange("q");

        InsertMessage("question", "orders", "billing",
            "json_patch(" + Consequences + ", json_object('sub_questions', json_array("
          + "json_object('id','q1'), json_object('id','q2','gate',json_object('on','q1')))))");
    }
}
