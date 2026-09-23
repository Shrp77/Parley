using Parley.Configuration;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That <c>enroll scan</c> runs on a machine naming several projects, and that its two siblings still do not.
/// </summary>
/// <remarks>
/// <para>
/// The refusal to choose between several configured projects was applied to the <c>enroll</c> verb rather than to its
/// subcommands, so all three were held to the requirement of two. <c>plan</c> and <c>apply</c> do need a project —
/// the key they write into each repository's launch configuration is <c>parley-&lt;project&gt;</c>. <c>scan</c> needs
/// nothing: it walks one directory, decides which subdirectories are git repositories, and writes a file for a person
/// to edit. No broker, no catalogue, no launch configuration, and no project named anywhere in what it produces.
/// </para>
/// <para>
/// <b>What that cost was the wrong way round.</b> The one step whose whole job is to survey a machine was the one step
/// that could not be run on a machine with several projects on it — which is the machine worth surveying. Found by the
/// owner trying to re-enrol nineteen repositories after 018 made every catalogue entry unreadable.
/// </para>
/// <para>
/// Driven through the real binary rather than through <c>EnrollCommand</c>, because the refusal is in the entry point
/// and an in-process call never reaches it. That is the same reason <c>FilesCommand.OwnOptions</c> is tested this way:
/// half of <c>parley files</c> was unreachable from a shell while every in-process test passed.
/// </para>
/// </remarks>
public sealed class ScanNeedsNoProjectTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-scan-" + Guid.NewGuid().ToString("N"));

    public ScanNeedsNoProjectTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Checkouts);

        // A directory is a repository when git says so, which is what the scan filters on. The marker is enough:
        // nothing here runs git, and a scan that shelled out would be a scan that needs git on the machine.
        Directory.CreateDirectory(Path.Combine(Checkouts, "orders"));
        Directory.CreateDirectory(Path.Combine(Checkouts, "orders", ".git"));

        ProjectManifest.Write(
            ManifestPath,
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

    private string ManifestPath => Path.Combine(_directory, "projects.json");

    private string Checkouts => Path.Combine(_directory, "checkouts");

    private string Output => Path.Combine(_directory, "agents.json");

    [Fact]
    public void Scanning_is_not_refused_for_naming_no_project()
    {
        var (code, _, error) = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, ManifestPath)], "enroll", "scan", Checkouts, Output, "--none");

        Assert.Equal(0, code);
        Assert.DoesNotContain("alpha, beta", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Output), "the scan wrote no manifest");
    }

    [Fact]
    public void What_it_writes_names_the_repository_and_no_project()
    {
        _ = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, ManifestPath)], "enroll", "scan", Checkouts, Output, "--none");

        var written = File.ReadAllText(Output);

        // The point of the exemption stated as a property of the artefact rather than of the exit code: there is
        // nothing in this file for a project to have decided, so being made to choose one was asking for an answer
        // that would have been written nowhere.
        Assert.Contains("orders", written, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", written, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Planning_still_has_to_be_told_which_project()
    {
        // The narrowness is the whole of it. `plan` reports the `parley-<project>` key it would write, so a plan that
        // guessed would describe changes to a deployment nobody named — and this machine has two.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)], "enroll", "plan", Output);

        Assert.Equal(78, code);
        Assert.Contains("alpha, beta", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_named_and_not_described_is_still_refused_for_a_scan()
    {
        // Exempt from having to *choose* a project, never from being told a name that is wrong — the same line the
        // explicit-address exemption beside it holds, and for the same reason: a misspelling answered by silence is
        // how somebody enrols against a deployment they did not mean.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, ManifestPath)],
            "--project", "gamma", "enroll", "scan", Checkouts, Output, "--none");

        Assert.Equal(78, code);
        Assert.Contains("No project named 'gamma'", error, StringComparison.Ordinal);
    }
}
