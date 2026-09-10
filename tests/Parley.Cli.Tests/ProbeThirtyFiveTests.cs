using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// Review gate, lens 2, run 24: what one unattended start actually starts.
/// </summary>
/// <remarks>
/// <para>
/// FR-013 makes consent to run unattended per project, and enforces it as <i>one workspace, one
/// project's catalogue</i> — because a node starts an agent with the workspace as its working
/// directory, and two nodes doing that is two models editing one checkout.
/// </para>
/// <para>
/// <b>That is not the only thing one start starts.</b> The client the node launches reads the
/// <c>.mcp.json</c> in that directory and brings up <i>every</i> server in it — which, after this
/// feature, is one per project the repository answers in. So a node acting on alpha's consent also
/// attaches the repository as beta's agent and gamma's, in projects whose catalogues say nothing about
/// starting it unattended, and a peer in either can then have it answer with nobody watching.
/// </para>
/// <para>
/// The tooling cannot change what a client loads. What it can do is not let this be discovered
/// afterwards: the person granting the consent is the only one in a position to weigh it, and the
/// facts are all in the file in front of them.
/// </para>
/// </remarks>
public sealed class ProbeThirtyFiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe35-" + Path.GetRandomFileName());

    public ProbeThirtyFiveTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Workspace => Path.Combine(_root, "shared");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string ManifestPath => Path.Combine(_root, "projects.json");

    /// <summary>The three projects, as a machine's manifest holds them.</summary>
    /// <remarks>
    /// Passed on every call, because the terminal passes it on every call. A probe that withheld it
    /// would be asking the code to know something its caller never told it, and the answer would say
    /// more about the probe than about the code.
    /// </remarks>
    private IReadOnlyList<ProjectSettings> Deployment()
    {
        var projects = new List<ProjectSettings>();

        foreach (var (name, port) in ((string, int)[])[("alpha", 7001), ("beta", 7002), ("gamma", 7003)])
        {
            projects.Add(new ProjectSettings(
                ProjectName.Create(name),
                Path.Combine(_root, $"{name}.db"),
                $"http://127.0.0.1:{port}",
                Path.Combine(_root, $"{name}-node.json"),
                null));
        }

        ProjectManifest.Write(ManifestPath, projects);

        return projects;
    }

    private Task<Enrolment.Outcome> EnrolAsync(string project, bool unattended) =>
        Enrolment.AddAsync(
            new EnrollmentEntry(Workspace, "infra", "Pipelines and images. Not the product.", unattended)
            {
                Enroll = true,
                OwnsApproved = true,
            },
            project,
            Path.Combine(_root, $"{project}-node.json"),
            Path.Combine(Workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken,
            Deployment(),
            ManifestPath);

    [Fact]
    public async Task Granting_an_unattended_start_says_which_other_projects_it_brings_up()
    {
        // The case the feature exists for: one repository several product lines depend on. Two of
        // them get an attended entry; the third grants the unattended start — and gets all three.
        await EnrolAsync("alpha", unattended: false);
        await EnrolAsync("beta", unattended: false);

        var outcome = await EnrolAsync("gamma", unattended: true);

        var said = string.Join(" | ", outcome.Notes);

        Assert.Contains("alpha", said, StringComparison.Ordinal);
        Assert.Contains("beta", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attended_enrolment_beside_an_unattended_one_says_it_too()
    {
        // The other order, and the one that is easier to miss: the consent was granted first and is
        // widened by an enrolment that is not itself about running unattended. Nothing re-reads the
        // catalogue afterwards, so this is the only moment anybody could be told.
        await EnrolAsync("alpha", unattended: true);

        var outcome = await EnrolAsync("beta", unattended: false);

        Assert.Contains(
            outcome.Notes,
            note => note.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_only_project_there_is_said_nothing_about()
    {
        // The control. A repository that answers in one project has nothing to warn about, and a
        // note on every enrolment is a note nobody reads.
        var outcome = await EnrolAsync("alpha", unattended: true);

        Assert.DoesNotContain(
            outcome.Notes,
            note => note.Contains("also", StringComparison.OrdinalIgnoreCase)
                    && note.Contains("unattended", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_entries_really_are_all_in_the_one_file()
    {
        // The fact the warning rests on, asserted rather than assumed: three servers in one
        // `.mcp.json`, which is what the client in that working directory will read.
        await EnrolAsync("alpha", unattended: false);
        await EnrolAsync("beta", unattended: false);
        await EnrolAsync("gamma", unattended: true);

        var servers = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!["mcpServers"]!;

        Assert.Equal(
            ["parley-alpha", "parley-beta", "parley-gamma"],
            servers.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
    }
}
