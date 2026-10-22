using Parley.Core;
using Parley.Configuration;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That stopping a broker which hosts a node stops the node, and says so honestly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ShutdownTests"/> asserts the wiring by reading the entry point, because sending a
/// genuine CTRL_C_EVENT needs a console group a test host does not have to spare. That check is
/// worth keeping and is not this one: it proves the call sits inside the interrupt's scope, and
/// proves nothing about what happens when the token is actually pulled.
/// </para>
/// <para>
/// What happens matters because the node is hosted in the broker's process. A node holds
/// the agents it started (SPEC.md §11.1), and one token ends both, so a hosted node that ignored it — or a broker that
/// stopped without waiting for it — would leave residents behind with nobody to tell them to
/// quit.
/// </para>
/// </remarks>
public sealed class HostedNodeCancellationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-hosted-stop-" + Guid.NewGuid().ToString("N"));

    public HostedNodeCancellationTests() => Directory.CreateDirectory(_directory);

    /// <summary>The one project this rig serves, on a port the operating system picks.</summary>
    /// <remarks>
    /// Port zero rather than a claimed one: `PortLedger` reconciles within a root and every test class
    /// has its own, so two classes would both be handed the bottom of the band and one would fail to
    /// listen. See BrokerProcess, which rests on the same thing.
    /// </remarks>
    private ProjectSettings Project() =>
        new(
            Parley.Core.ProjectName.Create("probe"),
            ProjectFolder.StoreFor(_directory, Parley.Core.ProjectName.Create("probe")),
            "http://127.0.0.1:0",
            null);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Stopping_the_broker_stops_the_node_it_hosts()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = new CancellationTokenSource();

        var listening = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();

        // Stands in for the node: it is handed the same token the broker is stopped with, and
        // records whether that token ever reached it.
        async Task<int> Hosting(
            IReadOnlyList<string> addresses, TextWriter reporting, CancellationToken token)
        {
            await reporting.WriteLineAsync("hosted").ConfigureAwait(false);
            listening.SetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stopped.SetResult();
                throw;
            }

            return 0;
        }

        var serving = ServeCommand.RunAsync(
            [Project()],
            null,
            output,
            error,
            (_, addresses, reporting, token) => Hosting(addresses, reporting, token),
            cancellationToken: stopping.Token);

        // The hook runs only once the port is open, which is the ordering the whole callback
        // exists for: a node that raced the broker would report "no broker is listening".
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await stopping.CancelAsync();

        // Returns rather than throwing, which is the contract the sweep already sets: it catches
        // the cancellation itself, on the grounds that Ctrl+C is how this command is meant to end
        // rather than a fault. A hosted node has to agree with that or an ordinary stop would
        // exit as a crash.
        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, code);

        // Already completed by the time serving returned, not merely completed eventually. That
        // ordering is the property worth having: the broker's shutdown waits for the node to
        // finish sending its agents away, and a process that exited first would leave them
        // running with nobody left to tell them to quit.
        Assert.True(stopped.Task.IsCompletedSuccessfully, "the interrupt never reached the hosted node");
        Assert.Contains("hosted", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_that_fails_does_not_take_the_broker_down_with_it()
    {
        // Containment, from the outside. Hosting is a deployment convenience and must not become
        // a way for one machine's supervision problem to end a process every agent in the
        // deployment is connected to. The broker keeps serving until it is stopped, and the exit
        // code still reports that the run did less than it claimed.
        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = new CancellationTokenSource();

        var failed = new TaskCompletionSource();

        Task<int> Hosting(IReadOnlyList<string> addresses, TextWriter reporting, CancellationToken token)
        {
            failed.SetResult();
            throw new InvalidOperationException("the node fell over");
        }

        var serving = ServeCommand.RunAsync(
            [Project()],
            null,
            output,
            error,
            (_, addresses, reporting, token) => Hosting(addresses, reporting, token),
            cancellationToken: stopping.Token);

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Still serving: the sweep runs until stopped, so the task is not finished by the node
        // having thrown.
        Assert.False(serving.IsCompleted, "the broker ended when the node it hosts threw");

        await stopping.CancelAsync();

        try
        {
            _ = await serving;
        }
        catch (OperationCanceledException)
        {
            // The interrupt, which is how a broker ends.
        }

        Assert.Contains("the node fell over", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("broker is still serving", output.ToString(), StringComparison.Ordinal);
    }
}
