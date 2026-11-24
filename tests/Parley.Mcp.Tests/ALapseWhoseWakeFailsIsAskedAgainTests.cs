using System.Text.Json.Nodes;
using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Mcp.Tests;

/// <summary>
/// That the start a lapsed freeze owes is asked for again at the machine's next join when the asking fails (SPEC.md §14).
/// </summary>
/// <remarks>
/// The lapse is taken off the broker's list at the join and paid from the delayed wake. A read that fails there (the resume
/// gate throws, the store is busy) must give it back, as the restart wake gives its machine back; otherwise the agent's
/// mail waits for something unrelated to arrive. Review, lens 3 run 1 on 6fde4ef3: the give-back passed every test with it
/// removed.
/// </remarks>
[Integration]
public sealed class ALapseWhoseWakeFailsIsAskedAgainTests : IDisposable
{
    private const string Machine = "probe";

    private static readonly AgentName Orders = AgentName.Create("orders@" + Machine);
    private static readonly AgentName Billing = AgentName.Create("billing@" + Machine);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-fails").FullName;

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
    public async Task A_lapse_whose_wake_failed_is_paid_at_the_next_join()
    {
        await FrozenWithMailAsync();

        // The restart wake asks the gate first, about the Request orders owes, and is told no; the lapse's asking is the
        // second, and fails.
        var gate = new FailingSecondGate();

        await using var runtime = BrokerRuntime.Open(
            ProjectName.Create("test"),
            Store,
            BrokerOptions.Default with { ResumePolicy = ResumePolicy.Always, ResumeGate = gate },
            TimeProvider.System);

        runtime.RestartWakeDelay = TimeSpan.Zero;

        var first = await JoinAsync(runtime);

        Assert.Equal(2, gate.Asked);
        Assert.False(first.Commands.TryRead(out var early), $"nothing should have been asked yet, but {early} was");

        await first.DisposeAsync();

        var second = await JoinAsync(runtime);

        try
        {
            Assert.True(second.Commands.TryRead(out var command), "the lapse that failed was not asked for again");
            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Start, command.CommandCase);
            Assert.Equal(Orders.Value, command.Start.Agent);
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

    // Waits for the wakes the join scheduled, which run beside the join rather than inside it.
    private static async Task<JoinedNode> JoinAsync(BrokerRuntime runtime)
    {
        var joined = await runtime.JoinNodeAsync(
            new NodeRecord(Machine, null, null, null, null, true, DateTimeOffset.UtcNow),
            ["orders", "billing"],
            Ct);

        await runtime.RestartWakeOf(Machine).WaitAsync(Ct);

        return joined;
    }

    private sealed class FailingSecondGate : IResumeGate
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public ValueTask<bool> MayResumeAsync(Exchange exchange, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _asked) == 2
                ? throw new InvalidOperationException("the store was busy")
                : ValueTask.FromResult(false);
    }
}
