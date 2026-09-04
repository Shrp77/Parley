using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// <c>parley project remove --destroy</c>: withdrawal, then the deleting withdrawal leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// Killing a project used to be four steps, and the second was restarting every other project's
/// broker to stop this one's. Now the process serving the manifest stops a removed project on
/// its own, and this command waits for it to have done so before deleting a file the broker
/// still had open — which is the order that matters, and the one a person doing it by hand gets
/// wrong.
/// </para>
/// <para>
/// The catalogue case is the one worth a test of its own: a machine commonly starts agents for
/// several projects from one <c>node.json</c>, and destroying one project must not take the
/// others' catalogue with it.
/// </para>
/// </remarks>
public sealed class ProjectDestroyTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-destroy-" + Guid.NewGuid().ToString("N"));

    public ProjectDestroyTests() => Directory.CreateDirectory(_directory);

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

    private string Manifest => Path.Combine(_directory, "projects.json");

    [Fact]
    public async Task With_nothing_serving_the_store_and_an_unshared_catalogue_are_deleted()
    {
        var catalogue = Path.Combine(_directory, "billing-node.json");
        var billing = Project("billing", catalogue);

        ProjectManifest.Write(Manifest, [billing]);
        await File.WriteAllTextAsync(billing.Store, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(catalogue, "{}", TestContext.Current.CancellationToken);

        var (code, output, error) = await RemoveAsync("billing", destroy: true);

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("Destroyed 'billing'", output, StringComparison.Ordinal);
        Assert.False(File.Exists(billing.Store), "the store survived");
        Assert.False(File.Exists(catalogue), "the catalogue survived");

        Assert.True(ProjectManifest.TryRead(Manifest, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task Another_projects_catalogue_and_store_are_not_touched()
    {
        // The manifest refuses two projects sharing a catalogue, so "another project's" is the
        // only other case, and it must stay exactly where it is.
        var orders = Project("orders", Path.Combine(_directory, "orders-node.json"));
        var billing = Project("billing", Path.Combine(_directory, "billing-node.json"));

        ProjectManifest.Write(Manifest, [orders, billing]);

        foreach (var path in new[] { orders.Store, orders.NodeConfig!, billing.Store, billing.NodeConfig! })
        {
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        }

        var (code, _, _) = await RemoveAsync("billing", destroy: true);

        Assert.Equal(0, code);
        Assert.True(File.Exists(orders.Store), "the other project's store was deleted");
        Assert.True(File.Exists(orders.NodeConfig), "the other project's catalogue was deleted");
        Assert.False(File.Exists(billing.Store));
        Assert.False(File.Exists(billing.NodeConfig));
    }

    [Fact]
    public async Task Without_destroy_nothing_is_deleted_and_the_way_to_is_named()
    {
        var billing = Project("billing", null);

        ProjectManifest.Write(Manifest, [billing]);
        await File.WriteAllTextAsync(billing.Store, string.Empty, TestContext.Current.CancellationToken);

        var (code, output, _) = await RemoveAsync("billing", destroy: false);

        Assert.Equal(0, code);
        Assert.Contains("Nothing was deleted", output, StringComparison.Ordinal);
        Assert.Contains("--destroy", output, StringComparison.Ordinal);
        Assert.True(File.Exists(billing.Store));
    }

    [Fact]
    public async Task While_serving_the_broker_is_stopped_first_and_then_the_store_goes()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders", null);
        var billing = Project("billing", null);

        ProjectManifest.Write(Manifest, [orders, billing]);

        // The real reread: the manifest file, when its stamp moves, exactly as the entry point
        // hands it to the command.
        var stamp = File.GetLastWriteTimeUtc(Manifest);

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billing],
            null,
            output,
            TextWriter.Null,
            reread: () =>
            {
                var now = File.GetLastWriteTimeUtc(Manifest);

                if (now == stamp)
                {
                    return null;
                }

                stamp = now;

                return ProjectManifest.TryRead(Manifest, out var current, out _) ? current : null;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(billing.Endpoint, ct), "the project never came up");
            Assert.True(File.Exists(billing.Store), "the broker created no store");

            var (code, said, error) = await RemoveAsync("billing", destroy: true);

            Assert.True(code == 0, $"exit {code}: {error}");
            Assert.Contains("Destroyed 'billing'", said, StringComparison.Ordinal);
            Assert.False(File.Exists(billing.Store), "the store survived");

            // The other project was not touched.
            Assert.True(await AnswersAsync(orders.Endpoint, ct, attempts: 5), "destroying one stopped the other");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        _ = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Contains("billing: removed from the manifest; stopping its broker", output.ToString(), StringComparison.Ordinal);
    }

    private async Task<(int Code, string Out, string Error)> RemoveAsync(string name, bool destroy)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["remove", name],
            Manifest,
            new ProjectFields(null, null, null, null),
            output,
            error,
            TestContext.Current.CancellationToken,
            destroy: destroy);

        return (code, output.ToString(), error.ToString());
    }

    private ProjectSettings Project(string name, string? catalogue) =>
        new(
            ProjectName.Create(name),
            Path.Combine(_directory, name + ".db"),
            $"http://127.0.0.1:{FreePort()}",
            catalogue,
            null);

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

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}
