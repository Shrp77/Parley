using Microsoft.Data.Sqlite;
using Parley.Broker;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// The operator operations of SPEC.md §10.2, as a person runs them.
/// </summary>
/// <remarks>
/// <para>
/// Until now the only one that existed was <c>force_conclude</c>, and nothing hosted it — so
/// <c>retire</c>, which §14 leans on and which the MCP server enforces at launch, could not be
/// reached except by writing SQL by hand. A control nobody can invoke is not a control.
/// </para>
/// <para>
/// These drive the commands rather than the process, so what is pinned is what each one does and
/// what it says. The entry point keeps only argument shape and exit codes.
/// </para>
/// </remarks>
public sealed class OperatorCommandTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders");
    private static readonly AgentName Billing = AgentName.Create("billing");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy and we can diverge.";

    private readonly SqliteConnection _connection;
    private readonly AgentRepository _agents;
    private readonly StringWriter _output = new();
    private readonly OperatorCommands _commands;

    public OperatorCommandTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        _agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            _agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, $"/src/acme-{name.Value}", null, null,
                    null, AgentStatus.Registered),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _commands = new OperatorCommands(new OperatorConsole(_connection), _output);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _output.Dispose();
    }

    private string Printed => _output.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AgentStatus> StatusOf(AgentName name) =>
        (await _agents.GetAllAsync(Ct)).Single(a => a.Name == name).Status;

    [Fact]
    public async Task Listing_agents_shows_what_a_peer_is_never_shown()
    {
        // §10.1 rule 3 keeps the workspace out of the agent-facing listing, because a peer has
        // no business knowing where another repository lives. An operator is looking at their
        // own deployment, so this is the projection that does show it — which is exactly why the
        // two operations are named apart.
        Assert.Equal(0, await _commands.RegistryAsync(Ct));

        Assert.Contains("orders", Printed, StringComparison.Ordinal);
        Assert.Contains("/src/acme-orders", Printed, StringComparison.Ordinal);
        Assert.Contains("registered", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_registry_says_how_one_gets_filled()
    {
        using var connection = ParleyDatabase.OpenInMemory();
        using var output = new StringWriter();

        var commands = new OperatorCommands(new OperatorConsole(connection), output);

        Assert.Equal(0, await commands.RegistryAsync(Ct));
        Assert.Contains("first time its server starts", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_withdraws_the_registration()
    {
        Assert.Equal(0, await _commands.RetireAsync(Billing, Ct));
        Assert.Equal(AgentStatus.Retired, await StatusOf(Billing));

        // Says what it did not do, because that is the part an operator gets wrong: retiring is
        // about the registry, and the exchanges the agent already owed run out of time on the
        // ordinary schedule.
        Assert.Contains("nobody answered", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_twice_reports_it_and_exits_non_zero()
    {
        Assert.Equal(0, await _commands.RetireAsync(Billing, Ct));
        Assert.Equal(1, await _commands.RetireAsync(Billing, Ct));

        Assert.Equal(AgentStatus.Retired, await StatusOf(Billing));
        Assert.Contains("already retired", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_an_agent_that_does_not_exist_is_refused_rather_than_silent()
    {
        Assert.Equal(1, await _commands.RetireAsync(AgentName.Create("nobody"), Ct));
        Assert.Contains("not registered", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reinstating_is_the_only_way_back()
    {
        // §14: retirement is cleared by an explicit operator action and by nothing else — not by
        // registration, and not by an agent attaching. Without this the state is terminal, and
        // an operator who retired the wrong agent has no way back short of editing the database.
        await _commands.RetireAsync(Billing, Ct);

        Assert.Equal(0, await _commands.ReinstateAsync(Billing, Ct));

        // Offline rather than registered, which is right: the agent is not attached, and offline
        // is the ordinary resting state of a lazily spawned agent between questions. It is
        // routable again, which is what reinstating means.
        Assert.Equal(AgentStatus.Offline, await StatusOf(Billing));
        Assert.False(await _agents.IsUnroutableAsync(Billing, cancellationToken: Ct));
    }

    [Fact]
    public async Task Reinstating_an_agent_that_was_not_retired_changes_nothing()
    {
        Assert.Equal(1, await _commands.ReinstateAsync(Billing, Ct));
        Assert.Equal(AgentStatus.Registered, await StatusOf(Billing));
        Assert.Contains("not retired", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_reads_across_agents_which_is_why_it_is_an_operator_operation()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var question =
            $$"""{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"{{Consequences}}"}""";

        var asked = await broker.AskAsync(
            Orders, Billing, "some-model-9", question, T0.AddMinutes(1), "pricing", Ct);

        Assert.True(asked.Succeeded, $"expected admission, got {asked.Rejection}");

        Assert.Equal(0, await _commands.ExportAsync("pricing", Ct));

        // The payload as stored, not the envelope of §13. An envelope marks a peer's words as
        // data inside another agent's context, and there is no agent here.
        Assert.Contains("gross or net?", Printed, StringComparison.Ordinal);
        Assert.Contains("some-model-9", Printed, StringComparison.Ordinal);
        Assert.DoesNotContain("begin peer payload", Printed, StringComparison.Ordinal);

        // Readable rather than merely present. The longest type name is exactly the width the
        // column was first padded to, so every question ran into its sender.
        Assert.Contains("question orders -> billing", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retiring_leaves_work_already_in_flight_alone()
    {
        // §14: retiring is about the registry, not the traffic. What the agent already owed
        // orphans and is ghosted on the ordinary schedule, and each asker is told with its own
        // stated consequences — the same path any abandoned exchange takes.
        //
        // An answer that does arrive is still accepted. Nothing checks retirement on the way in,
        // and that is the right way round: ghosting does not discard, and neither should this.
        // An agent cannot reach it in practice — its server refuses to start — so this is what
        // happens if one settles a debt some other way, and the answer is worth more than the
        // ghost it would otherwise become.
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var question =
            $$"""{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"{{Consequences}}"}""";

        var asked = await broker.AskAsync(
            Orders, Billing, "some-model-9", question, T0.AddMinutes(1), cancellationToken: Ct);

        Assert.Equal(0, await _commands.RetireAsync(Billing, Ct));

        var exchanges = new ExchangeRepository(_connection);
        Assert.Equal(
            ExchangeState.Open,
            (await exchanges.GetAsync(asked.Exchange!.Id, cancellationToken: Ct))!.State);

        // Nothing new may be asked of it.
        var refused = await broker.AskAsync(
            Orders, Billing, "some-model-9",
            $$"""{"intent":"and refunds","sub_questions":[{"id":"q1","text":"and refunds?"}],"consequences":"{{Consequences}}"}""",
            T0.AddMinutes(2), cancellationToken: Ct);

        Assert.Equal(OpenRejection.AnswererUnreachable, refused.Rejection);

        // What it already owed, it may still settle.
        Assert.True((await broker.AnswerAsync(
            asked.Exchange.Id, Billing, "some-model-9",
            """{"resolutions":[{"id":"q1","resolution":"answered","text":"net"}]}""",
            T0.AddMinutes(3), cancellationToken: Ct)).Succeeded);
    }

    [Fact]
    public async Task Exporting_a_conversation_nobody_started_says_where_they_come_from()
    {
        Assert.Equal(1, await _commands.ExportAsync("never-happened", Ct));
        Assert.Contains("named one on a question", Printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_concluding_closes_a_discussion_over_its_votes()
    {
        var broker = new Broker.Broker(_connection, BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, Ct);

        var question =
            $$"""{"intent":"where does price live","sub_questions":[{"id":"q1","text":"gross or net?"}],"consequences":"{{Consequences}}"}""";

        await broker.AskAsync(Orders, Billing, "some-model-9", question, T0.AddMinutes(1), "pricing", Ct);

        Assert.Equal(0, await _commands.ForceConcludeAsync("pricing", T0.AddMinutes(2), Ct));

        // And it stays closed: a new question naming it is refused, which is what the operator
        // was reaching for.
        var reopened = await broker.AskAsync(
            Orders, Billing, "some-model-9",
            $$"""{"intent":"and refunds","sub_questions":[{"id":"q1","text":"and refunds?"}],"consequences":"{{Consequences}}"}""",
            T0.AddMinutes(3), "pricing", Ct);

        Assert.Equal(OpenRejection.ConversationClosed, reopened.Rejection);
    }

    [Fact]
    public async Task Force_concluding_something_that_is_not_open_reports_it()
    {
        Assert.Equal(1, await _commands.ForceConcludeAsync("never-happened", T0, Ct));
        Assert.Contains("not open", Printed, StringComparison.Ordinal);
    }
}
