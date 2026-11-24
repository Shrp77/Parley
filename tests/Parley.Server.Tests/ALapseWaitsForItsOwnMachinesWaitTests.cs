using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A lapse is taken only by the wake of its own machine's node: one owed on another machine waits for that machine's node to
/// have waited too, so a process left running there has had the wait to attach again (SPEC.md §14, §11.1).
/// </summary>
/// <remarks>
/// <para>
/// The machine comparison in <c>LapsedOn</c> passed every test with it removed: a machine whose node joined while another
/// machine's wake waited was asked for its agent's start at the end of the other machine's wait, before its own (review,
/// lens 1 on 44c8e1e6).
/// </para>
/// </remarks>
[Integration]
public sealed class ALapseWaitsForItsOwnMachinesWaitTests : IDisposable
{
    private const string Docs = "docs@m9";
    private const string Orders = "orders@m1";
    private const string Billing = "billing@m2";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(300);

    private readonly string _folder = Directory.CreateTempSubdirectory("parley-lapse-own-machine").FullName;

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
    public async Task A_node_that_joins_inside_another_machines_wait_is_asked_only_after_its_own()
    {
        await using (var first = await LoopbackBroker.StartOnAsync(Store, Ct))
        {
            await using var docs = await AttachedClient.AttachAsync(first, Docs, cancellationToken: Ct);

            foreach (var agent in new[] { Orders, Billing })
            {
                await using (await AttachedClient.AttachAsync(first, agent, cancellationToken: Ct))
                {
                }

                await first.WaitUntilDetachedAsync(agent, Patience);
                Assert.True((await first.ConnectAsOperator().FreezeAsync(new Wire.FreezeRequest { Agent = agent }, cancellationToken: Ct)).Changed);
                Assert.Equal(
                    Wire.WakeOutcome.Frozen,
                    Assert.Single((await docs.Client.RequestAsync(Requests.Open(Docs, agent, "while frozen", "c-" + agent.Replace('@', '-')), cancellationToken: Ct)).Outcomes).Wake);
            }
        }

        await using var second = await LoopbackBroker.StartOnAsync(Store, Ct, restartWakeDelay: Wait);
        await using var one = await JoinedNodeClient.JoinAsync(second, "m1", ["orders"], Ct, reportsTurns: true, waitForRestartWake: false);

        // Halfway through m1's wait, m2 joins: its own wait ends a whole half after m1's.
        var m1Wake = second.Runtime.RestartWakeOf("m1");
        Assert.False(await Task.WhenAny(m1Wake, Task.Delay(Wait / 2, Ct)) == m1Wake, "m1's wake ended before m2 joined");
        await using var two = await JoinedNodeClient.JoinAsync(second, "m2", ["billing"], Ct, reportsTurns: true, waitForRestartWake: false);

        await m1Wake.WaitAsync(Patience, Ct);
        Assert.Equal(1, await one.CountCommandsAsync(1, Briefly, Patience));
        Assert.Equal(0, await two.CountCommandsAsync(Briefly));

        await second.Runtime.RestartWakeOf("m2").WaitAsync(Patience, Ct);
        Assert.Equal(1, await two.CountCommandsAsync(1, Briefly, Patience));
        Assert.Equal(Billing, two.Commands[0].Start.Agent);
    }
}
