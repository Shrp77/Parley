using System.Text.Json.Nodes;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A node joining a broker that wakes agents for the work they owe (SPEC.md §14, §4.0).
/// </summary>
/// <remarks>
/// Review, quality lens run 1 (0.62.1): the wake ran after the join was recorded and outside the cleanup that undoes a
/// failed join, so a wake that threw (the node's call cancelled mid-read) left the node marked connected with nothing
/// to remove it, and every retry was refused as a second node until the broker restarted.
/// </remarks>
[Integration]
public sealed class AJoinThatWakesAgentsNeverStrandsTheNodeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@probe");
    private static readonly AgentName Billing = AgentName.Create("billing@probe");

    [Fact]
    public async Task A_wake_that_fails_does_not_fail_the_join_and_is_asked_for_again_at_the_next()
    {
        var token = TestContext.Current.CancellationToken;
        var gate = new CountingGate(failFirst: true);

        await using var runtime = await OpenWithOwedWorkAsync(gate, token);

        var first = await JoinAsync(runtime, token);
        Assert.Equal(1, gate.Asked);
        Assert.Contains("probe", runtime.Nodes);

        await first.DisposeAsync();

        // Not refused as a second node, and the wake that failed is asked for again.
        var second = await JoinAsync(runtime, token);
        Assert.Equal(2, gate.Asked);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task A_machine_is_woken_once_however_often_its_node_rejoins()
    {
        var token = TestContext.Current.CancellationToken;
        var gate = new CountingGate(failFirst: false);

        await using var runtime = await OpenWithOwedWorkAsync(gate, token);

        for (var round = 0; round < 3; round++)
        {
            await (await JoinAsync(runtime, token)).DisposeAsync();
        }

        Assert.Equal(1, gate.Asked);
    }

    private static async Task<BrokerRuntime> OpenWithOwedWorkAsync(CountingGate gate, CancellationToken token)
    {
        var runtime = BrokerRuntime.OpenInMemory(
            BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always, ResumeGate = gate },
            TimeProvider.System);

        // At the join, as these tests are about what a wake does to the join and not about when it runs.
        runtime.RestartWakeDelay = TimeSpan.Zero;

        _ = await runtime.UseConnectionAsync(
            async (connection, now, ct) =>
            {
                foreach (var name in new[] { Orders, Billing })
                {
                    await new AgentRepository(connection).UpsertAsync(
                        new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), ct);
                }

                var broker = new Parley.Broker.Broker(connection);
                await broker.StartAsync(Orders, now, ct);
                var sent = await broker.RequestAsync(
                    Orders, Billing, "claude-opus-5", new JsonObject { ["ask"] = "price?" }.ToJsonString(), now, "pricing", ct);
                Assert.True(sent.Succeeded);
                return true;
            },
            token);

        return runtime;
    }

    // Waits for the restart wake the join scheduled, which runs beside the join rather than inside it.
    private static async Task<JoinedNode> JoinAsync(BrokerRuntime runtime, CancellationToken token)
    {
        var joined = await runtime.JoinNodeAsync(
            new NodeRecord("probe", null, null, null, null, true, DateTimeOffset.UtcNow),
            ["orders", "billing"],
            token);

        await runtime.RestartWakeOf("probe").WaitAsync(token);

        return joined;
    }

    private sealed class CountingGate(bool failFirst) : IResumeGate
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _asked) == 1 && failFirst
                ? throw new OperationCanceledException("the node's call was cancelled")
                : ValueTask.FromResult(true);
    }
}
