using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A node that leaves and joins again at once is read as connected afterwards (SPEC.md §4.0).
/// </summary>
/// <remarks>
/// A node cancels its own stream when its catalogue changes, which enrolling a repository does, and joins again at
/// once. The old stream's release removed the node, then wrote the machine's leave behind the gate; the new join could
/// be added and write its join in between, and the leave landing after it read every agent on the machine as stranded
/// until the node next rejoined. Found by review, lens 1 run 85: wrong in over a thousand of three thousand rounds.
/// </remarks>
public sealed class ANodeThatRejoinsAtOnceStaysConnectedTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@probe");

    private const int Rounds = 300;

    [Fact]
    public async Task A_leave_racing_the_next_join_never_reads_the_machine_as_gone()
    {
        var token = TestContext.Current.CancellationToken;

        await using var runtime = BrokerRuntime.OpenInMemory(BrokerOptions.Default, TimeProvider.System);
        _ = await runtime.UseConnectionAsync(
            async (connection, _, ct) => await new AgentRepository(connection).UpsertAsync(
                new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                ct),
            token);

        var node = await JoinAsync(runtime, token);
        var wrong = 0;

        for (var round = 0; round < Rounds; round++)
        {
            using var start = new Barrier(2);
            var old = node;

            var leaving = Task.Run(
                async () =>
                {
                    start.SignalAndWait(token);
                    await old.DisposeAsync();
                },
                token);

            var joining = Task.Run(
                async () =>
                {
                    start.SignalAndWait(token);

                    // As the node does: refused while the old join is still held, and tried again.
                    while (true)
                    {
                        try
                        {
                            return await JoinAsync(runtime, token);
                        }
                        catch (InvalidOperationException)
                        {
                            await Task.Yield();
                        }
                    }
                },
                token);

            await leaving;
            node = await joining;

            if (await PresenceAsync(runtime, token) != AgentPresence.Startable)
            {
                wrong++;
            }
        }

        await node.DisposeAsync();

        Assert.True(wrong == 0, $"the machine read as gone after {wrong} of {Rounds} rejoins while its node was joined");
    }

    private static Task<JoinedNode> JoinAsync(BrokerRuntime runtime, CancellationToken token) =>
        runtime.JoinNodeAsync(
            new NodeRecord("probe", null, null, null, null, true, DateTimeOffset.UtcNow),
            ["orders"],
            token);

    private static async Task<AgentPresence> PresenceAsync(BrokerRuntime runtime, CancellationToken token) =>
        await runtime.UseConnectionAsync(
            async (connection, _, ct) =>
                (await new AgentRepository(connection).GetAllAsync(ct)).Single(a => a.Name == Orders).Presence,
            token);
}
