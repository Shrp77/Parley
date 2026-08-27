using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The counter behind SPEC.md §17 question 1.
/// </summary>
/// <remarks>
/// <para>
/// Notify is deliberately unbounded, so this counter is the only thing that will ever say whether
/// that was right. It has to keep counting after the messages are gone, and it has to count
/// correctly — a rollup that drifts is worse than no rollup, because a number that only grows
/// gives nothing to notice a mistake by.
/// </para>
/// <para>
/// Probed as attempts to make it lie: wrong type counted, size measured against the wrong thing,
/// the accumulation losing a message, and the survivability the whole table exists for.
/// </para>
/// </remarks>
public sealed class NotifyVolumeTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";
    private const string Later = "2026-01-09T00:00:00.0000000+00:00";

    private readonly SqliteConnection _connection;
    private readonly NotifyVolumeRepository _volume;
    private int _hashes;

    public NotifyVolumeTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _volume = new NotifyVolumeRepository(_connection);

        Execute("INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders@test','test','full','active'),('billing@test','test','full','active'),"
              + "('inventory@test','test','full','active');");
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private void Notify(string from, string to, string text, string when = Stamp) => Execute(
        "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
      + "payload_json, content_hash, created_utc) VALUES (NULL,'notify','" + from + "','" + to
      + "','some-model-9','{\"text\":\"" + text + "\"}','"
      + (++_hashes).ToString("D64", CultureInfo.InvariantCulture) + "','" + when + "');");

    [Fact]
    public async Task Nothing_sent_is_reported_as_nothing_rather_than_as_zero_flows()
    {
        var report = await _volume.ReadAsync(Ct);

        Assert.True(report.IsEmpty);
        Assert.Equal(0, report.Sent);
        Assert.Equal(0, report.Mean);
    }

    [Fact]
    public async Task Each_notification_is_counted_once_and_its_size_accumulated()
    {
        Notify("orders@test", "billing@test", "one");
        Notify("orders@test", "billing@test", "two");
        Notify("orders@test", "billing@test", "three");

        var report = await _volume.ReadAsync(Ct);
        var flow = Assert.Single(report.Flows);

        Assert.Equal(3, flow.Sent);

        // Measured on payload_json, which is what rule 8.5 caps, so the numbers here are
        // comparable to the limit they will be weighed against. {"text":"one"} is 14 characters
        // and {"text":"three"} is 16 -- spelled out rather than computed, because a test that
        // derives its expectation the same way the code does agrees with a bug.
        Assert.Equal(14 + 14 + 16, flow.Characters);
        Assert.Equal(16, flow.Largest);
    }

    [Fact]
    public async Task The_largest_is_kept_because_a_mean_hides_it()
    {
        // Twenty short notices and one enormous one average out to something unremarkable and are
        // completely different problems. If largest were dropped the report could not tell them
        // apart, which is the distinction the decision turns on.
        Notify("orders@test", "billing@test", "x");
        Notify("orders@test", "billing@test", new string('y', 500));

        var flow = Assert.Single((await _volume.ReadAsync(Ct)).Flows);

        Assert.Equal(2, flow.Sent);
        Assert.True(flow.Largest > 500, "the long one must be recorded as the largest");
        Assert.True(flow.Mean < flow.Largest, "the mean must not be mistaken for the worst case");
    }

    [Fact]
    public async Task Both_ends_of_the_span_are_kept_because_a_rate_needs_one()
    {
        // Twenty notifications in an hour and twenty over a month are the same count and opposite
        // findings. last_utc alone cannot separate them.
        Notify("orders@test", "billing@test", "first", Stamp);
        Notify("orders@test", "billing@test", "last", Later);

        var flow = Assert.Single((await _volume.ReadAsync(Ct)).Flows);

        Assert.StartsWith("2026-01-01", flow.FirstUtc, StringComparison.Ordinal);
        Assert.StartsWith("2026-01-09", flow.LastUtc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directions_are_counted_apart()
    {
        // The same reason §7.1 counts slots directionally: a busy flow one way says nothing about
        // the other, and merging them would report one agent's noise against its peer's name.
        Notify("orders@test", "billing@test", "out");
        Notify("billing@test", "orders@test", "back");
        Notify("billing@test", "orders@test", "again");

        var report = await _volume.ReadAsync(Ct);

        Assert.Equal(2, report.Flows.Count);
        Assert.Equal(2, report.Flows.Single(f => f.From == "billing@test").Sent);
        Assert.Equal(1, report.Flows.Single(f => f.From == "orders@test").Sent);
    }

    [Fact]
    public async Task Only_notifications_are_counted()
    {
        // The trigger fires on messages, and every other type goes through the same table. A
        // question counted here would inflate the one number the decision rests on.
        Notify("orders@test", "billing@test", "an event");

        Execute(
            "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
          + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
          + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
          + "('billing@test' || char(31) || 'orders@test','orders@test','billing@test','billing@test','OPEN',0,3,5,"
          + "900000,14400000,86400000,'" + Stamp + "','" + Stamp + "','h');");

        Execute(
            "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
          + "payload_json, content_hash, created_utc) VALUES (1,'clarify','billing@test','orders@test',"
          + "'some-model-9','{\"text\":\"which one?\"}','"
          + (++_hashes).ToString("D64", CultureInfo.InvariantCulture) + "','" + Stamp + "');");

        var flow = Assert.Single((await _volume.ReadAsync(Ct)).Flows);

        Assert.Equal(1, flow.Sent);
    }

    [Fact]
    public async Task The_count_survives_the_messages_being_pruned()
    {
        // The whole reason this is a table rather than a query over messages. §9.4 prunes by
        // DELETE FROM messages, and a measurement that could only see the retention window would
        // be useless for deciding a limit.
        Notify("orders@test", "billing@test", "one");
        Notify("orders@test", "billing@test", "two");

        Execute("DELETE FROM messages;");

        Assert.Equal(0, Scalar("SELECT count(*) FROM messages;"));

        var flow = Assert.Single((await _volume.ReadAsync(Ct)).Flows);

        Assert.Equal(2, flow.Sent);
    }

    [Fact]
    public async Task What_each_agent_received_is_totalled_across_its_senders()
    {
        // The number a survey threshold reads. An agent judging whether notifications are worth
        // having is judging all of them, not one peer's share — so a total per recipient is the
        // figure, and per-flow counts would each look small while the inbox was full.
        Notify("orders@test", "billing@test", "a");
        Notify("orders@test", "billing@test", "b");
        Notify("inventory@test", "billing@test", "c");
        Notify("orders@test", "inventory@test", "d");

        var report = await _volume.ReadAsync(Ct);

        Assert.Equal(3, report.Received.Single(r => r.Agent == "billing@test").Received);
        Assert.Equal(1, report.Received.Single(r => r.Agent == "inventory@test").Received);
    }

    [Fact]
    public async Task The_overall_mean_is_weighted_by_messages_and_not_by_flow()
    {
        // One pair sending many short notices must not be balanced out by a pair that sent one
        // long one. Averaging the flows rather than the messages would report a mean no message
        // was near.
        for (var i = 0; i < 10; i++)
        {
            Notify("orders@test", "billing@test", "x");
        }

        Notify("inventory@test", "billing@test", new string('y', 1000));

        var report = await _volume.ReadAsync(Ct);

        Assert.Equal(11, report.Sent);

        // Eleven messages, ten of them tiny: the weighted mean stays near the small ones.
        Assert.True(report.Mean < 200, "a flow-averaged mean would land near 500");
    }
}
