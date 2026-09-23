using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That adding and withdrawing write inside the two files they name, and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.2. This surface writes into repositories that are not the one
/// it is running in, on a path a person typed — so the question is not whether the happy path works
/// but what the unhappy paths reach. A relative path, a trailing separator, a path with a parent
/// segment in it, and a workspace that is the project's own directory are all things somebody can
/// type by accident.
/// </para>
/// <para>
/// <b>The catalogue is a shared file and the launch configuration is somebody else's.</b> A write
/// that landed outside the workspace, or that took another agent's line with it, would be a defect
/// nothing else on this machine would notice.
/// </para>
/// </remarks>
public sealed class WritingBoundaryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-boundary-" + Path.GetRandomFileName());

    public WritingBoundaryTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));
        Directory.CreateDirectory(Other);
        Directory.CreateDirectory(Path.Combine(Other, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }

    private string Workspace => Path.Combine(_root, "orders");

    private string Other => Path.Combine(_root, "billing");

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    [Fact]
    public async Task Adding_writes_the_launch_configuration_inside_the_workspace_and_nowhere_above_it()
    {
        await AddAsync(Workspace, "orders");

        Assert.True(File.Exists(Path.Combine(Workspace, ".mcp.json")));

        // Nothing above it, which is the file a path with a parent segment would have reached.
        Assert.False(File.Exists(Path.Combine(_root, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(Other, ".mcp.json")));
    }

    [Fact]
    public async Task A_workspace_with_a_trailing_separator_is_the_same_workspace()
    {
        // Typed by anybody who tab-completed a directory. Two entries for one repository would be
        // two agents the node would try to start in the same place.
        await AddAsync(Workspace + Path.DirectorySeparatorChar, "orders");

        var agents = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!["agents"]!;

        var recorded = agents[IdentityOf(Workspace).ToString()]!["workspace"]!.GetValue<string>();

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Workspace)),
            Path.TrimEndingDirectorySeparator(recorded));
    }

    [Fact]
    public async Task Withdrawing_one_agent_leaves_another_agents_launch_configuration_alone()
    {
        // The probe. Both repositories are agents of the same project, and both have the same file
        // name in different directories -- so a withdrawal that resolved a path from the catalogue
        // rather than from the agent being withdrawn would take the wrong one.
        await AddAsync(Workspace, "orders");
        await AddAsync(Other, "billing");

        var mine = IdentityOf(Workspace);
        var theirs = IdentityOf(Other);

        var untouched = await File.ReadAllTextAsync(
            Path.Combine(Other, ".mcp.json"), TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CataloguePath, TestContext.Current.CancellationToken);

        Assert.Equal(
            untouched,
            await File.ReadAllTextAsync(
                Path.Combine(Other, ".mcp.json"), TestContext.Current.CancellationToken));

        var agents = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!["agents"]!;

        Assert.False(agents.ContainsKey(mine.ToString()));
        Assert.True(
            agents.ContainsKey(theirs.ToString()), "the other agent's catalogue line went too");
    }

    [Fact]
    public async Task Withdrawing_a_checkout_the_catalogue_holds_at_a_different_path_still_takes_its_line()
    {
        // A hand-edited catalogue, or one written before a repository moved. The line names a
        // directory this withdrawal is not about.
        //
        // **What decides which line goes is the identity the withdrawn checkout declares** (018), where
        // it used to be the name typed. The entry is this checkout's however stale the path in it, so it
        // goes — and the note still names the path, so a person can see it was not the one they were
        // looking at. Removing by name alone was the weaker rule: a name is what one project calls
        // something, and a second checkout can claim it.
        await AddAsync(Workspace, "orders");

        var mine = IdentityOf(Workspace);

        // The catalogue now says this checkout lives somewhere else entirely.
        var catalogue = JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!;

        catalogue["agents"]![mine.ToString()]!["workspace"] = Other;

        await File.WriteAllTextAsync(
            CataloguePath, catalogue.ToJsonString(), TestContext.Current.CancellationToken);

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", CataloguePath, TestContext.Current.CancellationToken);

        // It is removed, because the name is what identifies an agent to the broker and to the
        // node -- but the report says which workspace the line named, so a person can see that it
        // was not the one they were looking at.
        Assert.Contains(outcome.Notes, note => note.Contains("orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Withdrawing_from_a_project_that_names_no_catalogue_still_takes_the_launch_configuration()
    {
        await AddAsync(Workspace, "orders");

        var outcome = await Enrolment.WithdrawAsync(
            Workspace, "orders", "acme", null, TestContext.Current.CancellationToken);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(Workspace, ".mcp.json"), TestContext.Current.CancellationToken))!;

        Assert.False(((JsonObject)launch["mcpServers"]!).ContainsKey("parley"));
        Assert.Contains(outcome.Notes, note => note.Contains("no catalogue", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_path_is_accepted_and_resolved_rather_than_written_as_typed()
    {
        // Whatever a person types, what reaches the two files has to be a path a node on this
        // machine can start something in -- a relative one would resolve against whatever
        // directory that node happened to be launched from.
        Assert.Null(Adding.WorkspaceProblem(Workspace));

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Workspace);

        Assert.Null(Adding.WorkspaceProblem(relative));
    }

    /// <summary>Which agent a checkout says it is.</summary>
    /// <param name="workspace">The checkout.</param>
    /// <returns>Its identity.</returns>
    private static AgentIdentity IdentityOf(string workspace)
    {
        Assert.True(LaunchConfiguration.TryRead(workspace, out var declared, out var problem), problem);

        return declared.Identity;
    }

    private async Task AddAsync(string workspace, string role)
    {
        var entry = new EnrollmentEntry(
            Path.GetFullPath(Path.TrimEndingDirectorySeparator(workspace)),
            role,
            null,
            Unattended: true)
        {
            Enroll = true,
        };

        await Enrolment.AddAsync(
            entry,
            "acme",
            CataloguePath,
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);
    }
}
