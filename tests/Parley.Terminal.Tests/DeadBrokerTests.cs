using Parley.Cli.Tests;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That one project's broker being down stays that project's problem.
/// </summary>
/// <remarks>
/// <para>
/// FR-017 and SC-004. The failure this forbids is the ordinary one: a surface reads its projects
/// in a loop, the second one is unreachable, and the exception takes the whole screen — so a
/// person with four healthy deployments and one stopped broker can see none of them.
/// </para>
/// <para>
/// <b>The timing half is the part that gets skipped.</b> Contained but sequential is still a
/// surface nobody waits for: the cost becomes the sum of the timeouts rather than the longest of
/// them, and a fact that takes twenty seconds to appear is a fact nobody reads. So the dead
/// brokers here are several, and the assertion is on the clock.
/// </para>
/// </remarks>
public sealed class DeadBrokerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public DeadBrokerTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(
                AgentName.Create("orders@test"), "mcp", AgentTier.Pull,
                null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task A_broker_that_is_not_there_is_reported_rather_than_thrown()
    {
        // Port 1 on loopback: reserved, never listening, and it refuses rather than hanging, so
        // this is the "broker is stopped" case and not the "machine is gone" one.
        var read = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(
                new Parley.Transport.Operators.OperatorsClient(
                    BrokerDirectory.Connect("http://127.0.0.1:1")))
                .RegistryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.False(read.Reachable);
        Assert.NotNull(read.Problem);

        // The words matter as much as the containment. A person looking at one red row needs to
        // know the rest of the screen is still true, or they will distrust all of it.
        Assert.Contains("not answering", read.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_dead_broker_leaves_the_live_one_readable()
    {
        var ct = TestContext.Current.CancellationToken;

        var dead = BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(
                new Parley.Transport.Operators.OperatorsClient(
                    BrokerDirectory.Connect("http://127.0.0.1:1")))
                .RegistryAsync(ct).ConfigureAwait(false));

        var live = BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(_broker.Operators).RegistryAsync(ct).ConfigureAwait(false));

        var both = await Task.WhenAll(dead, live);

        Assert.False(both[0].Reachable);
        Assert.True(both[1].Reachable, both[1].Problem);
        Assert.Contains(both[1].Value!, a => a.Name.Value == "orders@test");
    }

    [Fact]
    public async Task Several_dead_brokers_cost_one_timeout_and_not_the_sum_of_them()
    {
        // Five, asked at once. In sequence this would be five refusals end to end; the assertion
        // is deliberately generous because a refused connection is fast and the point is the
        // shape, not the milliseconds -- but SC-004 says five seconds for a project to be
        // reported, and five sequential timeouts would blow that on any machine where the
        // connection hangs rather than refuses.
        var ct = TestContext.Current.CancellationToken;
        var clock = Stopwatch.StartNew();

        var reads = Enumerable.Range(0, 5).Select(_ =>
            BrokerDirectory.ReadAsync(async () =>
                await new BrokerClient(
                    new Parley.Transport.Operators.OperatorsClient(
                        BrokerDirectory.Connect("http://127.0.0.1:1")))
                    .RegistryAsync(ct).ConfigureAwait(false)));

        var answers = await Task.WhenAll(reads);

        clock.Stop();

        Assert.All(answers, a => Assert.False(a.Reachable));
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"five unreachable projects took {clock.Elapsed.TotalSeconds:F1}s, which is SC-004's "
            + "whole budget for one");
    }
}
