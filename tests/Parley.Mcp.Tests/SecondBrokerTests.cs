using Parley.Broker;
using Parley.Core;
using Parley.Server;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// A second broker for a store that is already being served changes nothing about it.
/// </summary>
/// <remarks>
/// <para>
/// A scheduled task and a
/// terminal restoring its last session both started <c>parley serve</c> at logon. Every broker clears
/// the <c>connected</c> flags it finds when it opens a store, because a broker that has just started
/// holds no connections; the one that then failed to bind its port had already cleared the flags of
/// the one that had not. The live node's stream never ended, so it never rejoined, and seventeen agents
/// read as stranded on the machine running them — in the terminal, in the web UI, and in the roster
/// their peers read.
/// </para>
/// <para>
/// The probe read <c>Startable</c> before the second open and <c>Stranded</c> after it, with the live
/// runtime still holding its node. The first test is that probe with its assertion turned the right way
/// round.
/// </para>
/// </remarks>
public sealed class SecondBrokerTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@probe");

    private static readonly ProjectName Probe = ProjectName.Create("probe");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-second-" + Guid.NewGuid().ToString("N"));

    public SecondBrokerTests() => Directory.CreateDirectory(_directory);

    private string Store => Path.Combine(_directory, "parley.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this suite.
        }
    }

    [Fact]
    public async Task A_second_broker_for_a_served_store_leaves_its_live_node_connected()
    {
        var token = TestContext.Current.CancellationToken;

        var live = BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System);

        try
        {
            _ = await live.UseConnectionAsync(
                async (connection, _, ct) => await new AgentRepository(connection).UpsertAsync(
                    new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                    ct),
                token);

            _ = await live.JoinNodeAsync(
                new NodeRecord("probe", null, null, null, null, true, DateTimeOffset.UtcNow),
                ["orders"],
                token);

            Assert.Equal(AgentPresence.Startable, await PresenceAsync(live, Orders, token));

            _ = Assert.Throws<StoreHeldException>(
                () => BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System));

            // The whole defect: this read Stranded.
            Assert.Equal(AgentPresence.Startable, await PresenceAsync(live, Orders, token));
        }
        finally
        {
            await live.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_broker_stopping_hands_its_store_to_the_next()
    {
        // A broker restarted after the previous one stopped. Disposal has to release the claim, or
        // the restart is refused over a store nobody is serving.
        var first = BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System);

        await first.DisposeAsync();

        await using var second = BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task The_refused_broker_is_told_which_process_is_serving()
    {
        await using var live = BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System);

        var refused = Assert.Throws<StoreHeldException>(
            () => BrokerRuntime.Open(Probe, Store, BrokerOptions.Default, TimeProvider.System));

        Assert.Equal(Environment.ProcessId, refused.Owner?.ProcessId);
    }

    private static async Task<AgentPresence> PresenceAsync(
        BrokerRuntime runtime, AgentName who, CancellationToken token) =>
        await runtime.UseConnectionAsync(
            async (connection, _, ct) =>
                (await new AgentRepository(connection).GetAllAsync(ct)).Single(a => a.Name == who).Presence,
            token);
}
