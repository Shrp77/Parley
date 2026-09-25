using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What `parley enroll` tells a person to do next, read off a real run in each of the shapes it distinguishes.
/// </summary>
/// <remarks>
/// <para>
/// The gate over the installer change found this step telling people to deploy with `serve --db` and
/// `autostart install --db`, which serve one store outside the manifest and are for testing only. The replacement
/// branches on two facts — whether a project is named, and whether the catalogue this run wrote is the one that
/// project's entry names — and a sentence chosen by a branch is only as right as the branch, so each is driven here
/// rather than read.
/// </para>
/// <para>
/// The catalogue matters because a machine serving several projects hosts each one's node from the catalogue that
/// project's entry names, and consent written somewhere else is consent nothing reads there. A machine with one project
/// also honours <c>PARLEY_NODE_CONFIG</c>, so pointing the entry at the catalogue written is never wrong, only sometimes
/// unnecessary.
/// </para>
/// </remarks>
public sealed class EnrolmentNextStepsTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-nextsteps-" + Path.GetRandomFileName());

    private readonly ServedProject _alpha;

    public EnrolmentNextStepsTests()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        _alpha = ServedProject.Start("alpha", Path.Combine(_root, "alpha.db"), Catalogue);
        ProjectManifest.Write(ManifestPath, [_alpha.Settings]);
    }

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Workspace => Path.Combine(_root, "checkouts", "orders");

    // **A root, not the old manifest file.** Writing through a path ending in `projects.json`
    // creates a *directory* of that name and every read of it afterwards is refused — which is
    // why `ProjectManifest.Write` now refuses it too.
    private string ManifestPath => _root;

    private string Catalogue => Path.Combine(_root, "alpha-node.json");

    private string Elsewhere => Path.Combine(_root, "elsewhere-node.json");

    private async Task<string> ApplyAsync(string catalogue, bool unattended, ProjectSettings? project)
    {
        // The row a surface hands back. It was a manifest naming this catalogue and this
        // repository, and what the next steps branch on — whether a project is named, and whether
        // the catalogue written is the one that project's entry names — is unchanged by the row
        // arriving in memory rather than through a file.
        var chosen = new EnrollmentEntry(
            Workspace.Replace(Path.DirectorySeparatorChar, '/'),
            "orders",
            "What it owns. Not what it does not.",
            unattended)
        {
            Enroll = true,
            OwnsApproved = true,
        };

        var output = new StringWriter();
        var error = new StringWriter();

        var code = await EnrollCommand.EnrolAsync(
            [chosen],
            describe: false,
            new Resolved(catalogue, ValueSource.Argument),
            output,
            error,
            project,
            project is null ? [] : [project],
            ManifestPath,
            TestContext.Current.CancellationToken);

        var said = output.ToString();

        TestContext.Current.TestOutputHelper?.WriteLine(said + error);
        Assert.True(code == 0, $"exit {code}: {error}");

        // Whatever the shape, no step deploys with one store.
        Assert.DoesNotContain("--db <path to the store> --with-node", said, StringComparison.Ordinal);
        Assert.DoesNotContain("autostart install --db", said, StringComparison.Ordinal);

        return said;
    }

    [Fact]
    public async Task A_named_project_whose_entry_names_this_catalogue_needs_only_the_entry()
    {
        var said = await ApplyAsync(Catalogue, unattended: true, _alpha.Settings);

        Assert.Contains("A broker and this machine's node for alpha.", said, StringComparison.Ordinal);
        Assert.Contains("parley autostart install", said, StringComparison.Ordinal);
        Assert.DoesNotContain("project set", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consent_written_to_any_catalogue_points_the_project_at_none_of_them()
    {
        // **The instruction this asserted is gone, and what replaced it is the absence.** A project
        // recorded a catalogue of its own, so consent written somewhere the project did not name left
        // a step: point it there. 018 made that field mean nothing and 019 removed it, so there is one
        // catalogue per machine and nothing a project could be pointed at.
        //
        // Asserted as a refusal to mention it rather than deleted, because a step that quietly grew
        // back would name a flag the tool no longer has — and this is the one place that text is read.
        var said = await ApplyAsync(Elsewhere, unattended: true, _alpha.Settings);

        Assert.DoesNotContain("project set", said, StringComparison.Ordinal);
        Assert.DoesNotContain("--node-config", said, StringComparison.Ordinal);
        Assert.Contains("for alpha.", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enrolling_records_the_consent_so_the_node_is_named()
    {
        // **This asserted "no consent recorded names no node", and the state it named cannot
        // happen any more.** Consent used to be a field ticked per repository in a worksheet, so
        // an enrolment could record none and the next steps would name only a broker. 019 deleted
        // the worksheet and FR-043 reversed the rule: enrolling is the consent.
        //
        // So the row's own `unattended` is passed as false here deliberately — it is the value the
        // old design would have honoured — and the steps still name the node, because what grants
        // the consent now is the choosing rather than the field.
        var said = await ApplyAsync(Catalogue, unattended: false, _alpha.Settings);

        Assert.Contains("A broker and this machine's node for alpha.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("node-config", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_project_named_says_to_name_one_first()
    {
        var said = await ApplyAsync(Elsewhere, unattended: true, project: null);

        Assert.Contains("Name the project these repositories answer in", said, StringComparison.Ordinal);

        // **The name is the whole of the command**, and this asserted three flags none of which
        // still exist. The catalogue does not appear because there is one per machine, so naming a
        // project cannot point it at one; `--db` and `--listen` do not appear because 019 removed
        // them, and until now this test required both — so the step told an operator to run a
        // command the tool would refuse, and the check over it insisted on that wording.
        // **And now the step names no project command at all.** Creating a project became the
        // terminal's on 2026-09-25, so a step telling an operator to run one would be telling them
        // to run something the tool refuses — the exact fault this comment was written about,
        // arrived at a second time by a different route.
        Assert.Contains("parley terminal", said, StringComparison.Ordinal);
        Assert.DoesNotContain("--node-config", said, StringComparison.Ordinal);
        Assert.DoesNotContain("--db", said, StringComparison.Ordinal);
        Assert.DoesNotContain("--listen", said, StringComparison.Ordinal);
    }
}
