using Microsoft.Data.Sqlite;
using Xunit;

namespace Parley.Store.Tests;

/// <summary>
/// The report SPEC.md §17 question 4 needs before the clarify cap can be argued about.
/// </summary>
/// <remarks>
/// <para>
/// The figures matter more than most reports because of what they will be used for: somebody
/// deciding whether a protocol rule should exist. A count that is plausible and wrong here does
/// not produce a visible failure — it produces a confident change to the spec.
/// </para>
/// <para>
/// So these probe the two ways this particular query shape goes wrong. One is a join between
/// exchanges and messages multiplying exchange rows by the messages on them, which inflates every
/// total while leaving the distribution looking sensible. The other is counting an agent's honest
/// <c>unknown</c> among the broker's forced ones, which would report a healthy deployment as one
/// whose cap keeps firing.
/// </para>
/// </remarks>
public sealed class ClarifyReportTests : IDisposable
{
    private const string Stamp = "2026-01-01T00:00:00.0000000+00:00";
    // Distinct per message. ux_message_dedup refuses two messages sharing a hash inside one
    // 60-second bucket, which is the deduplication of SPEC.md §13.1 doing its job -- a fixture
    // reusing one hash silently tests one message where it meant to test three.
    private int _hashes;

    private readonly SqliteConnection _connection;
    private readonly ClarifyReportRepository _report;

    public ClarifyReportTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _report = new ClarifyReportRepository(_connection);

        Execute("INSERT INTO nodes (machine, connected, first_seen_utc) VALUES ('test', 1, '2026-01-01T00:00:00+00:00') ON CONFLICT DO NOTHING; INSERT INTO agents (name, adapter, tier, status) VALUES "
              + "('orders@test','test','full','active'),('billing@test','test','full','active');");
    }

    public void Dispose() => _connection.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Exchange(string question, int depth, int cap, string? resolution = null)
    {
        var state = resolution is null ? "OPEN" : "CLOSED";
        var closed = resolution is null ? "NULL" : "'" + Stamp + "'";
        var res = resolution is null ? "NULL" : "'" + resolution + "'";

        Execute(
            "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
          + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
          + "grace_period_ms, opened_utc, expires_utc, closed_utc, resolution, question_hash) "
          + "VALUES ('billing@test' || char(31) || 'orders@test','orders@test','billing@test','billing@test','" + state
          + "'," + depth + "," + cap + ",5,900000,14400000,86400000,'" + Stamp + "','" + Stamp
          + "'," + closed + "," + res + ",'" + question + "');");

        using var id = _connection.CreateCommand();
        id.CommandText = "SELECT id FROM exchanges WHERE question_hash = '" + question + "';";
        return (long)id.ExecuteScalar()!;
    }

    private void Answer(long exchangeId, string responder, string payload) => Execute(
        "INSERT INTO messages (exchange_id, type, from_agent, to_agent, responder_model, "
      + "payload_json, content_hash, created_utc) VALUES (" + exchangeId
      + ",'answer','billing@test','orders@test','" + responder + "','" + payload + "','"
      + (++_hashes).ToString("D64", System.Globalization.CultureInfo.InvariantCulture) + "','"
      + Stamp + "');");

    private static string Resolutions(params string[] kinds) =>
        "{\"resolutions\":[" + string.Join(",", kinds.Select((k, i) =>
            "{\"id\":\"q" + (i + 1) + "\",\"resolution\":\"" + k + "\",\"text\":\"t\"}")) + "]}";

    [Fact]
    public async Task An_empty_deployment_reports_nothing_rather_than_a_shape()
    {
        var report = await _report.ReadAsync(Ct);

        Assert.Equal(0, report.Exchanges);
        Assert.Empty(report.Distribution);
        Assert.Equal(0, report.DeepestReached);
    }

    [Fact]
    public async Task The_distribution_counts_exchanges_and_includes_the_ones_that_never_clarified()
    {
        // Depth zero is the decisive bucket. If the report dropped it — which a GROUP BY over
        // only clarified exchanges would — every deployment would look like one where
        // clarification is normal, which is the opposite of what the evidence is for.
        Exchange("a", depth: 0, cap: 3);
        Exchange("b", depth: 0, cap: 3);
        Exchange("c", depth: 2, cap: 3);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(3, report.Exchanges);
        Assert.Equal(2, report.Distribution.Single(b => b.Depth == 0).Exchanges);
        Assert.Equal(1, report.Distribution.Single(b => b.Depth == 2).Exchanges);
        Assert.Equal(2, report.DeepestReached);
    }

    [Fact]
    public async Task Messages_do_not_multiply_the_exchange_counts()
    {
        // The bug this report is most likely to have. One exchange carrying several messages
        // must still be one exchange; a join would make it three and the distribution would
        // stay plausible while every total was wrong.
        var id = Exchange("a", depth: 1, cap: 3, resolution: "answered");
        Answer(id, "some-model-9", Resolutions("answered"));
        Answer(id, "some-model-9", Resolutions("answered"));
        Answer(id, "some-model-9", Resolutions("answered"));

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Exchanges);
        Assert.Equal(1, report.Distribution.Single(b => b.Depth == 1).Exchanges);
    }

    [Fact]
    public async Task Reaching_the_cap_is_counted_and_a_cap_of_zero_is_not_counted_as_reached()
    {
        // At a cap of zero every exchange satisfies `depth >= cap` at depth zero. Counted
        // naively, the strictest possible setting would report as universally breached — which
        // reads as the cap firing constantly when in fact nothing has clarified at all.
        Exchange("at", depth: 3, cap: 3);
        Exchange("under", depth: 1, cap: 3);
        Exchange("none", depth: 0, cap: 0);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.ExchangesAtCap);
    }

    [Fact]
    public async Task The_caps_in_force_are_reported_so_the_distribution_can_be_read()
    {
        // Without these the histogram is unreadable: a pile at the top means the measurement was
        // truncated, and you cannot see the top without being told what it was.
        Exchange("a", depth: 0, cap: 3);
        Exchange("b", depth: 0, cap: 200);

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(3, report.LowestCapInForce);
        Assert.Equal(200, report.HighestCapInForce);
    }

    [Fact]
    public async Task An_agents_own_unknown_is_just_an_answer()
    {
        // This used to assert that a broker-authored `unknown` was counted apart from an agent's
        // own. Nothing writes one: SPEC.md §5.2 refuses a breach and leaves the exchange alone,
        // because `unknown` means "I own this and I do not know" and only the answerer can say
        // it. What remains worth pinning is that an honest `unknown` -- which the tool
        // description actively asks for -- is an ordinary answer and inflates nothing.
        var honest = Exchange("honest", depth: 0, cap: 3, resolution: "unknown");
        Answer(honest, "some-model-9", Resolutions("unknown"));

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(0, report.ExchangesAtCap);
        Assert.Equal(1, report.Answers);
    }

    [Fact]
    public async Task An_answer_resolving_its_sub_questions_alike_is_not_counted_as_divergent()
    {
        var id = Exchange("a", depth: 0, cap: 3, resolution: "answered");
        Answer(id, "some-model-9", Resolutions("answered", "answered", "answered"));

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Answers);
        Assert.Equal(0, report.AnswersResolvingSubQuestionsDifferently);
    }

    [Fact]
    public async Task An_answer_that_treats_its_sub_questions_differently_is()
    {
        // The whole test of whether sub-questions carry information. If no answer anywhere ever
        // resolves them differently, the structure is formatting and the per-sub-question budget
        // §17 question 4 floats is not worth building.
        var id = Exchange("a", depth: 0, cap: 3, resolution: "unknown");
        Answer(id, "some-model-9", Resolutions("answered", "unknown", "out_of_scope"));

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(1, report.Answers);
        Assert.Equal(1, report.AnswersResolvingSubQuestionsDifferently);
    }

    [Fact]
    public async Task A_single_sub_question_is_never_divergent()
    {
        // count(DISTINCT ...) > 1 over one element must be false. Worth pinning: an off-by-one
        // here would report every answer as divergent and settle a design question wrongly in
        // the direction of more machinery.
        var id = Exchange("a", depth: 0, cap: 3, resolution: "answered");
        Answer(id, "some-model-9", Resolutions("answered"));

        var report = await _report.ReadAsync(Ct);

        Assert.Equal(0, report.AnswersResolvingSubQuestionsDifferently);
    }
}
