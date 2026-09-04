using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// A project named while the process is serving gets its broker without a restart.
/// </summary>
/// <remarks>
/// SPEC.md §4.1: every project has its own broker, and one process may host them all. The
/// process read the manifest once at start, so a project named from the terminal was "not
/// answering" until the next logon — and the first person to create one from the form said, in
/// so many words, that creating a project means wanting its broker running now.
/// </remarks>
public sealed class ManifestWatchTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-watch-" + Guid.NewGuid().ToString("N"));

    public ManifestWatchTests() => Directory.CreateDirectory(_directory);

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
    public async Task A_project_named_while_serving_is_started_within_seconds()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        // What the manifest says now. Starts with one project; the second is named while the
        // process runs, the way the form names one.
        var manifest = new List<ProjectSettings> { orders };
        var changed = false;

        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            output,
            error,
            reread: () =>
            {
                if (!changed)
                {
                    return null;
                }

                changed = false;

                return manifest;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "the first project never came up");
            Assert.False(await AnswersAsync(billing.Endpoint, ct, attempts: 3), "the second project was up before it was named");

            manifest.Add(billing);
            changed = true;

            Assert.True(await AnswersAsync(billing.Endpoint, ct), "the project named while serving never came up");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
        Assert.Contains("billing: named in the manifest since this started", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("billing on http://", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());

        // Both ports are free again: the project started late was stopped with the rest.
        Assert.True(CanBind(orders.Endpoint));
        Assert.True(CanBind(billing.Endpoint));
    }

    [Fact]
    public async Task A_project_that_will_not_start_is_tried_once_per_change_rather_than_every_tick()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");

        var brokenStore = Path.Combine(_directory, "broken-store");
        Directory.CreateDirectory(brokenStore);

        var broken = new ProjectSettings(
            ProjectName.Create("broken"), brokenStore, $"http://127.0.0.1:{FreePort()}", null, null);

        var reads = 0;

        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Every tick reports the same manifest as changed, which is the worst case for a watcher
        // that retried whatever had failed: the failure line would be written every two seconds.
        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            output,
            error,
            reread: () =>
            {
                reads++;
                return [orders, broken];
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            // Long enough for several ticks.
            for (var waited = 0; reads < 3 && waited < 100; waited++)
            {
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(reads >= 3, "the watcher did not tick");

        var failures = error.ToString().Split('\n').Count(l => l.Contains("broken: could not open", StringComparison.Ordinal));

        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task A_project_removed_from_the_manifest_is_stopped_and_its_store_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        var manifest = new List<ProjectSettings> { orders, billing };
        var changed = false;

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billing],
            null,
            output,
            TextWriter.Null,
            reread: () =>
            {
                if (!changed)
                {
                    return null;
                }

                changed = false;

                return manifest;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(billing.Endpoint, ct), "the second project never came up");

            _ = manifest.Remove(billing);
            changed = true;

            // Gone within a few ticks: the port can be bound again, so the broker let go of it.
            var freed = false;

            for (var attempt = 0; attempt < 100 && !freed; attempt++)
            {
                await Task.Delay(100, ct);
                freed = CanBind(billing.Endpoint);
            }

            Assert.True(freed, "the removed project's broker kept its port");

            // And the other one is untouched by it.
            Assert.True(await AnswersAsync(orders.Endpoint, ct, attempts: 5), "stopping one project stopped the other");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
        Assert.Contains("billing: removed from the manifest; stopping its broker", output.ToString(), StringComparison.Ordinal);

        // Stopping is not deleting. The store is exactly where it was.
        Assert.True(File.Exists(billing.Store), "removal deleted the store");
    }

    private static async Task<bool> AnswersAsync(string endpoint, CancellationToken cancellationToken, int attempts = 80)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using var channel = GrpcChannel.ForAddress(endpoint);
                await channel.ConnectAsync(timeout.Token);

                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return false;
    }

    private static bool CanBind(string endpoint)
    {
        var port = new Uri(endpoint).Port;

        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private ProjectSettings Project(string name) =>
        new(
            ProjectName.Create(name),
            Path.Combine(_directory, name + ".db"),
            $"http://127.0.0.1:{FreePort()}",
            null,
            null);
}
