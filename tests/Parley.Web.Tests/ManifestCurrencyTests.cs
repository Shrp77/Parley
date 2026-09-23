using Parley.Configuration;
using Parley.Core;
using Xunit;
using Parley.Client;

namespace Parley.Web.Tests;

/// <summary>
/// That the console follows this machine's manifest rather than remembering it.
/// </summary>
/// <remarks>
/// <para>
/// Found in use. An operator ran <c>parley project add</c>, went back to the project list exactly
/// as the naming page told them to, and the stand-in row was still there — because the directory
/// read the manifest once, in its factory, and a singleton behind a web server then answered from
/// that read for the life of the process.
/// </para>
/// <para>
/// That was harmless while <c>parley console</c> was a thing somebody started and stopped, and
/// stopped being harmless when the console moved inside the broker and <c>autostart install</c>
/// turned it on by default. The only way to collect on the naming page's promise was to restart
/// the broker, which drops every agent attached to the deployment — to pick up a rename that
/// SPEC.md §4.1 says the protocol never sees.
/// </para>
/// </remarks>
public sealed class ManifestCurrencyTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"parley-manifest-{Guid.NewGuid():N}");

    private readonly string _path;

    public ManifestCurrencyTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "projects.json");
    }

    /// <summary>Writes a manifest the way <c>parley project add</c> writes one.</summary>
    /// <param name="names">The projects to put in it.</param>
    /// <remarks>
    /// Through the real writer rather than by hand, for the reason
    /// <c>UnnamedMachineTests</c> already gives — it moved to <c>Parley.Client.Tests</c> with
    /// the directory it checks: a hand-rolled file the reader rejects
    /// makes a test pass because nothing was listed, which looks identical to the case it meant
    /// to check.
    /// </remarks>
    private void WriteManifest(params string[] names) =>
        ProjectManifest.Write(
            _path,
            [.. names.Select((name, index) => new ProjectSettings(
                ProjectName.Create(name),
                Path.Combine(_folder, $"{name}.db"),
                $"http://127.0.0.1:{5301 + index}",
                null))]);

    [Fact]
    public void A_project_named_after_the_console_started_appears_without_a_restart()
    {
        // The defect itself, in the order it happened: console up first, manifest written second.
        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Empty(directory.Projects);

        WriteManifest("orders");

        var named = Assert.Single(directory.Projects);

        Assert.Equal("orders", named.Name.Value);
    }

    [Fact]
    public void The_stand_in_gives_way_to_the_name_the_operator_chose()
    {
        // The same again on a machine running unnamed, which is how the deployment this was built
        // for actually runs -- so this is the path the operator was on, not a variant of it. The
        // fallback row must not survive the naming: two rows for one broker, one of them called
        // `this-machine`, would read as two deployments.
        using var directory = BrokerDirectory.FromManifest(
            _path, new FallbackBroker("http://127.0.0.1:51707"));

        Assert.Equal("this-machine", directory.Projects[0].Name.Value);

        WriteManifest("orders");

        var only = Assert.Single(directory.Projects);

        Assert.Equal("orders", only.Name.Value);
        Assert.NotNull(directory.Find("orders"));
        Assert.Null(directory.Find("this-machine"));
    }

    [Fact]
    public void A_project_removed_from_the_manifest_stops_being_reachable()
    {
        // The other direction, and the one a cache makes dangerous rather than merely stale: a
        // page still listing a project the operator withdrew is a link to a broker this machine
        // has said it no longer tracks.
        WriteManifest("alpha", "beta");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Equal(2, directory.Projects.Count);

        WriteManifest("alpha");

        Assert.Single(directory.Projects);
        Assert.Null(directory.Find("beta"));
    }

    [Fact]
    public void A_manifest_broken_after_it_was_read_is_reported_rather_than_remembered()
    {
        // Keeping the last good read would be the tempting half-measure, and it hides the edit
        // that caused the problem: the operator sees their projects listed and no sign that the
        // file they just saved is unusable. Every other reader of this manifest refuses; so does
        // this one.
        WriteManifest("alpha");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Null(directory.ManifestProblem);

        File.WriteAllText(_path, "{ \"projects\": \"not a list\" }");

        Assert.NotNull(directory.ManifestProblem);
        Assert.Empty(directory.Projects);
    }

    [Fact]
    public void A_manifest_repaired_after_it_broke_is_read_again()
    {
        // A directory that latched on the failure would need the restart this whole change
        // removes, and would need it at the worst moment -- the operator has just fixed the file
        // and has no way to tell whether the fix worked.
        File.WriteAllText(_path, "{ \"projects\": \"not a list\" }");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.NotNull(directory.ManifestProblem);

        WriteManifest("alpha");

        Assert.Null(directory.ManifestProblem);
        Assert.Single(directory.Projects);
    }

    [Fact]
    public void An_unchanged_manifest_is_not_read_twice()
    {
        // The cache, probed rather than assumed. `TryRead` builds a new list per call, so the
        // same instance coming back twice is evidence the file was not opened the second time --
        // which is what keeps repeated reads inside one page render agreeing with each other, and
        // what makes this affordable on a page that touches Projects three times.
        WriteManifest("alpha");

        using var directory = BrokerDirectory.FromManifest(_path);

        Assert.Same(directory.Projects, directory.Projects);
    }

    [Fact]
    public void A_directory_built_from_a_fixed_list_follows_no_file()
    {
        // The constructor the tests use, and the contract it keeps. A caller who supplied the
        // projects owns them; swapping in whatever this machine's manifest happens to hold would
        // answer a question nobody asked -- and would make every test in this assembly depend on
        // the developer's own AppData.
        WriteManifest("alpha");

        var fixedList = new[]
        {
            new ProjectSettings(
                ProjectName.Create("given"), "given.db", "http://127.0.0.1:5399", null)
        };

        using var directory = new BrokerDirectory(fixedList, BrokerDirectory.Connect);

        WriteManifest("alpha", "beta");

        Assert.Equal("given", Assert.Single(directory.Projects).Name.Value);
    }

    [Fact]
    public void The_default_console_never_reads_the_manifest_of_the_machine_it_runs_on()
    {
        // Parley.Tests.ManifestIsolationTests pins the resolution for every assembly that takes
        // the guard. This is the half only this one can check: that the call the console's own
        // entry point makes -- FromManifest with no arguments, exactly as ConsoleHost registers
        // it -- comes back empty. The bare WebApplicationFactory<Program> builds that console, so
        // on a machine that had named a deployment the test host listed it and opened channels to
        // the broker serving it.
        using var directory = BrokerDirectory.FromManifest();

        Assert.Empty(directory.Projects);
        Assert.Null(directory.ManifestProblem);
    }

    [Fact]
    public async Task Readers_racing_a_rewrite_never_see_a_list_that_was_never_written()
    {
        // The directory is a singleton behind a web server, so every page render reaches it at
        // once, and re-reading made that a read of a file somebody may be replacing. The failure
        // being probed is not a slow read -- it is a torn one: a caller handed a half-parsed list,
        // a null, or an exception out of a property that used to be a field.
        //
        // Churn includes deleting the file, because "absent" is a distinct branch and an operator
        // running `project remove` produces exactly this sequence.
        // Bounded by the writer's rounds rather than by a clock. An earlier version ran both
        // sides for 400ms and asserted the readers had observed the list change; it passed alone
        // and failed inside the full suite, where the churn was starved and observed one shape.
        // A test whose subject is a race must not also depend on winning one -- the race here is
        // guaranteed by construction, because the writer completes every round while the readers
        // are live.
        WriteManifest("alpha", "beta");

        using var directory = BrokerDirectory.FromManifest(_path);
        using var stopping = new CancellationTokenSource();

        var reads = 0;
        var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Threads rather than Task.Run, and that is the second timing dependency this test had.
        // Inside the full suite the pool is saturated by the other fixtures' hosts, so eight
        // queued readers had not started by the time sixty rounds of file writes were done -- the
        // assertions passed over an empty bag and the harness guard caught it. A reader that must
        // run is a thread, not a work item.
        var readers = Enumerable.Range(0, 8).Select(reader => Run(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var projects = directory.Projects;

                    Assert.NotNull(projects);

                    // Every name that comes back must be one that was actually written. A torn
                    // read shows up here as a project nobody ever put in the file.
                    foreach (var project in projects)
                    {
                        Assert.Contains(
                            project.Name.Value, (string[])["alpha", "beta"], StringComparer.Ordinal);
                    }

                    Interlocked.Increment(ref reads);
                }
                catch (Exception failure)
                {
                    faults.Add(failure);
                    return;
                }
            }
        })).ToArray();

        try
        {
            // Deleting is one of the rounds: absent is a distinct branch, and an operator running
            // `project remove` produces exactly this sequence.
            for (var round = 0; round < 60; round++)
            {
                switch (round % 3)
                {
                    case 0: WriteManifest("alpha", "beta"); break;
                    case 1: WriteManifest("alpha"); break;
                    default: File.Delete(_path); break;
                }
            }
        }
        finally
        {
            await stopping.CancelAsync();

            foreach (var reader in readers)
            {
                reader.Join();
            }
        }

        Assert.Empty(faults);

        // Guards the harness rather than the subject: readers that never started would make every
        // assertion above them vacuous, and the writer would still have completed its rounds.
        Assert.True(reads > 0, "no reader completed a read");
    }

    /// <summary>Starts work on a thread of its own, already running.</summary>
    /// <param name="work">What to do until it is told to stop.</param>
    /// <returns>The thread, to be joined.</returns>
    /// <remarks>
    /// A background thread so a reader that somehow never notices the cancellation cannot hold
    /// the test host open after the run is over.
    /// </remarks>
    private static Thread Run(Action work)
    {
        var thread = new Thread(() => work()) { IsBackground = true };

        thread.Start();

        return thread;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder the runner has not finished with. Not this test's business.
        }
    }
}
