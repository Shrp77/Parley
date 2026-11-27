using System.Text.Json.Nodes;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Mcp.Tests;

/// <summary>
/// That a lapsed freeze whose start could not be asked for, because its node left as it was asked, stays in the store, so a
/// broker that starts again before the next join still owes it (SPEC.md §14).
/// </summary>
/// <remarks>
/// Review, lens 3 on fc26cb8a: after an unreachable asking the lapse is given back in memory and must not then be cleared
/// from the store as if it had been paid. Every test passed with that skip removed, because each asked again from the same
/// broker, which reads memory.
/// </remarks>
[Integration]
public sealed class ALapseNobodyCouldBeAskedForStaysStoredTests : IDisposable
{
    private const string Machine = "probe";

    private static readonly AgentName Orders = AgentName.Create("orders@" + Machine);
    private static readonly AgentName Billing = AgentName.Create("billing@" + Machine);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-stays-stored").FullName;

    private string Store => Path.Combine(_folder, "parley.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory's to clean.
        }
    }

    [Fact]
    public async Task A_lapse_whose_node_left_as_it_was_asked_is_paid_by_the_next_broker_too()
    {
        await FrozenWithMailAsync();

        var gate = new LeavingAtTheSecondGate();

        await using (var runtime = BrokerRuntime.Open(
            ProjectName.Create("test"),
            Store,
            BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always, ResumeGate = gate },
            TimeProvider.System))
        {
            runtime.RestartWakeDelay = TimeSpan.Zero;

            var first = await runtime.JoinNodeAsync(
                new NodeRecord(Machine, null, null, null, null, true, DateTimeOffset.UtcNow),
                ["orders", "billing"],
                Ct);

            gate.Joined.SetResult(first);

            await runtime.RestartWakeOf(Machine).WaitAsync(Ct);
            await gate.Leaving.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.True(gate.Asked >= 2, $"the lapse's asking never reached the gate ({gate.Asked})");
            Assert.False(first.Commands.TryRead(out var early), $"nothing could have been asked, but {early} was");

            var stored = await runtime.UseConnectionAsync(
                async (connection, _, ct) => await new AgentRepository(connection).LapsedAsync(runtime.RunId, ct),
                Ct);

            Assert.Equal([Orders], stored);
        }

        // The broker starts again before the node returns: the lapse is read again and paid at the join.
        await using var again = BrokerRuntime.Open(ProjectName.Create("test"), Store, BrokerOptions.Default, TimeProvider.System);

        again.RestartWakeDelay = TimeSpan.Zero;

        var node = await again.JoinNodeAsync(
            new NodeRecord(Machine, null, null, null, null, true, DateTimeOffset.UtcNow),
            ["orders", "billing"],
            Ct);

        try
        {
            await again.RestartWakeOf(Machine).WaitAsync(Ct);

            Assert.True(node.Commands.TryRead(out var command), "the lapse nobody could be asked for was lost with the broker");
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Orders.Value, command.Start.Agent);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    // The first run: billing asks orders something, and the person freezes orders with nothing running.
    private async Task FrozenWithMailAsync()
    {
        await using var runtime = BrokerRuntime.Open(ProjectName.Create("test"), Store, BrokerOptions.Default, TimeProvider.System);

        _ = await runtime.UseConnectionAsync(
            async (connection, now, ct) =>
            {
                foreach (var name in new[] { Orders, Billing })
                {
                    await new AgentRepository(connection).UpsertAsync(
                        new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), ct);
                }

                var broker = new Parley.Broker.Broker(connection);
                await broker.StartAsync(Billing, now, ct);
                var sent = await broker.RequestAsync(
                    Billing, Orders, "claude-opus-5", new JsonObject { ["ask"] = "price?" }.ToJsonString(), now, "pricing", ct);
                Assert.True(sent.Succeeded);

                Assert.Equal(1, await new AgentRepository(connection).FreezeAsync(Orders, now, run: runtime.RunId, cancellationToken: ct));
                return true;
            },
            Ct);
    }

    // Says no every time; at the second asking, the lapse's, the node leaves first.
    private sealed class LeavingAtTheSecondGate : IResumeGate
    {
        private int _asked;
        private Task _leaving = Task.CompletedTask;
        private readonly TaskCompletionSource<bool> _left = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<JoinedNode> Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Asked => Volatile.Read(ref _asked);

        public Task Leaving => _left.Task.ContinueWith(_ => _leaving, TaskScheduler.Default).Unwrap();

        public async ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _asked) == 2)
            {
                var node = await Joined.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                _leaving = node.DisposeAsync().AsTask();
                _left.SetResult(true);
            }

            return false;
        }
    }
}
