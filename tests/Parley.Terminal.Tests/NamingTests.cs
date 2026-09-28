using Parley.Client;
using Parley.Configuration;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

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

    // **The root, not a manifest file.** `--config` names the directory this machine keeps its
    // Parley files in; the projects are the folders under it.
    private string Root => _directory;

    [Fact]
    public async Task A_new_project_is_checked_as_it_is_typed_and_needs_nothing_but_a_name()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, installed: null);

        await shell.Create();

        var form = (FormDialog)driving.Find(Naming.CreateTitle);

        // **Two fields, and this asserted four.** It used to check that a store was proposed rather than
        // blank, and a port with it — which was the best a form could do when a person still had to
        // supply both. 019 removed the questions rather than improving the answers, so what is asserted
        // is that they have gone: a name, and a description that is optional.
        Assert.Equal(2, form.Values.Count);
        Assert.Equal(string.Empty, form.Values[0]);
        Assert.Equal(string.Empty, form.Values[1]);

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

        form.Fill(1, "the first one");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.True(ProjectManifest.TryRead(Root, out var projects, out var problem), problem);

        var named = Assert.Single(projects);

        Assert.Equal("alpha", named.Name.Value);
        Assert.Equal("the first one", named.Description);

        // **Both derived, neither typed**, which is the whole of what this form gave up. The store is
        // inside the project's own folder and the port comes from the band.
        Assert.Equal(ProjectFolder.StoreFor(Root, named.Name), named.Store);
        Assert.InRange(new Uri(named.Endpoint).Port, 19100, 19355);

        Assert.Contains(Naming.Created("alpha"), shell.Said, StringComparison.Ordinal);

        // Nothing starts it, and the form says so rather than leaving the first person to use it
        // assuming a broker was spawned.
        Assert.Contains("Nothing starts it yet", shell.Said, StringComparison.Ordinal);

        // **And that is all it says.** The owner's call of 2026-09-27: this printed the command's whole
        // report for a shell -- the port, the folder, a paragraph about tunnels -- and nobody read it. Where
        // the project answers is on its row in the list now. Must fail against the old report, which carried
        // every one of these.
        Assert.DoesNotContain("serving on", shell.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("files in", shell.Said, StringComparison.Ordinal);
        Assert.DoesNotContain("tunnel", shell.Said, StringComparison.Ordinal);
        Assert.Contains(ProjectsRegion.Address(named), Drawn.Rows(shell.Projects.List)[0], StringComparison.Ordinal);

        // The description is on the list it returns to.
        Assert.Contains(
            shell.Projects.Projects,
            p => string.Equals(p.Description, "the first one", StringComparison.Ordinal));
    }

    [Fact]
    public void Naming_a_running_deployment_is_not_offered_at_all_any_more()
    {
        // **Three tests lived here and the flow they drove has gone.** "Name the running deployment"
        // adopted a broker already serving, reading its store and port out of an autostart entry:
        // `The_running_deployment_is_named_from_a_form_that_is_already_filled_in`,
        // `A_second_project_is_not_proposed_the_first_ones_port`, and
        // `A_name_a_store_or_a_port_already_taken_is_refused_in_the_field` between them covered the
        // form, the port it proposed, and the three fields it validated.
        //
        // `CanNameRunning` required an entry serving one explicit store — an entry written with
        // `--db`. Nothing can write one now, so no machine will have one again; and adopting a store
        // where it lies is not something 019 can do, because a project's store is derived from its
        // name. Deriving a fresh one and keeping the stated endpoint would have compiled and passed
        // while pointing the named project at an empty database.
        //
        // Asserted as an absence rather than dropped, because a suite that merely lost those three
        // would leave the next reader unable to tell a rule that was removed from one nobody wrote.
        var surface = typeof(Naming);

        foreach (var gone in new[]
                 {
                     "NameRunningTitle", "CanNameRunning", "OpenNameRunningAsync",
                     "ProposedStore", "StoreProblem", "EndpointProblem", "FreeEndpoint",
                 })
        {
            Assert.True(
                surface.GetMember(gone, Bindings).Length == 0,
                $"Naming.{gone} is back: it belonged to a flow 019 removed, and a form that "
                + "proposes a store or a port is a form asking a question that no longer exists");
        }

        Assert.Empty(typeof(Shell).GetMember("NameRunning", Bindings));
    }

    private const System.Reflection.BindingFlags Bindings =
        System.Reflection.BindingFlags.Public
        | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.Instance;

    /// <summary>Opens the surface on this test's manifest.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="installed">What this machine's supervisor runs, for the test.</param>
    /// <returns>The surface, with its projects read.</returns>
    private async Task<Shell> OpenAsync(Driving.Session driving, InstalledEntry? installed)
    {
        var directory = BrokerDirectory.FromManifest(Root);
        var shell = TerminalCommand.Build(directory, manifestPath: Root, installed: installed);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }
}
