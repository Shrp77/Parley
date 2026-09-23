using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;
using static Parley.Cli.Tests.Loopback;

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

    /// <summary>Where this machine keeps its Parley files.</summary>
    /// <remarks>
    /// **It named the manifest file before 019 and names the root now.** Every reader and writer below
    /// took the same value, so the change is one line here rather than one per test — which is the whole
    /// reason `ProjectManifest` kept its type name through the conversion.
    /// </remarks>
    private string Root => _directory;

    [Fact]
    public async Task With_nothing_serving_the_store_is_deleted_and_the_machines_catalogue_is_not()
    {
        // **The catalogue is no longer the project's to delete.** A project recorded one of its own
        // until 019, and destroying the project took it; there is one per machine now, shared by every
        // project, so a destroy that removed it would take the other projects' agents with it.
        var catalogue = Path.Combine(_directory, NodeCatalogue.DefaultFileName);
        var billing = Project("billing");

        ProjectManifest.Write(Root, [billing]);
        await File.WriteAllTextAsync(billing.Store, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(catalogue, "{}", TestContext.Current.CancellationToken);

        var (code, output, error) = await RemoveAsync("billing", destroy: true);

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("Destroyed 'billing'", output, StringComparison.Ordinal);
        Assert.False(File.Exists(billing.Store), "the store survived");
        Assert.True(File.Exists(catalogue), "the machine's catalogue was deleted with one project");

        Assert.True(ProjectManifest.TryRead(Root, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task Another_projects_store_is_not_touched()
    {
        // **The catalogue half of this went with 019, and only the catalogue half.** A project used
        // to record one of its own, so destroying one had to leave another's alone; there is one
        // catalogue per machine now and destroying a project does not touch it at all. What still
        // has to hold — and is the reason this test exists — is that a destroy reaches exactly one
        // project's store.
        var orders = Project("orders");
        var billing = Project("billing");

        ProjectManifest.Write(Root, [orders, billing]);

        foreach (var path in new[] { orders.Store, billing.Store })
        {
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        }

        var (code, _, _) = await RemoveAsync("billing", destroy: true);

        Assert.Equal(0, code);
        Assert.True(File.Exists(orders.Store), "the other project's store was deleted");
        Assert.False(File.Exists(billing.Store));
    }

    [Fact]
    public async Task Without_destroy_nothing_is_deleted_and_the_way_to_is_named()
    {
        var billing = Project("billing");

        ProjectManifest.Write(Root, [billing]);
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

        var orders = Project("orders");
        var billing = Project("billing");

        ProjectManifest.Write(Root, [orders, billing]);

        // The real reread: the manifest file, when its stamp moves, exactly as the entry point
        // hands it to the command.
        var stamp = File.GetLastWriteTimeUtc(ProjectFolder.ConfigurationFor(Root, orders.Name));

        var output = new StringWriter();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serving = ServeCommand.RunAsync(
            [orders, billing],
            null,
            output,
            TextWriter.Null,
            reread: () =>
            {
                var now = File.GetLastWriteTimeUtc(ProjectFolder.ConfigurationFor(Root, orders.Name));

                if (now == stamp)
                {
                    return null;
                }

                stamp = now;

                return ProjectManifest.TryRead(Root, out var current, out _) ? current : null;
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

    [Fact]
    public async Task A_broker_the_serving_process_does_not_own_is_reported_and_left_alone()
    {
        // Lens 1 probe, and the adversarial reading of "wait for it to stop": a broker started
        // by hand on the entry's endpoint never stops when the entry goes, because nothing told
        // it to. The command must not kill it, must not delete a store it is still using, and
        // must say so -- and the entry is gone regardless, which it must also say.
        var ct = TestContext.Current.CancellationToken;

        var store = Path.Combine(_directory, "byhand.db");

        using var byHand = BrokerProcess.Start(store);

        var project = new ProjectSettings(ProjectName.Create("byhand"), store, byHand.Address, null);

        ProjectManifest.Write(Root, [project]);

        var started = DateTime.UtcNow;
        var (code, output, error) = await RemoveAsync("byhand", destroy: true);

        Assert.Equal(69, code);
        Assert.Contains("still answers", error, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", error, StringComparison.Ordinal);
        Assert.Contains("Removed 'byhand'", output, StringComparison.Ordinal);
        Assert.True(File.Exists(store), "the store of a broker still running was deleted");

        // It waited for the serving process rather than deciding at once, and gave up in bounded
        // time rather than for ever.
        var waited = DateTime.UtcNow - started;

        Assert.True(waited > TimeSpan.FromSeconds(20) && waited < TimeSpan.FromSeconds(60), $"waited {waited}");

        Assert.True(ProjectManifest.TryRead(Root, out var left, out _));
        Assert.Empty(left);

        // And the broker is untouched by any of it.
        Assert.True(await AnswersAsync(byHand.Address, ct, attempts: 3), "the by-hand broker was stopped");
    }

    [Fact]
    public async Task The_machines_own_files_cannot_be_named_as_a_store_and_so_cannot_be_destroyed()
    {
        // **A lens 2 probe, and 019 closed it by construction rather than by a better refusal.**
        // `--destroy` deletes whatever the project names as its store. The files whose deletion takes
        // every project with it sit at the root — the port record, the machine's catalogue — so a project
        // naming one of those had to be refused on the way in, by `add`, rather than caught on the way
        // out.
        //
        // There is no way in now. A store is `parley.db` inside a folder named by the project, and no
        // flag carries a path, so the value that made this possible cannot be supplied. What is asserted
        // is the property the refusal was protecting: every store lies inside its own project's folder,
        // so destroying one cannot reach anything the machine shares.
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["add", "trap"],
            Root,
            new ProjectFields(null),
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, code);

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out var problem), problem);

        var trap = Assert.Single(projects);
        var folder = ProjectFolder.For(Root, trap.Name);

        Assert.StartsWith(folder + Path.DirectorySeparatorChar, trap.Store, StringComparison.Ordinal);

        // And the root's own files are outside every project folder, which is the other half: a store
        // confined to a folder is only safe if what must survive is not in one.
        Assert.False(
            PortLedger.PathIn(Root).StartsWith(ProjectFolder.ProjectsIn(Root), StringComparison.Ordinal),
            "the port record sits inside the projects directory, where a destroy could reach it");
    }

    [Fact]
    public async Task A_store_that_is_a_directory_is_not_deleted()
    {
        // Lens 2 probe. Deleting is by file, and a directory named as a store -- which the
        // manifest accepts, since a store need not exist yet -- must not become a recursive
        // delete of whatever is in it.
        var directoryStore = Path.Combine(_directory, "not-a-file");
        Directory.CreateDirectory(directoryStore);
        await File.WriteAllTextAsync(Path.Combine(directoryStore, "precious.txt"), "keep", TestContext.Current.CancellationToken);

        var project = new ProjectSettings(
            ProjectName.Create("dir"), directoryStore, "http://127.0.0.1:1", null);

        ProjectManifest.Write(Root, [project]);

        var (code, output, _) = await RemoveAsync("dir", destroy: true);

        Assert.Equal(0, code);
        Assert.True(Directory.Exists(directoryStore), "a directory named as a store was deleted");
        Assert.True(File.Exists(Path.Combine(directoryStore, "precious.txt")));
        Assert.Contains("Nothing was on disk to delete", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_something_else_holds_open_is_reported_not_half_deleted_in_silence()
    {
        // Lens 2 probe. The worst outcome of a delete that meets a locked file is a quiet partial
        // state: entry gone, some files gone, the rest still there, exit zero. What is required
        // is the truth in the exit code and the message: which files went, which did not, and
        // that the entry is gone either way.
        // Only Windows refuses to delete an open file; on Linux and macOS an unlink succeeds and
        // the last handle keeps the data until it closes, so the loud failure this guards cannot
        // occur there and the probe has nothing to assert. Found by the Linux run, 3 of 3.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("a held file can be deleted on this platform, so there is no refusal to report");
        }

        var ct = TestContext.Current.CancellationToken;

        var project = Project("held");

        ProjectManifest.Write(Root, [project]);
        await File.WriteAllTextAsync(project.Store, string.Empty, ct);
        await File.WriteAllTextAsync(project.Store + "-wal", string.Empty, ct);

        // Held the way a process that has the database open holds it: no sharing at all.
        using var holder = new FileStream(project.Store, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var (code, output, error) = await RemoveAsync("held", destroy: true);

        Assert.Equal(74, code);
        Assert.Contains("still has it open", error, StringComparison.Ordinal);
        Assert.Contains("Removed 'held'", output, StringComparison.Ordinal);
        Assert.True(File.Exists(project.Store), "a held store was deleted");

        Assert.True(ProjectManifest.TryRead(Root, out var left, out _));
        Assert.Empty(left);
    }

    [Fact]
    public async Task A_store_a_broker_still_holds_is_not_touched_at_all()
    {
        // The lock is deleted first because it is the check: a broker this command did not stop
        // -- one a terminal restored at logon -- still holds it, and everything after it must stay.
        // Windows-only for the reason the test above gives: elsewhere a held file can be unlinked,
        // and destroy already waits for the endpoint to stop answering before it gets here.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("a held file can be deleted on this platform, so there is no refusal to report");
        }

        var ct = TestContext.Current.CancellationToken;

        var project = Project("held-lock");

        ProjectManifest.Write(Root, [project]);
        await File.WriteAllTextAsync(project.Store, string.Empty, ct);
        await File.WriteAllTextAsync(project.Store + "-wal", string.Empty, ct);

        // Held the way a serving broker holds it.
        using var broker = new FileStream(
            project.Store + StoreFiles.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var (code, _, error) = await RemoveAsync("held-lock", destroy: true);

        Assert.Equal(69, code);
        Assert.Contains("a broker is still serving", error, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", error, StringComparison.Ordinal);
        Assert.True(File.Exists(project.Store), "the store was deleted from under a broker holding it");
        Assert.True(File.Exists(project.Store + "-wal"), "the store's journal was deleted from under a broker holding it");
    }

    [Fact]
    public async Task Destroying_removes_the_lock_and_the_owner_record()
    {
        // Left behind, they would be the one trace of a destroyed project in its directory -- and
        // an owner record naming a process that has since exited is harmless but untrue.
        var ct = TestContext.Current.CancellationToken;

        var project = Project("with-lock");

        ProjectManifest.Write(Root, [project]);
        await File.WriteAllTextAsync(project.Store, string.Empty, ct);
        await File.WriteAllTextAsync(project.Store + StoreFiles.Lock, string.Empty, ct);
        await File.WriteAllTextAsync(project.Store + StoreFiles.Owner, "pid=1", ct);

        var (code, _, _) = await RemoveAsync("with-lock", destroy: true);

        Assert.Equal(0, code);
        Assert.False(File.Exists(project.Store + StoreFiles.Lock));
        Assert.False(File.Exists(project.Store + StoreFiles.Owner));
        Assert.False(File.Exists(project.Store));
    }

    [Fact]
    public async Task Destroying_removes_the_file_location_and_its_staging_directory()
    {
        // The files agents placed belong to the store (SPEC.md §9.5). A project destroyed while they stayed
        // would leave every file anybody placed on disk under a name nothing refers to any more.
        var ct = TestContext.Current.CancellationToken;

        var project = Project("with-files");

        ProjectManifest.Write(Root, [project]);
        await File.WriteAllTextAsync(project.Store, string.Empty, ct);

        var folder = Path.Combine(project.Store + StoreFiles.Files, "pricing");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "price-snapshot.md"), "net of tax", ct);
        Directory.CreateDirectory(project.Store + StoreFiles.FilesIncoming);

        var (code, output, _) = await RemoveAsync("with-files", destroy: true);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(project.Store + StoreFiles.Files));
        Assert.False(Directory.Exists(project.Store + StoreFiles.FilesIncoming));
        Assert.Contains(StoreFiles.Files, output, StringComparison.Ordinal);
    }

    private async Task<(int Code, string Out, string Error)> RemoveAsync(string name, bool destroy)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await ProjectCommand.RunAsync(
            ["remove", name],
            Root,
            new ProjectFields(null),
            output,
            error,
            destroy,
            TestContext.Current.CancellationToken);

        return (code, output.ToString(), error.ToString());
    }

    // **The store is derived, so the fixture cannot put one anywhere.** That is the layout, and it is
    // also what closes two of the probes below: a store inside the project's own folder cannot be
    // another project's, and cannot be the machine's own configuration.
    private ProjectSettings Project(string name) =>
        new(
            ProjectName.Create(name),
            ProjectFolder.StoreFor(Root, ProjectName.Create(name)),
            $"http://127.0.0.1:{FreePort()}",
            null);

}
