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
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

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

    private static async Task<Wire.RegistryRow> RowAsync(LoopbackBroker broker, string name) =>
        (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows
        .Single(row => row.Name == name);

    [Fact]
    public async Task A_turn_reads_as_running_and_then_as_ended_with_its_refusals()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var agent = await AttachedClient.AttachAsync(broker, Agent, cancellationToken: Ct);
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

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
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

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
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

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

        var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

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
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

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
        await using var node = await JoinedNodeClient.JoinAsync(broker, Here, [], Ct, reportsTurns: true);

        await node.Nodes.ReportTurnAsync(Started(Here, "nobody@m1", T0), cancellationToken: Ct);
        await node.Nodes.ReportTurnAsync(Ended(Here, "nobody@m1", T0), cancellationToken: Ct);

        var rows = (await broker.ConnectAsOperator().DescribeRegistryAsync(new Wire.Empty(), cancellationToken: Ct)).Rows;

        Assert.DoesNotContain(rows, row => row.Name == "nobody@m1");
    }
}
