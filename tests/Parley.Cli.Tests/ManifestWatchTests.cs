using System.Net;
using System.Net.Sockets;
using Parley.Configuration;
using Parley.Core;
using Xunit;
using static Parley.Cli.Tests.Loopback;

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
            ProjectName.Create("broken"), brokenStore, $"http://127.0.0.1:{FreePort()}", null);

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

    [Fact]
    public async Task A_project_removed_and_renamed_back_in_one_edit_is_restarted_on_its_new_settings()
    {
        // Lens 1 probe. The watcher stops removed projects before starting new ones so that a
        // name reused in a single edit -- destroy and recreate between two ticks -- comes up on
        // the new entry rather than being left on the old one because "billing" was already
        // serving. The evidence is the ports: the old one frees and the new one answers.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billingBefore = Project("billing");
        var billingAfter = Project("billing");

        var manifest = new List<ProjectSettings> { orders, billingBefore };
        var changed = false;

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billingBefore],
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
            Assert.True(await AnswersAsync(billingBefore.Endpoint, ct));

            manifest[1] = billingAfter;
            changed = true;

            Assert.True(await AnswersAsync(billingAfter.Endpoint, ct), "the re-added project never came up on its new port");

            var freed = false;

            for (var attempt = 0; attempt < 50 && !freed; attempt++)
            {
                await Task.Delay(100, ct);
                freed = CanBind(billingBefore.Endpoint);
            }

            Assert.True(freed, "the old entry's broker kept its port");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task An_interrupt_during_a_late_start_leaves_no_port_held()
    {
        // Lens 1 probe. FR-021's failure, reached through the watcher: a project named while
        // serving is starting when Ctrl+C arrives. The start throws OperationCanceledException
        // inside the watcher, which must not swallow a half-started broker -- every port is
        // free once the command returns, or a broker is running that nothing can stop.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var late = Enumerable.Range(0, 6).Select(i => Project($"late{i}")).ToList();

        var manifest = new List<ProjectSettings> { orders };
        var changed = false;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            TextWriter.Null,
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

        Assert.True(await AnswersAsync(orders.Endpoint, ct));

        manifest.AddRange(late);
        changed = true;

        // Somewhere between "the watcher has seen the edit" and "all six are up".
        await Task.Delay(ServeCommand.WatchInterval + TimeSpan.FromMilliseconds(150), ct);
        await stopping.CancelAsync();

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Zero, not "zero or unavailable": an interrupt is how this is meant to end, and a
        // process that reports failure for being asked to stop teaches its supervisor to
        // restart it. The looser assertion that stood here briefly would have let that through.
        Assert.Equal(0, code);

        foreach (var project in late.Prepend(orders))
        {
            Assert.True(CanBind(project.Endpoint), $"{project.Name} still holds its port after the interrupt");
        }
    }

    [Fact]
    public async Task A_manifest_emptied_and_refilled_leaves_the_process_serving_the_new_project()
    {
        // Lens 1 probe. Every project removed at once is the state after somebody destroys the
        // last one, and the process must neither exit -- the supervisor would restart it into
        // the same emptiness -- nor lose the watcher, so that the next project named is served.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        var manifest = new List<ProjectSettings> { orders };
        var changed = false;

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
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
            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            manifest.Clear();
            changed = true;

            var freed = false;

            for (var attempt = 0; attempt < 50 && !freed; attempt++)
            {
                await Task.Delay(100, ct);
                freed = CanBind(orders.Endpoint);
            }

            Assert.True(freed, "the last project's broker kept its port");
            Assert.False(serving.IsCompleted, "the process exited when its last project was removed");

            manifest.Add(billing);
            changed = true;

            Assert.True(await AnswersAsync(billing.Endpoint, ct), "a project named into an emptied manifest never came up");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task A_project_that_could_not_start_is_started_when_its_port_frees_and_the_manifest_changes()
    {
        // Lens 1 probe. A port held by something else at start is the transient failure a
        // machine actually meets -- the previous broker still exiting at logon -- and the watcher
        // must not treat "failed once" as "never". It tries again on the next change to the
        // manifest, and by then the port is free.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        var holder = new TcpListener(IPAddress.Loopback, new Uri(billing.Endpoint).Port);
        holder.Start();

        var manifest = new List<ProjectSettings> { orders, billing };
        var changed = false;

        var output = new StringWriter();
        var error = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billing],
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
            Assert.True(await AnswersAsync(orders.Endpoint, ct));
            Assert.Contains("billing: could not listen", error.ToString(), StringComparison.Ordinal);

            holder.Stop();

            // A change the watcher notices: any change, here a description.
            manifest[1] = billing with { Description = "second try" };
            changed = true;

            Assert.True(await AnswersAsync(billing.Endpoint, ct), "the project whose port was busy at start never came up");
        }
        finally
        {
            holder.Stop();
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Exit zero: the failure at start was reported then, and the project is serving now.
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_read_stops_nothing_and_a_fixed_one_is_served_again()
    {
        // Lens 1 probe. Somebody halfway through editing a project's configuration by hand, or a write
        // that was interrupted: the file is there and is not configuration. The one thing the watcher
        // must not do on the strength of that is stop every project -- "removed" is a claim the
        // unreadable file cannot make. Then the file is fixed with a project added, and that project is
        // served.
        //
        // **Repointed at one project's own file, which is where a hand edit lands now.** The probe used
        // to damage the single manifest; with a directory of them the equivalent damage is to one
        // project's configuration, and the guarantee is the same or stronger -- an unreadable *one* must
        // not stop the others either.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");
        var billing = Project("billing");

        Parley.Configuration.ProjectManifest.Write(_directory, [orders]);

        var damaged = ProjectFolder.ConfigurationFor(_directory, billing.Name);

        Directory.CreateDirectory(ProjectFolder.For(_directory, billing.Name));
        await File.WriteAllTextAsync(damaged, "{ this is not configuration", ct);

        var stamp = File.GetLastWriteTimeUtc(damaged);
        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
            null,
            output,
            TextWriter.Null,
            reread: () =>
            {
                var now = File.GetLastWriteTimeUtc(damaged);

                if (now == stamp)
                {
                    return null;
                }

                stamp = now;

                return Parley.Configuration.ProjectManifest.TryRead(_directory, out var current, out _)
                    ? current
                    : null;
            },
            cancellationToken: stopping.Token);

        try
        {
            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            File.SetLastWriteTimeUtc(damaged, DateTime.UtcNow.AddSeconds(1));

            // Long enough for several ticks with the broken file in place.
            await Task.Delay(ServeCommand.WatchInterval * 3, ct);

            Assert.True(
                await AnswersAsync(orders.Endpoint, ct, attempts: 3),
                "one unreadable project stopped another that was serving");

            Assert.DoesNotContain("stopping its broker", output.ToString(), StringComparison.Ordinal);

            // Deleted before it is rewritten, because the writer refuses to write over a file it cannot
            // parse -- the same refusal that keeps a person's broken edit from being silently replaced --
            // and this probe is the person fixing it.
            File.Delete(damaged);
            Parley.Configuration.ProjectManifest.Write(_directory, [orders, billing]);
            File.SetLastWriteTimeUtc(damaged, DateTime.UtcNow.AddSeconds(2));

            Assert.True(
                await AnswersAsync(billing.Endpoint, ct),
                "a project whose configuration was repaired never came up");
        }
        finally
        {
            await stopping.CancelAsync();
        }

        var code = await serving.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task A_change_to_the_description_alone_restarts_nothing()
    {
        // Lens 2 probe. A broker restarted for a harmless edit drops every attached agent's
        // session for nothing; the watcher compares what a broker is made of and not the
        // description, and this holds it to that. The port never frees and no stop is announced.
        var ct = TestContext.Current.CancellationToken;

        var orders = Project("orders");

        var manifest = new List<ProjectSettings> { orders };
        var changed = false;

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders],
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
            Assert.True(await AnswersAsync(orders.Endpoint, ct));

            manifest[0] = orders with { Description = "described after the fact" };
            changed = true;

            // Watched across several ticks: the port must stay taken throughout.
            var deadline = DateTime.UtcNow + ServeCommand.WatchInterval * 3;

            while (DateTime.UtcNow < deadline)
            {
                Assert.False(CanBind(orders.Endpoint), "the broker released its port for a description change");
                await Task.Delay(100, ct);
            }

            Assert.DoesNotContain("stopping its broker", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await stopping.CancelAsync();
        }

        Assert.Equal(0, await serving.WaitAsync(TimeSpan.FromSeconds(30), ct));
    }

    private ProjectSettings Project(string name) =>
        new(
            ProjectName.Create(name),
            ProjectFolder.StoreFor(_directory, ProjectName.Create(name)),
            $"http://127.0.0.1:{FreePort()}",
            null);
}
