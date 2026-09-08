using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That a machine with no manifest is untouched by there being such a thing as a manifest.
/// </summary>
/// <remarks>
/// <para>
/// This whole feature is additive or it is a migration, and the difference is not a matter of
/// degree: every deployment configured before it existed keeps working, or every one of them has
/// to be changed on the day somebody runs <c>dotnet tool update</c>. The story this suite serves
/// is defined entirely by what does not happen, which makes it almost all tests and no
/// implementation — that is correct rather than a gap.
/// </para>
/// <para>
/// <b>Including that nothing is said.</b> A tool that mentions a missing optional file on every
/// run has made it mandatory in practice: the person reading the line does not know it is
/// optional, and the cheapest way to make the message stop is to create the file. So the absence
/// of the manifest must be as invisible as it was before the manifest was invented, and that is
/// asserted on standard output rather than reasoned about.
/// </para>
/// <para>
/// Both halves are checked, and they check different things. The resolution cases are pure and
/// exhaustive; the process cases are slower and few, and they exist because the pure function
/// being right proves nothing about whether the entry point calls it. The defect that started
/// this feature lived in exactly that gap.
/// </para>
/// </remarks>
public sealed class NoManifestParityTests : IDisposable
{
    private static readonly Func<string, string?> NoVariables = _ => null;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "parley-cli-parity-" + Guid.NewGuid().ToString("N"));

    public NoManifestParityTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>A path in this test's own directory where no file has been written.</summary>
    private string Absent => Path.Combine(_directory, "projects.json");

    // ---- nothing configured resolves exactly as it did ----------------------------------------

    [Fact]
    public void An_absent_manifest_is_not_an_error_and_describes_nothing()
    {
        Assert.True(ProjectManifest.TryRead(Absent, out var projects, out var problem));
        Assert.Empty(projects);
        Assert.Null(problem);
    }

    [Fact]
    public void With_no_manifest_every_value_is_what_it_was_before()
    {
        // Value for value, which is what "additive" has to mean to be worth asserting. The node
        // catalogue in particular: a bare file name resolved against the working directory, which
        // is the resolution that caused the defect this feature exists for — and changing only
        // this side of it would reintroduce the same disagreement in the other direction, because
        // the node itself still resolves it that way.
        Assert.True(ProjectResolution.TrySelect(null, [], out var project, out _));
        Assert.Null(project);

        Assert.Null(ProjectResolution.Store(null, NoVariables, project).Value);
        Assert.Equal(BrokerEndpoint.Default, ProjectResolution.Endpoint(null, NoVariables, project).Value);
        Assert.Equal(NodeCatalogue.DefaultFileName, ProjectResolution.NodeConfig(null, NoVariables, project).Value);
    }

    [Theory]
    [InlineData(DeploymentEnvironment.DatabaseVariable, "/from/env.db")]
    [InlineData(BrokerEndpoint.Variable, "http://env:1")]
    [InlineData(NodeCatalogue.PathVariable, "/from/env-node.json")]
    public void With_no_manifest_the_environment_still_decides(string name, string value)
    {
        Func<string, string?> variables = asked => asked == name ? value : null;

        var resolved = name switch
        {
            DeploymentEnvironment.DatabaseVariable => ProjectResolution.Store(null, variables, null),
            BrokerEndpoint.Variable => ProjectResolution.Endpoint(null, variables, null),
            _ => ProjectResolution.NodeConfig(null, variables, null),
        };

        Assert.Equal(value, resolved.Value);
        Assert.Equal(ValueSource.Environment, resolved.Source);
    }

    // ---- a manifest present does not take over --------------------------------------------------

    [Fact]
    public void An_explicit_value_still_wins_over_a_project_and_says_so()
    {
        // FR-009. Somebody who has always passed --db keeps passing --db, and the report says
        // which input won — because the only thing worse than the wrong path is the wrong path
        // with nothing to say where it came from.
        var project = new ProjectSettings(
            ProjectName.Create("orders"), "/srv/orders.db", "http://127.0.0.1:51801", null, null);

        var store = ProjectResolution.Store("/given/here.db", NoVariables, project);

        Assert.Equal("/given/here.db", store.Value);
        Assert.Equal(ValueSource.Argument, store.Source);
        Assert.Contains("(given)", store.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_a_project_does_not_supply_falls_back_rather_than_failing()
    {
        // A project may legitimately start nothing on this machine, so its node catalogue is
        // absent rather than wrong. Treating a partially specified project as broken would make
        // the optional fields mandatory, which is the same mistake as reporting the missing file.
        var project = new ProjectSettings(
            ProjectName.Create("orders"), "/srv/orders.db", "http://127.0.0.1:51801", null, null);

        var node = ProjectResolution.NodeConfig(null, NoVariables, project);

        Assert.Equal(NodeCatalogue.DefaultFileName, node.Value);
        Assert.Equal(ValueSource.Default, node.Source);
    }

    // ---- and the program itself behaves the same ------------------------------------------------

    [Fact]
    public void A_command_with_no_manifest_never_mentions_one()
    {
        // Through the real binary, because the pure function above being right says nothing about
        // whether the entry point consults it — and a line printed before dispatch would be
        // invisible to every test in this file that is not this one.
        var (code, output, error) = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, Absent)], "--help");

        Assert.Equal(0, code);

        // Named phrases rather than the word "manifest", which the usage text uses for the
        // enrollment catalogue and has done since before any of this — a blunter check fails on
        // an unrelated line and teaches whoever meets it to loosen the assertion rather than read
        // it. What must not appear is anything reporting the file's absence.
        foreach (var stream in new[] { output, error })
        {
            Assert.DoesNotContain(ProjectManifest.FileName, stream, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("No projects configured", stream, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Absent, stream, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Enroll_names_the_catalogue_it_would_write_even_with_no_manifest()
    {
        // The line this whole feature exists to produce, on the machine that produced the defect.
        // That run had no manifest and no PARLEY_NODE_CONFIG, so the catalogue fell all the way
        // through to `node.json` in whatever directory the command was typed in — ten repositories
        // recorded as consenting to run unattended, in a file the node does not read, reported as
        // success.
        //
        // Gating the report on a project having been selected, which is how it was first written,
        // shows it only to somebody who has already adopted the fix. It is asserted here rather
        // than in the project tests for exactly that reason.
        var (_, output, _) = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, Absent)],
            "enroll", "plan", Path.Combine(_directory, "absent-manifest.json"));

        Assert.Contains("node catalogue:", output, StringComparison.Ordinal);
        Assert.Contains(NodeCatalogue.DefaultFileName, output, StringComparison.Ordinal);

        // And says where that came from, without mentioning the manifest it does not have.
        Assert.Contains("(default)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectManifest.FileName, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serve_with_no_manifest_and_no_store_refuses_for_the_reason_it_always_did()
    {
        // The refusal must still be about the database. If the manifest had taken over the
        // selection, this would now fail with something about projects — a different message for
        // the same mistake, on every machine that has never heard of a project.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, Absent)], "serve");

        Assert.Equal(78, code);
        Assert.Contains(DeploymentEnvironment.DatabaseVariable, error, StringComparison.Ordinal);
        Assert.DoesNotContain("--project", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_command_with_no_manifest_still_fails_on_the_broker()
    {
        // Nothing is listening on the address this resolves to, and that is the point: the
        // failure has to be "no broker there", which is what it was before. A manifest-shaped
        // refusal here would mean an existing deployment stopped working the day it upgraded.
        var (code, error) = ToolProcess.Run(
            [
                (ProjectManifest.PathVariable, Absent),
                (BrokerEndpoint.Variable, "http://127.0.0.1:1"),
            ],
            "registry");

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("--project", error, StringComparison.Ordinal);
        Assert.DoesNotContain("projects.json", error, StringComparison.OrdinalIgnoreCase);
    }
}
