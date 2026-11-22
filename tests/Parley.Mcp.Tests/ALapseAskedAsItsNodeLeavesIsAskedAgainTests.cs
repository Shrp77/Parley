using System.Text.Json.Nodes;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the start a lapsed freeze owes is asked for again at the machine's next join when its node leaves as it is asked,
/// so the asking comes back unreachable (SPEC.md §14).
/// </summary>
/// <remarks>
/// The lapse is taken off the broker's list at the join and paid from the delayed wake, which checks that the node is still
/// there before it asks. A node that leaves between that check and the asking makes the wake come back unreachable, and the
/// lapse must be given back for the next join; otherwise the agent's mail waits for something unrelated to arrive. Review,
/// lens 3 on 6257cf80: that give-back passed every test with it removed.
/// </remarks>
[Integration]
public sealed class ALapseAskedAsItsNodeLeavesIsAskedAgainTests : IDisposable
{
    private const string Machine = "probe";

    private static readonly AgentName Orders = AgentName.Create("orders@" + Machine);
    private static readonly AgentName Billing = AgentName.Create("billing@" + Machine);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-node-leaves").FullName;

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
    public async Task A_lapse_whose_node_left_as_it_was_asked_is_paid_at_the_next_join()
    {
        await FrozenWithMailAsync();

        // The restart wake asks the gate first and is told no; the lapse's asking is the second, and the node leaves there,
        // after the wake has seen it joined and before it asks.
        var gate = new LeavingAtTheSecondGate();

        await using var runtime = BrokerRuntime.Open(
            ProjectName.Create("test"),
            Store,
            BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always, ResumeGate = gate },
            TimeProvider.System);

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

        var second = await runtime.JoinNodeAsync(
            new NodeRecord(Machine, null, null, null, null, true, DateTimeOffset.UtcNow),
            ["orders", "billing"],
            Ct);

        try
        {
            await runtime.RestartWakeOf(Machine).WaitAsync(Ct);

            Assert.True(second.Commands.TryRead(out var command), "the lapse whose asking found no node was not asked for again");
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Orders.Value, command.Start.Agent);
            Assert.Contains("freeze lapsed", command.Start.Because, StringComparison.Ordinal);
        }
        finally
        {
            await second.DisposeAsync();
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

    // Says no every time; at the second asking, the lapse's, the node leaves first. Its leaving is not awaited here: the
    // release writes to the store, which the asking holds, but it lets go of the machine before that.
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
