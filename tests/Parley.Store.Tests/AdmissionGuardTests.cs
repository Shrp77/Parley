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
[Integration]
public sealed class AdmissionGuardTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

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

    private string Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

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
    public void A_request_with_no_ask_is_refused()
    {
        // The whole of a Request's shape (§5.1).
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test", "json_object('text','x')"),
            "carries an ask");
    }

    [Fact]
    public void An_empty_ask_is_refused_and_so_is_one_that_is_only_spaces()
    {
        // A message with nothing in it is a turn spent on nothing at the other end.
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test", "json_object('ask','')"),
            "carries an ask");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test", "json_object('ask','   ')"),
            "carries an ask");
    }

    [Theory]
    // Tab, line feed and carriage return: ASCII, and all three admitted by a bare trim().
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    // No-break space, ideographic space, and the narrow no-break space: blank on every screen, and the ones a
    // sender would have to go out of its way to write.
    [InlineData(160)]
    [InlineData(12288)]
    [InlineData(8239)]
    public void An_ask_of_whitespace_that_is_not_the_space_character_is_refused_too(int codePoint)
    {
        // SQL's one-argument trim() removes the space character and
        // nothing else, so a trigger built on it would admit each of these — while both senders in this
        // repository refuse them, because .NET's IsNullOrWhiteSpace does. §9.3 puts the rule in storage precisely
        // so a second implementation cannot route around it, and a guard weaker than the code above it is the
        // wrong way round: this is a Request that says nothing, arriving from an adapter that did not check.
        InsertExchange("q");

        Refused(
            () => InsertMessage(
                "request", "orders@test", "billing@test", $"json_object('ask', char({codePoint}))"),
            "carries an ask");
    }

    [Fact]
    public void A_zero_width_space_is_not_blank_and_is_admitted()
    {
        // The other side of the line, and the reason the set is spelled out rather than guessed at: U+200B is not
        // whitespace by the rule the senders apply, so storage refusing it would refuse what they had just sent.
        InsertExchange("q");

        InsertMessage("request", "orders@test", "billing@test", "json_object('ask', char(8203))");
    }

    [Fact]
    public void An_ask_that_is_not_text_is_refused()
    {
        // A number, an object or an array in the field would be a payload no reader could render as the ask.
        InsertExchange("q");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test", "json_object('ask',7)"),
            "carries an ask");

        Refused(
            () => InsertMessage("request", "orders@test", "billing@test",
                "json_object('ask', json_array('who owns it'))"),
            "carries an ask");
    }

    [Fact]
    public void An_ask_beside_fields_this_version_does_not_define_is_admitted_and_kept_whole()
    {
        // Storage bounds the ask and is otherwise blind to the document (§13): a sender that carries something
        // this version does not define is not refused for it, and what it wrote is what is stored.
        InsertExchange("q");

        InsertMessage("request", "orders@test", "billing@test",
            "json_object('ask','who owns the invoice total?', 'note','carried by some other adapter')");

        Assert.Contains(
            "carried by some other adapter",
            Scalar("SELECT payload_json FROM messages WHERE type = 'request';"),
            StringComparison.Ordinal);
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
