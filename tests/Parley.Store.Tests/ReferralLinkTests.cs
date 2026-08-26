using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// Referrals outliving the messages that carried them.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §15 wants the <c>out_of_scope</c> rate per pair and calls it the signal that the
/// ownership map is wrong somewhere. That was unmeasurable past the retention window, because
/// <c>redirect_to</c> lived only in <c>messages.payload_json</c> and §9.4 prunes by deleting the
/// row. The table these probe exists to survive that, and the test that matters most here is the
/// one that prunes and then looks.
/// </para>
/// <para>
/// Written as attempts to defeat the recorder rather than as a reading of it. A trigger that
/// silently matches nothing is the specific way this repository has been burned before: every
/// check downstream passes, and passes by vacuity.
/// </para>
/// </remarks>
public sealed class ReferralLinkTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;
    private int _hashes;

    public ReferralLinkTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        Execute("INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders','test','full','offline'),('billing','test','full','offline'),"
              + "('inventory','test','full','offline'),('pricing','test','full','offline');");

        Execute(
            "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
          + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
          + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
          + "('billing' || char(31) || 'orders','orders','billing','billing','OPEN',0,3,5,"
          + "900000,14400000,86400000,'" + Stamp + "','" + Stamp + "','h');");
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Writes an answer, returning its message id.</summary>
    private long Answer(string payload, string from = "billing", string to = "orders")
    {
        var hash = (++_hashes).ToString("D64", CultureInfo.InvariantCulture);

        Execute(
            "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
          + "payload_json, content_hash, created_utc) VALUES (1,'answer','" + from + "','" + to
          + "','some-model-9','" + payload + "','" + hash + "','" + Stamp + "');");

        return Scalar("SELECT max(id) FROM messages;");
    }

    private static string Payload(string resolutions) => "{\"resolutions\":[" + resolutions + "]}";

    private static string Referral(string id, string redirectTo) =>
        "{\"id\":\"" + id + "\",\"resolution\":\"out_of_scope\",\"text\":\"not mine\","
      + "\"redirect_to\":" + redirectTo + "}";

    private static string Answered(string id) =>
        "{\"id\":\"" + id + "\",\"resolution\":\"answered\",\"text\":\"yes\"}";

    private static string Names(params string[] names) =>
        "[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]";

    [Fact]
    public void A_list_of_two_owners_records_two_referrals()
    {
        // The shape SPEC.md §5.3 documents and the tool could not previously send: a sub-question
        // that turns out to span two boundaries, with an answerer that can name both.
        Answer(Payload(Referral("q1", Names("inventory", "pricing"))));

        Assert.Equal(2, Scalar("SELECT count(*) FROM message_referrals;"));
        Assert.Equal(1, Scalar(
            "SELECT count(*) FROM message_referrals WHERE redirected_to = 'inventory';"));
        Assert.Equal(1, Scalar(
            "SELECT count(*) FROM message_referrals WHERE redirected_to = 'pricing';"));
    }

    [Fact]
    public void A_bare_name_records_one()
    {
        // The other shape §5.3 admits, and the only one the tool emitted before this change.
        // Both must work or the recorder would be correct only for new clients.
        Answer(Payload(Referral("q1", "\"inventory\"")));

        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void The_sub_question_that_was_referred_is_recorded_and_not_the_one_that_was_answered()
    {
        // Why this is per referral rather than a per-pair rollup. A question that splits across a
        // boundary refers one part and answers another, and a counter cannot say which part.
        Answer(Payload(Answered("q1") + "," + Referral("q2", Names("inventory"))));

        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));
        Assert.Equal("q2", ReadText("SELECT sub_question FROM message_referrals;"));
    }

    [Fact]
    public void An_answer_that_refers_nobody_records_nothing()
    {
        Answer(Payload(Answered("q1") + "," + Answered("q2")));

        Assert.Equal(0, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void A_clarify_records_nothing_even_if_its_text_mentions_the_key()
    {
        // The recorder reads $.resolutions[*].redirect_to, so a message with no resolutions at
        // all contributes nothing regardless of what its prose contains. Probed because the
        // neighbouring validator trigger scans the whole document, and the two having different
        // reach is deliberate rather than accidental.
        Execute(
            "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
          + "payload_json, content_hash, created_utc) VALUES (1,'clarify','billing','orders',"
          + "'some-model-9','{\"text\":\"you wrote redirect_to inventory, did you mean it?\"}','"
          + (++_hashes).ToString("D64", CultureInfo.InvariantCulture) + "','" + Stamp + "');");

        Assert.Equal(0, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void The_same_name_twice_in_one_list_is_one_referral()
    {
        // OR IGNORE rather than a failed insert. A repeated name is a sloppy payload, not a
        // reason to refuse an answer somebody is waiting on.
        Answer(Payload(Referral("q1", Names("inventory", "inventory"))));

        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void A_referral_survives_the_message_being_pruned()
    {
        // The entire reason this table exists. §9.4 prunes by DELETE FROM messages, and the
        // referral used to go with the row — so §15's out_of_scope rate could never see further
        // back than the retention window.
        var id = Answer(Payload(Referral("q1", Names("inventory"))));

        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));

        Execute("DELETE FROM messages WHERE id = " + id.ToString(CultureInfo.InvariantCulture) + ";");

        Assert.Equal(0, Scalar("SELECT count(*) FROM messages;"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void Deleting_the_message_is_not_refused_by_the_referral()
    {
        // The other half of the same design, and the failure mode a foreign key would have
        // introduced: a reference from here to messages would make the first prune after the
        // first referral fail, and retention would simply stop working.
        Execute("PRAGMA foreign_keys = ON;");
        var id = Answer(Payload(Referral("q1", Names("inventory"))));

        Execute("DELETE FROM messages WHERE id = " + id.ToString(CultureInfo.InvariantCulture) + ";");

        Assert.Equal(1, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void A_self_referral_never_reaches_the_table()
    {
        // Refused on the message by tr_referral_targets_exist, so the CHECK here is never the
        // thing that fires. Probed anyway: the two guards protect different things, and if the
        // message-level one were ever relaxed this is what stops the record itself carrying a
        // contradiction.
        Assert.Throws<SqliteException>(
            () => Answer(Payload(Referral("q1", Names("inventory", "billing")))));

        Assert.Equal(0, Scalar("SELECT count(*) FROM message_referrals;"));
    }

    [Fact]
    public void Referring_the_asker_back_to_itself_is_refused()
    {
        // "Not mine, ask yourself" is not a referral. Nothing at the message level catches this
        // one — tr_referral_targets_exist checks the sender and the roster, and 'orders' is a
        // registered agent that is not the sender — so this constraint is the only thing that
        // does, and it fires on a real insert rather than in principle.
        Assert.Throws<SqliteException>(
            () => Answer(Payload(Referral("q1", Names("orders")))));
    }

    private string ReadText(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
