using System.Globalization;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a node's turn reports are recorded when they check out and ignored when they do not (SPEC.md §11.6, specs/024).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as the malicious and awkward cases first</b>, each watched to fail before the code existed: a node reporting
/// for another machine's agent, a node not joined, an end arriving after a newer start, a node dropping mid-turn, a broker
/// that never heard a turn begin. Read back through the operator's registry, because a fact nobody can read through the
/// protocol is not evidence the protocol records anything — and because this project may not open a store.
/// </para>
/// </remarks>
public sealed class TurnReportingTests
{
    private const string Here = "m1";
    private const string Agent = "billing@m1";
    private const string Person = "human-user@test";

    // Today, to the second: the broker believes a node's time only within a day of its own clock.
    private static readonly DateTimeOffset T0 = new(
        DateTimeOffset.UtcNow.Ticks - (DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Wire.TurnReport Started(string machine, string agent, DateTimeOffset at) =>
        new() { Machine = machine, Agent = agent, Started = new Wire.TurnStarted { StartedUtc = Stamp(at) } };

    private static Wire.TurnReport Ended(string machine, string agent, DateTimeOffset began, int denials = 0) =>
        new()
        {
            Machine = machine,
            Agent = agent,
            Ended = new Wire.TurnEnded
            {
                StartedUtc = Stamp(began),
                ElapsedMs = 90_000,
                Clean = true,
                PermissionDenials = denials,
            },
        };

    // An agent as its node starts it: the launch named to the broker first, then the token presented on attach (§11.7).
    private static async Task<AttachedClient> LaunchedAsync(LoopbackBroker broker, JoinedNodeClient node)
    {
        _ = await node.Nodes.ReportLaunchAsync(
            new Wire.LaunchReport { Machine = Here, Agent = Agent, Token = "launch-1" }, cancellationToken: Ct);

        return await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct, launchToken: "launch-1");
    }

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker, string name) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == name);

    [Fact]
    public async Task A_turn_reads_as_running_and_then_as_ended_with_its_refusals()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);
        await using var agent = await LaunchedAsync(broker, node);

        await node.Nodes.ReportTurnAsync(Started(Here, Agent, T0), cancellationToken: Ct);

        var running = await RowAsync(broker, Agent);

        Assert.True(running.TurnsReported);
        Assert.Equal(Stamp(T0), running.TurnRunningSinceUtc);

        await node.Nodes.ReportTurnAsync(Ended(Here, Agent, T0, denials: 2), cancellationToken: Ct);

        var ended = await RowAsync(broker, Agent);

        Assert.Empty(ended.TurnRunningSinceUtc);
        Assert.Equal(Stamp(T0.AddSeconds(90)), ended.LastTurnEndedUtc);
        Assert.Equal(2, ended.LastTurnPermissionDenials);
        Assert.True(ended.LastTurnClean);
    }

    [Fact]
    public async Task A_node_cannot_report_for_an_agent_on_another_machine()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var elsewhere = await AttachedClient.AttachAsync(broker, "billing@m2", cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Started(Here, "billing@m2", T0), cancellationToken: Ct);
        await node.Nodes.ReportTurnAsync(Ended(Here, "billing@m2", T0, denials: 3), cancellationToken: Ct);

        var row = await RowAsync(broker, "billing@m2");

        Assert.Empty(row.TurnRunningSinceUtc);
        Assert.Empty(row.LastTurnEndedUtc);
    }

    [Fact]
    public async Task A_node_that_has_not_joined_is_not_heard()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        var stranger = broker.ConnectAsNode();

        await stranger.ReportTurnAsync(Started(Here, Agent, T0), cancellationToken: Ct);
        await stranger.ReportTurnAsync(Ended(Here, Agent, T0), cancellationToken: Ct);

        var row = await RowAsync(broker, Agent);

        Assert.Empty(row.TurnRunningSinceUtc);
        Assert.Empty(row.LastTurnEndedUtc);
        Assert.False(row.TurnsReported);
    }

    [Fact]
    public async Task A_late_end_of_an_older_turn_does_not_stop_a_newer_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Started(Here, Agent, T0), cancellationToken: Ct);
        await node.Nodes.ReportTurnAsync(Started(Here, Agent, T0.AddMinutes(5)), cancellationToken: Ct);
        await node.Nodes.ReportTurnAsync(Ended(Here, Agent, T0), cancellationToken: Ct);

        Assert.Equal(Stamp(T0.AddMinutes(5)), (await RowAsync(broker, Agent)).TurnRunningSinceUtc);
    }

    [Fact]
    public async Task A_node_that_goes_away_mid_turn_is_no_longer_claimed_to_be_running_one()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);

        var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Started(Here, Agent, T0), cancellationToken: Ct);

        Assert.NotEmpty((await RowAsync(broker, Agent)).TurnRunningSinceUtc);

        await node.DisposeAsync();

        // The node's stream ends asynchronously on the broker's side, so this waits for it rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while ((await RowAsync(broker, Agent)).TurnRunningSinceUtc.Length > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "a turn was still claimed to be running after its node left");

            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task An_end_the_broker_never_heard_begin_is_still_recorded_and_nothing_runs()
    {
        // The broker restarted mid-turn: it holds no running turn, and the end must still land.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Ended(Here, Agent, T0, denials: 1), cancellationToken: Ct);

        var row = await RowAsync(broker, Agent);

        Assert.Empty(row.TurnRunningSinceUtc);
        Assert.Equal(1, row.LastTurnPermissionDenials);
    }

    [Fact]
    public async Task A_node_that_does_not_say_it_reports_turns_leaves_its_agents_unknown()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct);

        Assert.False((await RowAsync(broker, Agent)).TurnsReported);
    }

    [Fact]
    public async Task A_report_for_an_agent_nobody_registered_stores_nothing_and_fails_nothing()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Started(Here, "nobody@m1", T0), cancellationToken: Ct);
        await node.Nodes.ReportTurnAsync(Ended(Here, "nobody@m1", T0), cancellationToken: Ct);

        var rows = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows;

        Assert.DoesNotContain(rows, row => row.Name == "nobody@m1");
    }

    [Theory]
    [InlineData(48L * 60 * 60 * 1000, 0)]
    [InlineData(900_000_000_000_000L, 0)]
    [InlineData(long.MaxValue, 0)]
    [InlineData(90_000, 365)]
    public async Task One_implausible_turn_cannot_break_the_registry_or_the_flags_for_everyone(long elapsedMs, int daysAhead)
    {
        // The review gate's finding: one report whose end fell past the calendar made every later registry and flags read
        // throw — for every surface, for the whole broker, and surviving the reporter leaving. No victim was needed: an
        // agent of the attacker's own was enough.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing"], Ct, reportsTurns: true);

        var report = Ended(Here, Agent, T0.AddDays(daysAhead), denials: 1);
        report.Ended.ElapsedMs = elapsedMs;
        report.Ended.CostUsd = double.PositiveInfinity;

        await node.Nodes.ReportTurnAsync(report, cancellationToken: Ct);

        if (daysAhead > 0)
        {
            // A start a year ahead would hold the agent "working" until then.
            await node.Nodes.ReportTurnAsync(Started(Here, Agent, T0.AddDays(daysAhead)), cancellationToken: Ct);
        }

        var row = await RowAsync(broker, Agent);

        _ = await broker.ConnectAsOperator().DescribeFlagsAsync(new Wire.Empty(), cancellationToken: Ct);

        Assert.Empty(row.LastTurnEndedUtc);
        Assert.Empty(row.TurnRunningSinceUtc);
    }

    [Fact]
    public async Task Turns_are_reported_only_for_an_agent_the_node_can_start()
    {
        // Decided per agent, not per machine (SPEC.md §11.6, §11.7): an agent its node did not start is unknown, never
        // stopped. Before, any agent on a reporting node's machine read as reported — including one opened in an editor.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, ["billing", "docs"], Ct, reportsTurns: true);
        await using var billing = await LaunchedAsync(broker, node);
        await using var editor = await AttachedClient.AttachAsync(broker, "docs@m1", cancellationToken: Ct);

        Assert.True((await RowAsync(broker, Agent)).TurnsReported);
        Assert.False((await RowAsync(broker, "docs@m1")).TurnsReported);
    }

    [Fact]
    public async Task Neither_the_person_nor_Parley_reads_as_reported_whatever_node_joins()
    {
        // Their names end in the project, so a node joined under the project's name matched them by machine.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(
            broker, "test", ["human-user", "parley-facilitator"], Ct, reportsTurns: true);

        Assert.False((await RowAsync(broker, Person)).TurnsReported);
    }

    [Fact]
    public async Task No_node_reports_a_turn_for_the_person_or_for_Parley()
    {
        // Neither is hosted on a machine: the half of their names after '@' is the project, so the machine comparison
        // alone admitted any node joined under the project's name — and a PermissionRefused flag against the person.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, "test", [], Ct, reportsTurns: true);

        foreach (var name in new[] { Person, "parley-facilitator@test" })
        {
            await node.Nodes.ReportTurnAsync(Started("test", name, T0), cancellationToken: Ct);
            await node.Nodes.ReportTurnAsync(Ended("test", name, T0, denials: 3), cancellationToken: Ct);
            await node.Nodes.ReportTurnAsync(Started("test", name, T0.AddMinutes(5)), cancellationToken: Ct);
        }

        var rows = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows;
        var person = rows.Single(row => row.Name == Person);

        Assert.Empty(person.TurnRunningSinceUtc);
        Assert.Empty(person.LastTurnEndedUtc);
        Assert.DoesNotContain(
            rows,
            row => row.Name == "parley-facilitator@test" && (row.LastTurnEndedUtc.Length > 0 || row.TurnRunningSinceUtc.Length > 0));
    }

    [Fact]
    public async Task Nothing_but_a_surface_seating_the_person_attaches_as_the_person()
    {
        // SPEC.md §12.3 says attaching under the reserved role is refused, and no code did: an agent's adapter attached as
        // the person and opened a conversation with the person's standing (principle 2.10).
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await using var impostor = await AttachedClient.AttachAsync(
                broker, Person, cancellationToken: Ct, adapter: "claude-code");
        });

        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Contains("person's seat", refused.Status.Detail, StringComparison.Ordinal);

        await using var seat = await AttachedClient.AttachAsync(
            broker, Person, cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);
    }

    [Fact]
    public async Task No_agent_attaches_under_the_persons_adapter()
    {
        // The other half of §12.3's tie, and the one with the standing in it: the broker recognises the person by the
        // seat's adapter (principle 2.10 — a person may always name a conversation), and registration writes whatever
        // adapter the caller reports. Admitted until the combined review gate, round 3.
        await using var broker = await LoopbackBroker.StartAsync(Ct);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await using var impostor = await AttachedClient.AttachAsync(
                broker, "orders@m1", cancellationToken: Ct, adapter: Parley.Core.PersonSeat.Adapter);
        });

        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, refused.StatusCode);
    }
}
