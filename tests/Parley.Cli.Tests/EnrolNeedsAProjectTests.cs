using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That <c>parley enroll</c> refuses to guess which project it is enrolling into.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no exemption from naming a project.</b> Enrolment is one verb, and there is no
/// survey step that could run without one.
/// </para>
/// <para>
/// One verb has no such asymmetry. It writes <c>parley-&lt;project&gt;</c> into every repository it configures, so
/// there is no step left that could run without being told which deployment is meant. What survives is the other half
/// of the old rule, which is the half that still bites: a machine naming several projects and a command told none, or
/// told one that does not exist.
/// </para>
/// <para>
/// Driven through the real binary rather than through <c>EnrollCommand</c>, because the refusal is in the entry point
/// and an in-process call never reaches it. That is the same reason <c>FilesCommand.OwnOptions</c> is tested this way:
/// half of <c>parley files</c> was unreachable from a shell while every in-process test passed.
/// </para>
/// </remarks>
public sealed class EnrolNeedsAProjectTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-enrol-project-" + Guid.NewGuid().ToString("N"));

    public EnrolNeedsAProjectTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Checkouts);

        // A directory is a repository when git says so, which is what the scan filters on. The marker is enough:
        // nothing here runs git, and a scan that shelled out would be a scan that needs git on the machine.
        Directory.CreateDirectory(Path.Combine(Checkouts, "orders"));
        Directory.CreateDirectory(Path.Combine(Checkouts, "orders", ".git"));

        ProjectManifest.Write(
            Root,
            [
                new ProjectSettings(
                    ProjectName.Create("alpha"), Path.Combine(_directory, "a.db"), "http://127.0.0.1:2", null),
                new ProjectSettings(
                    ProjectName.Create("beta"), Path.Combine(_directory, "b.db"), "http://127.0.0.1:3", null),
            ]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // **The root rather than the manifest file.** `--config` and PARLEY_CONFIG name the directory
    // this machine keeps its Parley files in; the projects are the folders under it.
    private string Root => _directory;

    private string Checkouts => Path.Combine(_directory, "checkouts");

    private string Output => Path.Combine(_directory, "agents.json");

    [Fact]
    public void Enrolling_has_to_be_told_which_project_on_a_machine_naming_several()
    {
        // **The successor to `Planning_still_has_to_be_told_which_project`.** The narrowness is the whole of
        // it: enrolment writes the `parley-<project>` key into each repository, so a run that guessed would
        // put every one of them into a deployment nobody named — and this machine has two.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, Root)], "enroll", Checkouts, "--none");

        Assert.Equal(78, code);
        Assert.Contains("alpha, beta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_named_and_not_described_is_still_refused()
    {
        // Never exempt from being told a name that is wrong — the same line the explicit-address exemption
        // beside it holds, and for the same reason: a misspelling answered by silence is how somebody
        // enrols against a deployment they did not mean.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, Root)],
            "--project", "gamma", "enroll", Checkouts, "--none");

        Assert.Equal(78, code);
        Assert.Contains("No project named 'gamma'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_named_that_this_machine_describes_is_accepted()
    {
        // **The control, and the file is worth little without it.** Both tests above assert a refusal, and a
        // build where `enroll` refused every invocation would satisfy them. `--none` ticks nothing, so this
        // configures nothing and still has to get past the selection that the other two are about.
        var (_, _, error) = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, Root)],
            "--project", "alpha", "enroll", Checkouts, "--none");

        // **Asserted on which refusal it is, not on the exit code, and the first draft of this got it
        // wrong.** Failing to choose a project and ticking nothing both exit 78, so a code alone cannot
        // tell "refused before the command ran" from "ran and had nothing to do" — and it was the second
        // that happened here. What says selection was satisfied is that the command was reached at all.
        Assert.DoesNotContain("alpha, beta", error, StringComparison.Ordinal);
        Assert.Contains("Nothing was configured", error, StringComparison.Ordinal);
    }
}
