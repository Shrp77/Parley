using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// <c>PARLEY_CLARIFY_CAP</c>, the setting that makes SPEC.md §17 question 4 answerable.
/// </summary>
/// <remarks>
/// <para>
/// The question is whether a clarify cap is needed at all, and it cannot be answered from capped
/// data: depths pile up at whatever bound was in force, so the distribution you read is the one
/// the cap produced. Raising it far above any plausible need and watching where depth settles is
/// the measurement, and this variable is what allows it without a schema change.
/// </para>
/// <para>
/// Which makes the parsing worth probing rather than reading. A cap silently defaulted after a
/// typo would produce a measurement taken under the number being measured, reported as though the
/// cap had been lifted — a wrong answer with no failure anywhere to notice.
/// </para>
/// </remarks>
public sealed class ClarifyCapLaunchTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set)
    {
        var map = set.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static (string, string)[] Minimal =>
        [(ServerOptions.DatabaseVariable, "p.db"), (ServerOptions.AgentVariable, "orders")];

    private static (string, string)[] With(string cap) =>
        [.. Minimal, (ServerOptions.ClarifyCapVariable, cap)];

    [Fact]
    public void The_documented_default_is_the_one_the_domain_applies()
    {
        // Two files name this number: ExchangePolicy, which stamps it onto exchanges, and
        // ServerOptions, whose positional record needs a compile-time constant and so cannot
        // reference it. This is the only thing stopping them drifting, and a drift would mean
        // the README documented one cap while the broker enforced another.
        Assert.Equal(ServerOptions.DefaultClarifyDepthCap, ExchangePolicy.Default.ClarifyDepthCap);
    }

    [Fact]
    public void An_unset_variable_leaves_the_default_in_place()
    {
        Assert.Equal(
            ServerOptions.DefaultClarifyDepthCap,
            ServerOptions.Read(Env(Minimal)).ClarifyDepthCap);
    }

    [Fact]
    public void The_default_is_the_measurement_setting()
    {
        // The measurement is the default now rather than something a deployment had to know to
        // opt into, because a cap censors the evidence that would justify it and only the
        // deployments already doubting the number were collecting any.
        Assert.Equal(200, ServerOptions.DefaultClarifyDepthCap);
    }

    [Fact]
    public void A_configured_cap_is_read_rather_than_defaulted()
    {
        // Deliberately not 200. This test used to assert exactly the measurement value, which
        // was sound while the default was 3 and became vacuous the moment the default moved to
        // 200: a build that ignored the variable entirely would have passed it. That is the
        // same shape as the regex that silently matched nothing — a green answer to a question
        // that was never asked — so the value here must differ from the default to mean
        // anything, and this comment is why it will look arbitrary to the next reader.
        Assert.Equal(37, ServerOptions.Read(Env(With("37"))).ClarifyDepthCap);
    }

    [Fact]
    public void Zero_is_a_setting_and_not_an_absence()
    {
        // The trap this probes: treating "0" as blank would turn the strictest available policy
        // — answer with what you have — into the default one, silently, and the deployment would
        // be running a cap nobody chose.
        Assert.Equal(0, ServerOptions.Read(Env(With("0"))).ClarifyDepthCap);
    }

    [Fact]
    public void The_top_of_the_schema_band_is_accepted()
    {
        Assert.Equal(
            ServerOptions.MaxClarifyDepthCap,
            ServerOptions.Read(Env(With("1000"))).ClarifyDepthCap);
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("three")]
    [InlineData("3.5")]
    [InlineData("")]
    [InlineData("  ")]
    public void Anything_the_schema_would_refuse_is_refused_at_start_up(string configured)
    {
        // Refused here rather than on the first question. The column is bounded in the schema, so
        // a server launched outside the band starts cleanly and then fails to admit any exchange
        // at all — reporting a launch configuration mistake to the model, which cannot fix it,
        // rather than to whoever made it.
        //
        // Blank and whitespace are in this list deliberately and pass for a different reason:
        // they fall back to the default rather than throwing. That is the intended reading of an
        // empty variable, and pinning it here stops a later "tidy-up" turning blank into an error
        // and breaking every launch configuration that declares the key without a value.
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Equal(
                ServerOptions.DefaultClarifyDepthCap,
                ServerOptions.Read(Env(With(configured))).ClarifyDepthCap);

            return;
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => ServerOptions.Read(Env(With(configured))));

        // The message has to name the variable, or somebody with eight of them in a launch
        // configuration is told only that something is wrong.
        Assert.Contains(ServerOptions.ClarifyCapVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(configured, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_band_the_server_enforces_is_the_band_the_schema_enforces()
    {
        // Restated in two places on purpose — one refuses at launch, the other at the insert — so
        // something has to keep them equal. This asserted `1000 == 1000`, which reads as a guard
        // and checks nothing: it would have passed unchanged while the schema moved underneath it,
        // which is precisely the drift it was written to catch. It now writes real rows.
        //
        // A server band wider than the schema's moves the failure from start-up to every exchange,
        // which is the outcome the start-up check exists to prevent. A narrower one silently
        // refuses settings the deployment is entitled to.
        using var connection = ParleyDatabase.OpenInMemory();

        Execute(connection,
            "INSERT INTO agents (name, adapter, tier, status) VALUES "
          + "('orders','test','full','registered'),('billing','test','full','registered');");

        Insert(connection, ServerOptions.MaxClarifyDepthCap, "at-the-top");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => Insert(connection, ServerOptions.MaxClarifyDepthCap + 1, "one-past"));
    }

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(
        Microsoft.Data.Sqlite.SqliteConnection connection, int cap, string question)
    {
        const string stamp = "2026-01-01T00:00:00.0000000+00:00";

        Execute(connection,
            "INSERT INTO exchanges (pair_key, asker, answerer, owed_by, state, clarify_depth, "
          + "clarify_depth_cap, open_pair_cap, delivery_window_ms, working_window_ms, "
          + "grace_period_ms, opened_utc, expires_utc, question_hash) VALUES "
          + "('billing' || char(31) || 'orders','orders','billing','billing','OPEN',0,"
          + cap.ToString(System.Globalization.CultureInfo.InvariantCulture)
          + ",5,900000,14400000,86400000,'" + stamp + "','" + stamp + "','" + question + "');");
    }

    [Fact]
    public async Task The_configured_cap_is_what_lands_on_a_real_exchange()
    {
        // The check the rest of this file cannot make. Every other test here reads the value back
        // out of the options object, which would keep passing if the wiring from ServerOptions to
        // ExchangePolicy were dropped -- and then the measurement would be taken at a cap of 3
        // while the launch configuration said 200 and nothing anywhere disagreed.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var options = fixture.OptionsFor("orders") with { ClarifyDepthCap = 200 };
        var session = await BrokerSession.StartAsync(options, fixture.Clock, ct);
        var tools = new ParleyTools(session, options);

        await fixture.LaunchAsync("billing", ct);

        var asked = await tools.AskAsync(
            "billing",
            "Whether this repository applies tax.",
            [new SubQuestion("q1", "Does billing freeze the rate at invoice time?")],
            "If it freezes I stop recomputing and record the contract; if not I keep recomputing.",
            cancellationToken: ct);

        Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());

        await session.DisposeAsync();

        using var connection = ParleyDatabase.Open(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT clarify_depth_cap FROM exchanges;";

        Assert.Equal(200L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task The_askers_cap_governs_the_exchange_and_not_the_answerers()
    {
        // Which side configures the budget decides whether the rule can be defeated by the party
        // it constrains. The cap exists to stop an answerer clarifying forever, so if the
        // answerer's launch configuration set it, the answerer would set its own allowance -- and
        // .mcp.json is a file in that agent's own repository.
        //
        // It is the asker's, because the asker's broker admits the exchange. Probed rather than
        // reasoned: the two servers share a database, so nothing about the call site makes the
        // answer obvious from reading it.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var askerOptions = fixture.OptionsFor("orders") with { ClarifyDepthCap = 7 };
        var asker = await BrokerSession.StartAsync(askerOptions, fixture.Clock, ct);
        var tools = new ParleyTools(asker, askerOptions);

        var answererOptions = fixture.OptionsFor("billing") with { ClarifyDepthCap = 900 };
        var answerer = await BrokerSession.StartAsync(answererOptions, fixture.Clock, ct);

        var asked = await tools.AskAsync(
            "billing",
            "Whether this repository applies tax.",
            [new SubQuestion("q1", "Does billing freeze the rate at invoice time?")],
            "If it freezes I stop recomputing and record the contract; if not I keep recomputing.",
            cancellationToken: ct);

        Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());

        await asker.DisposeAsync();
        await answerer.DisposeAsync();

        using var connection = ParleyDatabase.Open(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT clarify_depth_cap FROM exchanges;";

        Assert.Equal(7L, (long)command.ExecuteScalar()!);
    }
}
