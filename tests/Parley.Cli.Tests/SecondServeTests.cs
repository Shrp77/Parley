using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// A second <c>serve</c> for a project already being served says so and exits, touching nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Starting a broker more than once is ordinary, and a machine can do it at every logon.</b>
/// A scheduled task and a terminal restoring its last session both ran <c>parley serve</c> with the
/// same arguments, and the one that lost cleared the winner's liveness on its way out. The claim on
/// the store stops the damage; these check what the losing process then does: names the process that
/// is serving, starts nothing beside it, and exits 0 — a duplicate launch is not a failure, and an exit
/// code saying it was would put a red mark against a deployment that is working.
/// </para>
/// <para>
/// A second broker does not stand by to take over when the winner stops: an idle second broker is a
/// process nobody expects. The cost is written in CLAUDE.md.
/// </para>
/// <para>
/// In one process, as two calls, which is faithful: the claim is a file opened with no sharing, and it
/// refuses a second open whoever makes it.
/// </para>
/// </remarks>
[Integration]
public sealed class SecondServeTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-second-serve-" + Guid.NewGuid().ToString("N"));

    public SecondServeTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>One project at a chosen endpoint, in a throwaway root.</summary>
    /// <remarks>
    /// `serve` takes projects now: a store is derived from a project's name and its endpoint is its own,
    /// so a rig names a root and writes a project into it rather than passing two paths.
    /// </remarks>
    private static ProjectSettings At(string root, string endpoint, string name = "probe") =>
        new(
            Parley.Core.ProjectName.Create(name),
            ProjectFolder.StoreFor(root, Parley.Core.ProjectName.Create(name)),
            endpoint,
            null);

    [Fact]
    public async Task A_second_serve_for_a_served_project_names_the_server_and_exits_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");

        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var first = ServeCommand.RunAsync([orders], null, TextWriter.Null, TextWriter.Null, cancellationToken: stopFirst.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "the first broker never came up");

            var said = new StringWriter();
            var nodes = 0;
            var webUis = 0;

            var code = await ServeCommand.RunAsync(
                [orders],
                null,
                said,
                TextWriter.Null,
                nodeFor: (_, _, _, _) =>
                {
                    _ = Interlocked.Increment(ref nodes);

                    return Task.FromResult(0);
                },
                alongside: (_, _, _) =>
                {
                    _ = Interlocked.Increment(ref webUis);

                    return Task.FromResult(0);
                },
                cancellationToken: ct).WaitAsync(Patience, ct);

            Assert.Equal(0, code);
            Assert.Contains("already being served by another Parley broker", said.ToString(), StringComparison.Ordinal);
            Assert.Contains($"process {Environment.ProcessId}", said.ToString(), StringComparison.Ordinal);

            // A node here would be a second supervisor for the machine, and a web UI a fight for its port.
            Assert.Equal(0, nodes);
            Assert.Equal(0, webUis);

            Assert.True(await AnswersAsync(orders.Endpoint, ct, attempts: 3), "the second serve disturbed the broker that was serving");
        }
        finally
        {
            await stopFirst.CancelAsync();
        }

        Assert.Equal(0, await first.WaitAsync(Patience, ct));
    }

    [Fact]
    public async Task A_held_project_does_not_stop_the_others_being_served()
    {
        // One process serving several projects, one of which somebody else already serves. §4.1 keeps
        // one project's trouble from reaching the rest, and being held is not even trouble.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopSecond = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var first = ServeCommand.RunAsync([orders], null, TextWriter.Null, TextWriter.Null, cancellationToken: stopFirst.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            var said = new StringWriter();
            var second = ServeCommand.RunAsync([orders, billing], null, said, TextWriter.Null, cancellationToken: stopSecond.Token);

            try
            {
                Assert.True(await AnswersAsync(billing.Endpoint, ct), "the project nobody else serves never came up");
                Assert.Contains("orders: its store", said.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("did not start", said.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                await stopSecond.CancelAsync();
            }

            // Held is not failed, so the exit code at the end does not count it.
            Assert.Equal(0, await second.WaitAsync(Patience, ct));
        }
        finally
        {
            await stopFirst.CancelAsync();
        }

        Assert.Equal(0, await first.WaitAsync(Patience, ct));
    }

    [Fact]
    public async Task The_single_store_form_exits_cleanly_too()
    {
        // `parley serve --db` is what a person types into a terminal, and a terminal that restores its
        // session types it again.
        var ct = TestContext.Current.CancellationToken;

        // **One project, served twice.** It was one store served twice; the property is the same and
        // the second broker must still decline rather than clear the first one's liveness.
        var endpoint = $"http://127.0.0.1:{FreePort()}";
        var project = At(_directory, endpoint);

        using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var first = ServeCommand.RunAsync(
            [project], null, TextWriter.Null, TextWriter.Null, cancellationToken: stopFirst.Token);

        try
        {
            Assert.True(await AnswersAsync(endpoint, ct));

            var said = new StringWriter();

            var code = await ServeCommand.RunAsync(
                [project], null, said, TextWriter.Null, cancellationToken: ct)
                .WaitAsync(Patience, ct);

            Assert.Equal(0, code);
            Assert.Contains("already being served by another Parley broker", said.ToString(), StringComparison.Ordinal);
            Assert.True(await AnswersAsync(endpoint, ct, attempts: 3), "the second serve disturbed the broker that was serving");
        }
        finally
        {
            await stopFirst.CancelAsync();
        }

        _ = await first.WaitAsync(Patience, ct);
    }

    [Fact]
    public async Task A_serve_started_after_the_last_one_stopped_serves()
    {
        // The claim is released with the broker, so a restart is not refused over a store nobody holds.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");

        using (var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var first = ServeCommand.RunAsync([orders], null, TextWriter.Null, TextWriter.Null, cancellationToken: stopFirst.Token);

            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            await stopFirst.CancelAsync();
            Assert.Equal(0, await first.WaitAsync(Patience, ct));
        }

        using var stopSecond = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var said = new StringWriter();
        var second = ServeCommand.RunAsync([orders], null, said, TextWriter.Null, cancellationToken: stopSecond.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct), "a restart was refused over a store nobody held");
            Assert.DoesNotContain("already being served", said.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await stopSecond.CancelAsync();
        }

        Assert.Equal(0, await second.WaitAsync(Patience, ct));
    }

    private ProjectSettings Project(string name) =>
        new(
            ProjectName.Create(name),
            Path.Combine(_directory, name + ".db"),
            $"http://127.0.0.1:{FreePort()}",
            null);
}
