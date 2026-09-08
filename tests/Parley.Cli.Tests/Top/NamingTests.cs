using Parley.Cli.Top;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Naming a project from inside the surface: checked as it is typed, proposed rather than asked
/// for blank, and written through the command that owns the manifest.
/// </summary>
/// <remarks>
/// <para>
/// Two forms, because the two questions are different. Naming the deployment this machine already
/// runs has every answer written down in the autostart entry; creating a project needs values that
/// do not exist yet. Both are driven here through the dialog the surface opens.
/// </para>
/// <para>
/// The endpoints used are ports nothing answers on, so what is exercised is the form and the
/// command rather than a broker.
/// </para>
/// </remarks>
public sealed class NamingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-naming-" + Guid.NewGuid().ToString("N"));

    public NamingTests() => Directory.CreateDirectory(_directory);

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

    private string Manifest => Path.Combine(_directory, "projects.json");

    [Fact]
    public async Task A_new_project_is_checked_as_it_is_typed_and_proposed_its_own_paths()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, installed: null);

        await shell.Create();

        var form = (FormDialog)driving.Find(Naming.CreateTitle);

        // The store is proposed rather than blank, and the port with it.
        Assert.EndsWith(".db", form.Values[1], StringComparison.Ordinal);
        Assert.Equal(Naming.DefaultEndpoint, form.Values[2]);

        // **Corrected as it is typed, not refused after four more answers — and this assertion
        // changed shape rather than being deleted.** It used to fill "Alpha" and expect the words
        // "lowercase letters" in the problem, which is what the field said when it accepted a
        // capital and complained about it. The owner typed `TimeTracker` into this field and met no
        // resistance at all, because that complaint sat on a clipped row; the rule is applied now,
        // so a capital never survives to be complained about.
        form.Fill(0, "Alpha");

        Assert.Equal("alpha", form.Values[0]);
        Assert.Contains(Naming.NameWasCorrected, form.Problems[0], StringComparison.Ordinal);

        // The note stays while the corrected text does -- it explains why the field holds
        // something other than what was typed, which is true until that text goes -- and clears
        // when the field is emptied.
        form.Fill(0, string.Empty);

        // An empty name has a problem of its own, and a real problem outranks the note -- so what
        // is asserted here is that the note is gone, not that the line is.
        Assert.DoesNotContain(Naming.NameWasCorrected, form.Problems[0], StringComparison.Ordinal);

        form.Fill(0, "alpha");

        Assert.Equal(string.Empty, form.Problems[0]);

        form.Fill(2, "http://127.0.0.1:1");
        form.Fill(4, "the first one");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.True(ProjectManifest.TryRead(Manifest, out var projects, out var problem), problem);

        var named = Assert.Single(projects);

        Assert.Equal("alpha", named.Name.Value);
        Assert.EndsWith(".db", named.Store, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:1", named.Endpoint);
        Assert.Null(named.NodeConfig);
        Assert.Equal("the first one", named.Description);

        Assert.Contains(Naming.Named, shell.Said, StringComparison.Ordinal);

        // Nothing starts it, and the form says so rather than leaving the first person to use it
        // assuming a broker was spawned.
        Assert.Contains("Nothing starts it yet", shell.Said, StringComparison.Ordinal);

        // The description is on the list it returns to.
        Assert.Contains(
            shell.Projects.Projects,
            p => string.Equals(p.Description, "the first one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_running_deployment_is_named_from_a_form_that_is_already_filled_in()
    {
        // Every answer is already in the autostart entry, so the form is pre-filled from it. The
        // one thing typed is the name.
        var entry = new InstalledEntry(
            Store: Path.Combine(_directory, "running.db"),
            Listen: "http://127.0.0.1:1",
            NodeConfig: Path.Combine(_directory, "node.json"),
            Console: null,
            Manifest: null);

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, entry);

        await shell.NameRunning();

        var form = (FormDialog)driving.Find(Naming.NameRunningTitle);

        Assert.Equal(entry.Store, form.Values[1]);
        Assert.Equal(entry.Listen, form.Values[2]);
        Assert.Equal(entry.NodeConfig, form.Values[3]);

        form.Fill(0, "prod");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.True(ProjectManifest.TryRead(Manifest, out var projects, out var problem), problem);

        var named = Assert.Single(projects);

        Assert.Equal(entry.Store, named.Store);
        Assert.Equal(entry.Listen, named.Endpoint);
        Assert.Equal(entry.NodeConfig, named.NodeConfig);

        // And what to do next: the entry serves one store and will not serve this manifest until
        // it is reinstalled without one.
        Assert.Contains("Re-run `parley autostart install`", shell.Said, StringComparison.Ordinal);

        // Named, so it is no longer offered: the entry's store is claimed.
        Assert.DoesNotContain(shell.Actions(), action => action.Label == Naming.NameRunningTitle);
    }

    [Fact]
    public void A_second_project_is_not_proposed_the_first_ones_port()
    {
        // The default port when nothing holds it — an MCP server told nothing about a broker
        // looks there — and a free one otherwise, because a second deployment cannot share it.
        Assert.Equal(Naming.DefaultEndpoint, Naming.FreeEndpoint([]));

        var second = Naming.FreeEndpoint([Naming.DefaultEndpoint]);

        Assert.NotEqual(Naming.DefaultEndpoint, second);
        Assert.StartsWith("http://127.0.0.1:", second, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_a_store_or_a_port_already_taken_is_refused_in_the_field()
    {
        var alpha = new ProjectSettings(
            ProjectName.Create("alpha"), Path.Combine(_directory, "alpha.db"), "http://127.0.0.1:1", null, null);

        Assert.Contains("lowercase", Naming.NameProblem("Alpha", [alpha]), StringComparison.Ordinal);
        Assert.Contains("already named", Naming.NameProblem("alpha", [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.NameProblem("beta", [alpha]));

        Assert.Contains("already owns", Naming.StoreProblem(alpha.Store, [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.StoreProblem(Path.Combine(_directory, "beta.db"), [alpha]));

        Assert.Contains("already listens", Naming.EndpointProblem("http://127.0.0.1:1/", [alpha]), StringComparison.Ordinal);
        Assert.Contains("not an absolute address", Naming.EndpointProblem("nowhere", [alpha]), StringComparison.Ordinal);
        Assert.Null(Naming.EndpointProblem("http://127.0.0.1:2", [alpha]));
    }

    [Fact]
    public void The_running_deployment_is_offered_only_while_nothing_names_it()
    {
        var store = Path.Combine(_directory, "running.db");
        var explicitEntry = new InstalledEntry(store, null, null, null, null);
        var manifestEntry = new InstalledEntry(null, null, null, null, Manifest);

        var claimed = new ProjectSettings(
            ProjectName.Create("prod"), store, "http://127.0.0.1:1", null, null);

        Assert.True(Naming.CanNameRunning([], explicitEntry));
        Assert.False(Naming.CanNameRunning([claimed], explicitEntry));
        Assert.False(Naming.CanNameRunning([], manifestEntry));
        Assert.False(Naming.CanNameRunning([], null));
    }

    /// <summary>Opens the surface on this test's manifest.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="installed">What this machine's supervisor runs, for the test.</param>
    /// <returns>The surface, with its projects read.</returns>
    private async Task<Shell> OpenAsync(Driving.Session driving, InstalledEntry? installed)
    {
        var directory = BrokerDirectory.FromManifest(Manifest);
        var shell = TopCommand.Build(directory, manifestPath: Manifest, installed: installed);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }
}
