using System.Collections.Concurrent;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// A process serving several projects hosts a node in each of them.
/// </summary>
/// <remarks>
/// The rule this replaces refused a node across several projects, on the grounds that a machine
/// has one identity and the process could not choose which deployment it belonged to. The
/// manifest's per-project catalogue is what makes the choice, so the hook is now handed each
/// project in turn — and this checks it is handed <i>each</i>, because a hook run once against
/// the first started broker would be the old behaviour wearing the new signature.
/// </remarks>
public sealed class NodePerProjectTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-node-per-project-" + Guid.NewGuid().ToString("N"));

    public NodePerProjectTests() => Directory.CreateDirectory(_directory);

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
    public async Task Every_started_project_is_offered_the_node()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        var offered = new ConcurrentBag<string>();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billing],
            null,
            TextWriter.Null,
            TextWriter.Null,
            nodeFor: (project, addresses, _, _) =>
            {
                Assert.NotEmpty(addresses);
                offered.Add(project.Name.Value);

                return Task.FromResult(0);
            },
            cancellationToken: stopping.Token);

        try
        {
            for (var attempt = 0; attempt < 100 && offered.Count < 2; attempt++)
            {
                await Task.Delay(100, ct);
            }
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
        Assert.Contains("orders", offered);
        Assert.Contains("billing", offered);
    }

    private ProjectSettings Project(string name) =>
        new(
            ProjectName.Create(name),
            Path.Combine(_directory, name + ".db"),
            $"http://127.0.0.1:{FreePort()}",
            null,
            null);
}
