using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

namespace Parley.Cli.Tests;

/// <summary>
/// One process, several brokers.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.1 permits a process to host many brokers, and this is the command that takes it up.
/// Isolation is not what these tests are about — that lives in the storage layer, separate files
/// and separate connections, and is the same whether one process or three hold them. What is new
/// here is everything the process boundary now shares: one exit code covering several outcomes,
/// one interrupt that has to reach every host, and one pair of writers that several sweeps write
/// to at once.
/// </para>
/// <para>
/// <b>The interrupt case is a regression guard, not a feature test.</b> The defect it descends
/// from is one commit old: <c>serve</c> ignored Ctrl+C because the token it needed was never
/// passed to the loop that waits on it, while the console lifetime a <c>WebApplication</c>
/// installs quietly answered the key with <c>Cancel = true</c>. That was one host. This is N of
/// them, each installing its own, and the shape of the failure — a signal absorbed by something
/// that is not going to act on it — is available again at a level nothing was watching.
/// <see cref="ShutdownTests"/> guards the wiring in the entry point; this guards what the command
/// does with the token once it has one.
/// </para>
/// </remarks>
public sealed class MultiProjectServeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-cli-projects-" + Guid.NewGuid().ToString("N"));

    public MultiProjectServeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as elsewhere in this suite: a pooled SQLite connection can outlive the
            // test, and clearing the pools is process-global.
        }
    }

    private ProjectSettings Project(string name, string? store = null) =>
        new(
            ProjectName.Create(name),
            store ?? Path.Combine(_directory, name + ".db"),
            $"http://127.0.0.1:{FreePort()}",
            null);

    /// <summary>
    /// A project whose store cannot be opened, because the path is a directory.
    /// </summary>
    /// <param name="name">What to call it.</param>
    /// <returns>The settings.</returns>
    /// <remarks>
    /// A directory rather than a locked or malformed file, because it fails the same way on every
    /// platform this suite runs on and needs no cleanup of its own.
    /// </remarks>
    private ProjectSettings Unopenable(string name)
    {
        var path = Path.Combine(_directory, name + "-store");
        Directory.CreateDirectory(path);

        return Project(name, path);
    }

    /// <summary>
    /// Serves the given projects, waits for the caller's check, then stops.
    /// </summary>
    /// <param name="projects">What to serve.</param>
    /// <param name="whileRunning">Run once every started broker has had its chance to come up.</param>
    /// <returns>The exit code, what was printed, and what was reported as wrong.</returns>
    private static async Task<(int Code, string Output, string Error)> ServeAsync(
        IReadOnlyList<ProjectSettings> projects,
        Func<Task> whileRunning)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = new CancellationTokenSource();

        // The token by name, because the overload beside this one takes strings in these
        // positions: passing it positionally binds to whichever signature happens to fit.
        var serving = ServeCommand.RunAsync(
            projects, null, output, error, cancellationToken: stopping.Token);

        try
        {
            await whileRunning();
        }
        finally
        {
            // The single interrupt. Everything the command started has to come down from this,
            // and the test's own wait on `serving` is what proves it did.
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Two_projects_are_both_served_from_one_process()
    {
        var orders = Project("orders");
        var billing = Project("billing");

        var reachable = (Orders: false, Billing: false);

        var (code, output, error) = await ServeAsync([orders, billing], async () =>
        {
            reachable.Orders = await AnswersAsync(orders.Endpoint, TestContext.Current.CancellationToken);
            reachable.Billing = await AnswersAsync(billing.Endpoint, TestContext.Current.CancellationToken);
        });

        Assert.True(reachable.Orders, $"nothing answered on {orders.Endpoint}");
        Assert.True(reachable.Billing, $"nothing answered on {billing.Endpoint}");

        // Each named with its own store, because the report is how an operator tells which broker
        // is which — and two brokers reporting the same path is the mistake worth catching.
        Assert.Contains($"orders on {orders.Endpoint}", output, StringComparison.Ordinal);
        Assert.Contains($"billing on {billing.Endpoint}", output, StringComparison.Ordinal);
        Assert.Contains(orders.Store, output, StringComparison.Ordinal);
        Assert.Contains(billing.Store, output, StringComparison.Ordinal);

        Assert.True(File.Exists(orders.Store), "orders' store was not created");
        Assert.True(File.Exists(billing.Store), "billing's store was not created");

        Assert.Equal(string.Empty, error);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task The_versions_are_named_once_for_the_process_and_not_once_per_project()
    {
        // One build serves every project here, so a line repeated per project would read as
        // though it could differ — and an operator comparing two of them would be answering a
        // question that cannot arise. Counted rather than merely found, because "contains" passes
        // just as happily on the version this is written to prevent.
        var orders = Project("orders");
        var billing = Project("billing");

        var (code, output, _) = await ServeAsync([orders, billing], async () =>
        {
            Assert.True(await AnswersAsync(orders.Endpoint, TestContext.Current.CancellationToken));
            Assert.True(await AnswersAsync(billing.Endpoint, TestContext.Current.CancellationToken));
        });

        var lines = output
            .Split('\n')
            .Count(line => line.TrimEnd('\r').StartsWith("parley ", StringComparison.Ordinal));

        Assert.Equal(1, lines);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task One_interrupt_stops_every_broker_and_frees_every_port()
    {
        // The regression guard. Each WebApplication installs a console lifetime that answers the
        // key itself, so N projects mean N handlers and one signal; a broker still holding a port
        // after the process was told to stop is the defect fixed one commit ago, one level up.
        var orders = Project("orders");
        var billing = Project("billing");

        var (code, _, _) = await ServeAsync([orders, billing], async () =>
        {
            Assert.True(await AnswersAsync(orders.Endpoint, TestContext.Current.CancellationToken));
            Assert.True(await AnswersAsync(billing.Endpoint, TestContext.Current.CancellationToken));
        });

        Assert.Equal(0, code);

        Assert.True(CanBind(orders.Endpoint), $"{orders.Endpoint} is still held after shutdown");
        Assert.True(CanBind(billing.Endpoint), $"{billing.Endpoint} is still held after shutdown");
    }

    [Fact]
    public async Task A_project_that_cannot_start_does_not_stop_the_others()
    {
        var orders = Project("orders");
        var broken = Unopenable("billing");

        var reachable = false;

        var (code, output, error) = await ServeAsync([broken, orders], async () =>
            reachable = await AnswersAsync(orders.Endpoint, TestContext.Current.CancellationToken));

        Assert.True(reachable, "the healthy project stopped serving because another failed");

        // Named with its cause. "One project did not start" sends an operator to read logs that
        // do not say which.
        Assert.Contains("billing", error, StringComparison.Ordinal);
        Assert.Contains(broken.Store, error, StringComparison.Ordinal);

        Assert.Contains("orders on", output, StringComparison.Ordinal);
        Assert.Contains("serving a subset", output, StringComparison.Ordinal);

        // The part that is easy to get wrong and impossible to see afterwards. A process running
        // one of two brokers and exiting zero tells its supervisor everything is fine.
        Assert.NotEqual(0, code);
    }

    [Fact]
    public async Task Starting_none_of_several_is_a_failure_rather_than_an_empty_machine()
    {
        var (code, output, error) = await ServeAsync(
            [Unopenable("orders"), Unopenable("billing")],
            () => Task.CompletedTask);

        Assert.Contains("No broker started", error, StringComparison.Ordinal);
        Assert.NotEqual(0, code);

        // Nothing may claim to be serving. The banner is the line a person scans for.
        Assert.DoesNotContain("Ctrl+C stops", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interrupt_during_start_up_stops_what_had_already_started()
    {
        // The window nothing was watching, found by reading the shutdown path adversarially
        // rather than by running it: the start loop sat outside the try that disposes what it
        // started, so an interrupt arriving between one broker coming up and the next left the
        // first holding its port and its store handle for as long as the process lived. FR-021 in
        // full, and invisible on any machine that serves a single project — which is every machine
        // that had one before this feature existed.
        //
        // Cancelled by the enumerator rather than by a timer, so this reproduces the exact moment
        // rather than usually reproducing it. A flaky guard on a shutdown path is worse than none:
        // it gets re-run until it passes.
        var orders = Project("orders");
        var billing = Project("billing");

        using var stopping = new CancellationTokenSource();

        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ServeCommand.RunAsync(
            new CancelAfterFirst([orders, billing], stopping),
            null,
            output,
            error,
            cancellationToken: stopping.Token)
            .ContinueWith(
                t => t.IsCanceled || t.Exception?.InnerException is OperationCanceledException ? -1 : t.Result,
                TestContext.Current.CancellationToken,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

        // Cancellation is not a project failing, so it surfaces as the interrupt it is and the
        // entry point turns it into a clean exit. What matters here is the port.
        Assert.Equal(-1, code);

        Assert.True(CanBind(orders.Endpoint), $"{orders.Endpoint} is still held after an interrupt during start-up");
        Assert.True(CanBind(billing.Endpoint), $"{billing.Endpoint} is still held after an interrupt during start-up");
    }

    /// <summary>A project list that raises an interrupt once one project has been served.</summary>
    /// <param name="projects">The projects to hand over.</param>
    /// <param name="stopping">Cancelled after the first is yielded.</param>
    /// <remarks>
    /// A list rather than a timer because the moment being tested is a single iteration wide.
    /// Waiting for it with a delay would pass on a fast machine and pass differently on a slow
    /// one, which for a shutdown path means the guard is eventually deleted rather than believed.
    /// </remarks>
    private sealed class CancelAfterFirst(
        IReadOnlyList<ProjectSettings> projects,
        CancellationTokenSource stopping) : IReadOnlyList<ProjectSettings>
    {
        /// <inheritdoc />
        public int Count => projects.Count;

        /// <inheritdoc />
        public ProjectSettings this[int index] => projects[index];

        /// <inheritdoc />
        public IEnumerator<ProjectSettings> GetEnumerator()
        {
            for (var index = 0; index < projects.Count; index++)
            {
                yield return projects[index];

                if (index == 0)
                {
                    stopping.Cancel();
                }
            }
        }

        /// <inheritdoc />
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task An_empty_manifest_being_watched_waits_and_starts_the_first_project_named()
    {
        // The machine the installer leaves: an entry serving a manifest that names nothing yet. It used to fall through
        // to one default store nobody had named. Now it waits, and the first project named from `parley terminal` or with
        // `parley project add` gets its broker from the watcher within seconds.
        var ct = TestContext.Current.CancellationToken;
        var orders = Project("orders");

        using var named = new ManualResetEventSlim();
        using var stopping = new CancellationTokenSource();

        var output = new StringWriter();
        var error = new StringWriter();

        var serving = ServeCommand.RunAsync(
            [],
            null,
            output,
            error,
            reread: () => named.IsSet ? new List<ProjectSettings> { orders } : null,
            cancellationToken: stopping.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        Assert.False(serving.IsCompleted, $"an empty manifest being watched ended the process: {error}");

        named.Set();

        var reachable = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (!reachable && DateTime.UtcNow < deadline)
        {
            reachable = await AnswersAsync(orders.Endpoint, ct);

            if (!reachable)
            {
                await Task.Delay(250, ct);
            }
        }

        await stopping.CancelAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(reachable, $"the first project named was never served: {output}{error}");
        Assert.Contains("No project is named yet", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("orders: named in the manifest since this started", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_list_returns_rather_than_serving_nothing_forever()
    {
        // Without a watcher. An entry serving a manifest waits for its first project (the test above); a list that is
        // empty with nothing to reread could never start anything, and the alternative to returning is a process that
        // has started nothing and will now wait on the interrupt for as long as anybody leaves it. A supervisor cannot
        // tell that apart from a healthy broker.
        var (code, _, error) = await ServeAsync([], () => Task.CompletedTask);

        Assert.NotEqual(0, code);
        Assert.Contains("No broker started", error, StringComparison.Ordinal);
    }
}
