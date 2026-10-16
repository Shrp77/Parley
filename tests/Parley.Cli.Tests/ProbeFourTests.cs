using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// What the guards cannot see, and what happens on the second attempt.
/// </summary>
/// <remarks>
/// Runs 1 to 3 attacked writes and races and found six defects. This one asks a different question:
/// where does each guard stop looking? A check that skips an input it does not recognise is a check
/// that passes on exactly the file somebody hand-edited, and hand-editing is the ordinary state of
/// every file this feature touches.
/// </remarks>
public sealed class ProbeFourTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-probe4-" + Path.GetRandomFileName());

    public ProbeFourTests() => Directory.CreateDirectory(_root);

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

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name).Replace('\\', '/');

        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        return path;
    }

    private string CatalogueFor(string project) => Path.Combine(_root, $"{project}-node.json");

    private ProjectSettings Project(string name) =>
        new(ProjectName.Create(name), Path.Combine(_root, $"{name}.db"), "127.0.0.1:51707", null);

    private void Catalogue(string project, string role, string workspace) =>
        File.WriteAllText(
            CatalogueFor(project),
            $$"""{ "agents": { "{{role}}": { "command": "claude", "workspace": "{{workspace}}" } } }""");

    // **There is no cross-project scan to hide a workspace from.** There is one catalogue per machine
    // keyed by the agent's identity, so a second project grants no second consent. The question of
    // pointing one identity at two working directories is asked in `EnrollingTwiceTests`, where a
    // copied repository is the one way left to do it.

    [Fact]
    public async Task Withdrawing_twice_says_so_and_changes_nothing_the_second_time()
    {
        var workspace = Workspace("orders");

        await Enrolment.AddAsync(
            new EnrollmentEntry(workspace, "orders", "Carts.", Unattended: true)
            {
                Enroll = true,
            },
            "acme",
            CatalogueFor("acme"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        await Enrolment.WithdrawAsync(
            workspace, "orders", "acme", CatalogueFor("acme"), TestContext.Current.CancellationToken);

        var launch = Path.Combine(workspace, ".mcp.json");
        var after = await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken);
        var catalogue = await File.ReadAllBytesAsync(CatalogueFor("acme"), TestContext.Current.CancellationToken);

        var second = await Enrolment.WithdrawAsync(
            workspace, "orders", "acme", CatalogueFor("acme"), TestContext.Current.CancellationToken);

        Assert.Empty(second.Written);
        Assert.Equal(after, await File.ReadAllBytesAsync(launch, TestContext.Current.CancellationToken));
        Assert.Equal(
            catalogue,
            await File.ReadAllBytesAsync(CatalogueFor("acme"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{ "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": "not an object" } } }""")]
    [InlineData("""{ "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": [] } } }""")]
    [InlineData("""{ "mcpServers": { "parley-alpha": "not an object at all" } }""")]
    [InlineData("""{ "mcpServers": { "parley-alpha": null } }""")]
    public void An_entry_of_the_wrong_shape_is_read_without_throwing(string launch)
    {
        // Every one of these is legal JSON somebody could leave behind. The reader must answer a
        // question about them rather than fail, because the caller's next move is to decide whether
        // it may write there -- and an exception at this point is a crash, not a refusal.
        var servers = JsonNode.Parse(launch)?["mcpServers"] as JsonObject;

        var entries = LaunchEntries.Read(servers);

        Assert.Single(entries);
        Assert.Null(entries[0].Agent);
    }

    [Fact]
    public void An_empty_project_variable_is_the_same_as_none()
    {
        // An empty string is what an editor leaves when somebody clears a field rather than
        // deleting the line.
        var servers = (JsonObject)JsonNode.Parse("""
            { "mcpServers": { "parley": { "command": "parley-mcp", "env": {
                "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "" } } } }
            """)!["mcpServers"]!;

        var (key, landed) = LaunchEntries.Landing(servers, null);

        Assert.Equal("parley", key);
        Assert.NotNull(landed);
        Assert.Null(landed.Project);
    }

    [Fact]
    public void A_project_name_at_its_full_length_survives_the_round_trip()
    {
        // 32 characters is the grammar's ceiling, so the key is 39. Nothing here truncates, but the
        // check is cheap and the failure would be a repository whose entry nobody can find again.
        var longest = "a" + new string('b', 31);

        Assert.True(ProjectName.IsValid(longest));

        var key = LaunchKey.For(longest);

        Assert.Equal(longest, LaunchKey.ProjectOf(key));
        Assert.True(LaunchKey.IsOurs(key));
    }

    [Fact]
    public async Task One_manifest_naming_one_workspace_twice_does_not_catalogue_it_twice()
    {
        // A cross-project comparison looks at *other* projects. Two rows for one workspace inside a single run
        // are a different shape, and the guard says nothing about them -- so this asks whether
        // anything else does.
        var workspace = Workspace("inventory");

        var problems = EnrollmentPlan.Validate(
            [
                new EnrollmentEntry(workspace, "inventory", "Stock.", Unattended: true)
                {
                    Enroll = true,
                },
                new EnrollmentEntry(workspace, "stock", "Stock again.", Unattended: true)
                {
                    Enroll = true,
                },
            ],
            Directory.Exists);

        Assert.NotEmpty(problems);
    }

    [Fact]
    public async Task A_launch_configuration_deleted_while_a_writer_waits_does_not_hang()
    {
        // The hold retries while the file exists. If the file goes away mid-wait the loop's guard
        // stops being true, and what must not happen is a spin that never ends.
        var workspace = Workspace("orders");
        var launch = Path.Combine(workspace, ".mcp.json");

        await File.WriteAllTextAsync(launch, """{ "mcpServers": {} }""", TestContext.Current.CancellationToken);

        var blocker = new FileStream(launch, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var enrolling = Enrolment.AddAsync(
            new EnrollmentEntry(workspace, "orders", "Carts.", Unattended: false)
            {
                Enroll = true,
            },
            "acme",
            CatalogueFor("acme"),
            Path.Combine(workspace, "CLAUDE.md"),
            TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        blocker.Dispose();

        try
        {
            File.Delete(launch);
        }
        catch (IOException)
        {
            // The writer took the file first, which is the ordinary outcome under load and not the
            // scenario this is about. The assertions below hold either way: whichever happened, it
            // must end within the bound rather than spin. **The delete tolerates losing the race**, because
            // the probe races its own subject in a full-suite sweep.
        }

        // Whatever it decides, it must decide within the bound rather than spinning.
        var finished = await Task.WhenAny(enrolling, Task.Delay(10_000, TestContext.Current.CancellationToken));

        Assert.Same(enrolling, finished);

        // And it must not have thrown something no caller handles.
        var failure = await Record.ExceptionAsync(() => enrolling);

        Assert.True(
            failure is null or IOException or InvalidDataException or UnauthorizedAccessException,
            $"threw {failure?.GetType().Name}");
    }
}
