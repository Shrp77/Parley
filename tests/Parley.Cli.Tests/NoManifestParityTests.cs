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

    /// <summary>A root in this test's own directory that has never been created.</summary>
    /// <remarks>
    /// **It named <c>projects.json</c> and names a directory now.** Keeping the old spelling would have
    /// tested the compatibility rule rather than the absent case: a value whose final segment is the
    /// manifest's file name resolves to its parent, so this would have pointed at a directory that
    /// exists and holds this test's own scratch files.
    /// </remarks>
    private string Absent => Path.Combine(_directory, "never-configured");

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

        Assert.Equal(BrokerEndpoint.Default, ProjectResolution.Endpoint(null, NoVariables, project).Value);
        Assert.Equal(NodeCatalogue.DefaultFileName, ProjectResolution.NodeConfig(null, NoVariables, null).Value);
    }

    [Theory]
    // PARLEY_DB was here and resolves nothing now: a project's store is derived from its name, so
    // there is no variable a machine could set to change it.
    [InlineData(BrokerEndpoint.Variable, "http://env:1")]
    [InlineData(NodeCatalogue.PathVariable, "/from/env-node.json")]
    public void With_no_manifest_the_environment_still_decides(string name, string value)
    {
        Func<string, string?> variables = asked => asked == name ? value : null;

        var resolved = name == BrokerEndpoint.Variable
            ? ProjectResolution.Endpoint(null, variables, null)
            : ProjectResolution.NodeConfig(null, variables, null);

        Assert.Equal(value, resolved.Value);
        Assert.Equal(ValueSource.Environment, resolved.Source);
    }

    // ---- a manifest present does not take over --------------------------------------------------

    [Fact]
    public void An_explicit_value_still_wins_over_a_project_and_says_so()
    {
        // FR-009: the report says which input won, because the only thing worse than the wrong value is
        // the wrong value with nothing to say where it came from.
        //
        // **This was about `--db` beating a project's store, and that pairing no longer exists** — a
        // store is derived from the project's name, so there is nothing to override it with. The rule
        // survives its subject: `--broker` still beats a project's endpoint, and still says so.
        var project = new ProjectSettings(
            ProjectName.Create("orders"),
            "/srv/orders.db",
            "http://127.0.0.1:51801",
            null);

        var endpoint = ProjectResolution.Endpoint("http://given:2", NoVariables, project);

        Assert.Equal("http://given:2", endpoint.Value);
        Assert.Equal(ValueSource.Argument, endpoint.Source);
        Assert.Contains("(given)", endpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_no_manifest_still_resolves_a_catalogue()
    {
        // A machine may legitimately name no projects, and §4.1 keeps that supported. The catalogue is
        // the machine's rather than any project's since 018, so there is nothing for a project to fail
        // to supply — what has to hold is that the answer is still the one the node reaches.
        var node = ProjectResolution.NodeConfig(null, NoVariables, null);

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
        //
        // **Described rather than applied**, because the line is printed before anything is
        // written and nothing here should configure the machine the suite runs on. `--all` is what
        // reaches that line at all: with stdin redirected there is no picker, and a run with
        // nothing ticked is refused before the catalogue is named.
        var checkouts = Path.Combine(_directory, "checkouts");

        Directory.CreateDirectory(Path.Combine(checkouts, "orders", ".git"));

        var (_, output, _) = ToolProcess.RunFully(
            [(ProjectManifest.PathVariable, Absent)],
            "enroll", checkouts, "--all", "--describe");

        Assert.Contains("node catalogue:", output, StringComparison.Ordinal);
        Assert.Contains(NodeCatalogue.DefaultFileName, output, StringComparison.Ordinal);

        // And says where that came from, without mentioning the manifest it does not have.
        Assert.Contains("(default)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectManifest.FileName, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serve_with_nothing_configured_refuses_and_says_how_to_configure_something()
    {
        // **The refusal used to be about the database, and had to be.** While `--db` and `PARLEY_DB`
        // existed, a machine with nothing configured was missing a *store*, and a message about
        // projects would have been a different answer to the same mistake on every machine that had
        // never heard of one.
        //
        // There is no store to be missing now. What such a machine lacks is a project, and the refusal
        // says exactly that and how to make one — which is the whole of what it can usefully say.
        //
        // **And it must refuse rather than wait.** `serve --config <root>` waits, because that is the
        // entry an installer registers and a project named an hour later must come up without it
        // changing. `parley serve` typed by hand does not: making both wait hung the suite at 4,265
        // lines with five orphaned brokers and no failure.
        var (code, error) = ToolProcess.Run(
            [(ProjectManifest.PathVariable, Absent)], "serve");

        Assert.Equal(78, code);
        Assert.Contains("nothing to serve", error, StringComparison.Ordinal);
        Assert.Contains("parley terminal", error, StringComparison.Ordinal);

        // Nothing about a store, because there is nothing a person could do about one.
        Assert.DoesNotContain("--db", error, StringComparison.Ordinal);
        Assert.DoesNotContain(DeploymentEnvironment.DatabaseVariable, error, StringComparison.Ordinal);
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
