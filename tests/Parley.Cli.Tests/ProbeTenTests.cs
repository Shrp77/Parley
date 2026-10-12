using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 1, run 10: the feature's own user story, driven through the commands.
/// </summary>
/// <remarks>
/// <para>
/// Nine runs have probed units and races. Not one has run the thing the feature exists for through
/// the surface a person uses: scan, then apply into one project, then apply into a second, then scan
/// again and see both. Every part of that has a test; the sequence has none, and a sequence is where
/// the parts turn out to disagree.
/// </para>
/// <para>
/// It is also the one path that would catch a fix from any earlier run having quietly changed what
/// gets written — the assertions here are about the file a person ends up with, not about the
/// functions that produced it.
/// </para>
/// </remarks>
public sealed class ProbeTenTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe10-" + Path.GetRandomFileName());

    // **Both brokers actually listen.** Enrolment refuses a project whose broker does not answer,
    // because adding an agent has to leave it registered — so a fixture naming an endpoint nothing
    // serves stopped describing a deployment this command will write into.
    private readonly ServedProject _alpha;
    private readonly ServedProject _beta;

    public ProbeTenTests()
    {
        Directory.CreateDirectory(Checkouts);
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        _alpha = ServedProject.Start("alpha", Path.Combine(_root, "alpha.db"), CatalogueFor("alpha"));
        _beta = ServedProject.Start("beta", Path.Combine(_root, "beta.db"), CatalogueFor("beta"));

        ProjectManifest.Write(ManifestPath, [Project("alpha"), Project("beta")]);
    }

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Checkouts => Path.Combine(_root, "checkouts");

    private string Workspace => Path.Combine(Checkouts, "inventory");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    // **A root, not the old manifest file.** Writing through a path ending in `projects.json`
    // creates a *directory* of that name and every read of it afterwards is refused — which is
    // why `ProjectManifest.Write` now refuses it too.
    private string ManifestPath => _root;

    private string CatalogueFor(string project) => Path.Combine(_root, $"{project}-node.json");

    private ProjectSettings Project(string name) =>
        name == "alpha" ? _alpha.Settings : _beta.Settings;

    /// <summary>The row a surface hands back for this repository, ticked and approved.</summary>
    /// <remarks>
    /// It was a manifest per project that this wrote and the command read straight back; it is the
    /// one row that file held. What the sequence below is about — two projects, one repository, and
    /// each keeping its own name for it — is decided from the row either way.
    /// </remarks>
    private EnrollmentEntry Chosen(string agent, string owns) =>
        new(Workspace.Replace(Path.DirectorySeparatorChar, '/'), agent, owns, Unattended: false)
        {
            Enroll = true,
        };

    private async Task<(int Code, string Output)> ApplyAsync(string project, EnrollmentEntry chosen)
    {
        var output = new StringWriter();

        var code = await EnrollCommand.EnrolAsync(
            [chosen],
            describe: false,
            new Resolved(CatalogueFor(project), ValueSource.Argument),
            output,
            new StringWriter(),
            Project(project),
            [Project("alpha"), Project("beta")],
            ManifestPath,
            TestContext.Current.CancellationToken);

        return (code, output.ToString());
    }

    private async Task<JsonObject> ServersAsync() =>
        (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

    [Fact]
    public async Task One_repository_enrolled_into_two_projects_from_the_command_line()
    {
        // User Story 1, through the surface rather than through Describe. Two applies, two projects,
        // one repository — and the second must leave the first exactly as it was, because that is
        // what "answers in both" means on disk.
        var (first, _) = await ApplyAsync("alpha", Chosen("inventory", "Stock levels. Not pricing."));

        Assert.Equal(0, first);

        var alphaEntry = (await ServersAsync())["parley-alpha"]!.ToJsonString();

        var (second, _) = await ApplyAsync("beta", Chosen("stock", "What is on hand."));

        Assert.Equal(0, second);

        var servers = await ServersAsync();

        Assert.Equal(
            ["parley-alpha", "parley-beta"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));

        Assert.Equal(alphaEntry, servers["parley-alpha"]!.ToJsonString());
    }

    [Fact]
    public async Task Each_project_keeps_its_own_name_and_its_own_owns_line()
    {
        await ApplyAsync("alpha", Chosen("inventory", "Stock levels. Not pricing."));
        await ApplyAsync("beta", Chosen("stock", "What is on hand."));

        var servers = await ServersAsync();

        Assert.Equal("inventory", servers["parley-alpha"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("stock", servers["parley-beta"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());

        Assert.Equal("alpha", servers["parley-alpha"]!["env"]!["PARLEY_PROJECT"]!.GetValue<string>());
        Assert.Equal("beta", servers["parley-beta"]!["env"]!["PARLEY_PROJECT"]!.GetValue<string>());

        Assert.NotEqual(
            servers["parley-alpha"]!["env"]!["PARLEY_OWNS"]!.GetValue<string>(),
            servers["parley-beta"]!["env"]!["PARLEY_OWNS"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawing_from_one_leaves_the_other_answering()
    {
        // User Story 3 at the end of User Story 1, which is the sequence a person actually performs:
        // enrol in two, decide one was wrong, take it out.
        await ApplyAsync("alpha", Chosen("inventory", "Stock levels. Not pricing."));
        await ApplyAsync("beta", Chosen("stock", "What is on hand."));

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "stock", "beta", CatalogueFor("beta"), TestContext.Current.CancellationToken);

        Assert.Contains(LaunchFile, outcome.Written);

        var servers = await ServersAsync();

        Assert.Equal(["parley-alpha"], servers.Select(pair => pair.Key));
        Assert.Equal("inventory", servers["parley-alpha"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enrolling_into_the_same_project_twice_reports_no_change()
    {
        // FR-015 through the surface: the run must not claim a change it did not make.
        var chosen = Chosen("inventory", "Stock levels. Not pricing.");

        await ApplyAsync("alpha", chosen);

        var (code, output) = await ApplyAsync("alpha", chosen);

        Assert.Equal(0, code);
        Assert.Contains("already correct", output, StringComparison.Ordinal);
        Assert.Single(await ServersAsync());
    }
}
