using Microsoft.Data.Sqlite;
using Parley.Core;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The storage-level guards, exercised against the real schema.
/// </summary>
/// <remarks>
/// SPEC.md §9.3 argues that invariants belong in storage because application code can route around them. That
/// argument is worth exactly what the constraints are worth when a broken broker reaches them, so these bypass
/// the repositories entirely and write the rows such a broker would write. Payloads are built by SQLite's
/// json_object so that no JSON quoting is nested inside a C# string, which is otherwise where the mistakes go.
/// </remarks>
public sealed class AdmissionGuardTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string DoneWhen = "json_object('done_when','the invoice total is stated as gross or net')";

    private readonly SqliteConnection _connection;
    private int _hashes;

    public AdmissionGuardTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        Work.Register(
            _connection, AgentName.Create("orders@test"), AgentName.Create("billing@test"), AgentName.Create("inventory@test"));
        Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('pricing','OPEN','{Stamp}');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InsertExchange(string hash, int cap = 5, string conversation = "pricing") => Execute(
        "INSERT INTO exchanges (pair_key, requester, receiver, owed_by, state, open_pair_cap, "
      + "uncollected_flag_ms, stalled_flag_ms, opened_utc, request_hash, conversation_id) VALUES "
      + "('billing@test' || char(31) || 'orders@test','orders@test','billing@test','billing@test','OPEN',"
      + cap + ",900000,14400000,'" + Stamp + "','" + hash + "','" + conversation + "');");

    private void InsertMessage(string type, string from, string to, string payloadSql, string responder = "claude-opus-5") => Execute(
        "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
      + "payload_json, content_hash, conversation_id, created_utc) SELECT id, '" + type + "', '" + from + "', '"
      + to + "', '" + responder + "', " + payloadSql + ", '"
      + (++_hashes).ToString("x64", System.Globalization.CultureInfo.InvariantCulture)
      + "', conversation_id, '" + Stamp + "' FROM exchanges ORDER BY id LIMIT 1;");

    private static string Items(string itemsSql) => "json_patch(" + DoneWhen + ", json_object('items', " + itemsSql + "))";

    private static string Outcomes(string outcomesSql) => "json_object('outcomes', " + outcomesSql + ")";

    private void Refused(Action write, string reason)
    {
        var ex = Assert.Throws<SqliteException>(write);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    // ---- conversations ----

    [Fact]
    public void A_closed_conversation_cannot_be_reopened_by_a_new_exchange()
    {
        // SPEC.md §5.0: a closed conversation stays closed. The broker refuses this with a stated reason; this
        // is the backstop for every path that is not the broker.
        Execute($"INSERT INTO conversations (id, state, opened_utc, closed_utc, closed_by) VALUES ('done','CLOSED','{Stamp}','{Stamp}','person');");

        Refused(() => InsertExchange("still-curious", conversation: "done"), "conversation is closed");
    }

    [Fact]
    public void An_open_conversation_still_accepts_exchanges()
    {
        // The other half: a guard that refused a conversation in progress would be worse than none.
        InsertExchange("still-curious");
    }

    [Fact]
    public void A_vote_cannot_be_cast_once_the_conversation_has_closed()
    {
        Execute($"INSERT INTO conversations (id, state, opened_utc, closed_utc, closed_by) VALUES ('done','CLOSED','{Stamp}','{Stamp}','consensus');");
        Execute($"INSERT INTO conversation_participants (conversation_id, agent, joined_utc, polled_utc) VALUES ('done','billing@test','{Stamp}','{Stamp}');");

        Refused(
            () => Execute($"UPDATE conversation_participants SET vote = 'not_done', voted_utc = '{Stamp}' WHERE conversation_id = 'done';"),
            "conversation is closed");
    }

    [Fact]
    public void A_vote_is_accepted_while_the_poll_is_running()
    {
        // A trigger that refused every vote would pass the test above while making consensus impossible.
        Execute($"INSERT INTO conversations (id, state, opened_utc) VALUES ('polling','CLOSING','{Stamp}');");
        Execute($"INSERT INTO conversation_participants (conversation_id, agent, joined_utc, polled_utc) VALUES ('polling','billing@test','{Stamp}','{Stamp}');");

        Execute($"UPDATE conversation_participants SET vote = 'not_done', voted_utc = '{Stamp}' WHERE conversation_id = 'polling';");
    }

    // ---- the pair cap ----

    [Fact]
    public void A_set_cap_is_enforced_on_insert_even_when_the_broker_does_not_check()
    {
        for (var i = 0; i < 3; i++)
        {
            InsertExchange("request-" + i, cap: 3);
        }

        Refused(() => InsertExchange("one-too-many", cap: 3), "open_pair_cap");
    }

    [Fact]
    public void The_same_request_cannot_be_open_twice()
    {
        InsertExchange("same");

        Refused(() => InsertExchange("same"), "UNIQUE");
    }

    // ---- the shape of a Request (rule 8.1) ----

    [Fact]
    public void A_request_that_does_not_say_what_done_looks_like_is_refused()
    {
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                "json_object('items', json_array(json_object('id','q1','kind','work','text','x')))"),
            "what done looks like");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                "json_object('done_when','soon', 'items', json_array(json_object('id','q1','kind','work','text','x')))"),
            "what done looks like");
    }

    [Fact]
    public void A_request_with_no_items_is_refused()
    {
        InsertExchange("q");

        Refused(() => InsertMessage("request", "orders@test", "billing@test", Items("json_array()")), "at least one item");
    }

    [Fact]
    public void An_item_that_is_neither_work_nor_a_question_is_refused()
    {
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                Items("json_array(json_object('id','q1','kind','notice','text','x'))")),
            "work or a question");
    }

    [Fact]
    public void Two_items_sharing_an_id_are_refused()
    {
        // A Result gives one outcome per id, so two items under one id could never both be reported on.
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                Items("json_array(json_object('id','q1','kind','work','text','x'), json_object('id','q1','kind','question','text','y'))")),
            "its own id");
    }

    [Fact]
    public void An_item_id_no_result_could_record_is_refused()
    {
        // Found by the review gate. A 65-character id was admitted, and every Result for it then failed
        // result_items' own bound: the receiver could never report on the work, since a Result must echo the
        // ids exactly, and only a person closing the conversation ended it. A number is refused for the same
        // reason — it is not an id a Result can name.
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                Items($"json_array(json_object('id','{new string('r', 65)}','kind','work','text','x'))")),
            "1 to 64 characters");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                Items("json_array(json_object('id',1,'kind','work','text','x'))")),
            "1 to 64 characters");

        InsertMessage("request", "orders@test", "billing@test",
            Items($"json_array(json_object('id','{new string('r', 64)}','kind','work','text','x'))"));
    }

    [Fact]
    public void A_gate_naming_a_later_item_is_refused()
    {
        // Requiring a gate to name an earlier item makes the dependencies a DAG by construction, so a cycle
        // cannot be expressed rather than being detected and rejected.
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                Items("json_array(json_object('id','q1','kind','work','text','x','gate',json_object('on','q2')), json_object('id','q2','kind','work','text','y'))")),
            "earlier item");
    }

    [Fact]
    public void A_gate_naming_an_earlier_item_is_admitted()
    {
        InsertExchange("q");

        InsertMessage("request", "orders@test", "billing@test",
            Items("json_array(json_object('id','q1','kind','question','text','x'), json_object('id','q2','kind','work','text','y','gate',json_object('on','q1')))"));
    }

    // ---- the shape of a Result (§5.3) ----

    [Fact]
    public void A_result_with_no_outcomes_is_refused()
    {
        InsertExchange("q");

        Refused(() => InsertMessage("result", "billing@test", "orders@test", Outcomes("json_array()")), "an outcome for each item");
    }

    [Fact]
    public void A_blocked_outcome_must_name_its_blocker_and_nothing_else_may()
    {
        // Blocked without a blocker is a decline with no reason, which principle 2.6 does not recognise; a
        // blocker on a done item is a contradiction a report would count twice.
        InsertExchange("q");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','blocked','text','no access'))")),
            "names its blocker");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','done','blocker','conflict','text','x'))")),
            "names its blocker");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','blocked','blocker','busy','text','x'))")),
            "names its blocker");
    }

    [Fact]
    public void An_unknown_outcome_is_refused()
    {
        InsertExchange("q");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','out_of_scope','text','x'))")),
            "an outcome is done");
    }

    [Fact]
    public void A_source_naming_an_unregistered_participant_is_refused()
    {
        // Prose cannot be checked, but a source is a name, and a name can be looked up.
        InsertExchange("q");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','answered','text','x','sources',json_array('legacy@test')))")),
            "not registered");
    }

    [Fact]
    public void A_result_naming_its_own_sender_as_a_source_is_refused()
    {
        InsertExchange("q");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','answered','text','x','sources',json_array('billing@test')))")),
            "its own sender");
    }

    [Fact]
    public void A_source_naming_a_registered_participant_is_admitted_and_recorded()
    {
        // Relying on the requester itself is legitimate: "you told me this earlier".
        InsertExchange("q");

        InsertMessage("result", "billing@test", "orders@test",
            Outcomes("json_array(json_object('id','q1','outcome','answered','text','x','sources',json_array('inventory@test','orders@test')))"));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM result_sources WHERE from_agent = 'billing@test';";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Only_the_broker_records_closed_and_the_broker_records_nothing_else()
    {
        // An agent writing `closed` would claim a person ended its own unfinished work; the broker writing
        // `done` would put words in an agent's mouth. Both are refused in storage, because both are forgeries
        // of the record §15 is computed from.
        InsertExchange("q");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','closed','text','x'))")),
            "only the broker");

        Refused(
            () => InsertMessage("result", "billing@test", "orders@test",
                Outcomes("json_array(json_object('id','q1','outcome','done','text','x'))"), responder: "parley:broker"),
            "only closed outcomes");

        InsertMessage("result", "billing@test", "orders@test",
            Outcomes("json_array(json_object('id','q1','outcome','closed','text','closed by the person'))"), responder: "parley:broker");
    }

    // ---- closing ----

    [Fact]
    public void An_exchange_cannot_close_on_an_outcome_nobody_sent()
    {
        InsertExchange("q");

        Refused(
            () => Execute($"UPDATE exchanges SET state = 'CLOSED', closed_utc = '{Stamp}', outcome = 'complete';"),
            "closes on a result");
    }

    [Fact]
    public void An_exchange_closed_by_the_broker_carries_the_brokers_closed_result()
    {
        InsertExchange("q");

        // The receiver's own Result does not license `closed`: that outcome says the person ended it.
        InsertMessage("result", "billing@test", "orders@test",
            Outcomes("json_array(json_object('id','q1','outcome','done','text','x'))"));

        Refused(
            () => Execute($"UPDATE exchanges SET state = 'CLOSED', closed_utc = '{Stamp}', outcome = 'closed';"),
            "broker's closed result");
    }

    [Fact]
    public void A_closed_exchange_stays_closed()
    {
        InsertExchange("q");
        InsertMessage("result", "billing@test", "orders@test",
            Outcomes("json_array(json_object('id','q1','outcome','done','text','x'))"));
        Execute($"UPDATE exchanges SET state = 'CLOSED', closed_utc = '{Stamp}', outcome = 'complete';");

        Refused(
            () => Execute("UPDATE exchanges SET state = 'OPEN', closed_utc = NULL, outcome = NULL;"),
            "stays closed");
    }
}
