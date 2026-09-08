using Parley.Setup;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Parley.Cli.Top;
using Parley.Client.Views;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// Making a repository an agent of a project, from the agents region.
/// </summary>
/// <remarks>
/// <para>
/// User Story 3. The two files are the whole of what makes an agent an agent on this machine, and
/// both are asserted on disk rather than through the report the surface prints — a report is a
/// claim about a write, and the failure worth catching is a write that did not happen.
/// </para>
/// <para>
/// <b>The refusals are the probes.</b> Every one of them is a case where the tempting
/// implementation writes something: a directory that is not a repository, a repository that
/// already belongs to another project, a role the catalogue already holds. FR-009 says nothing is
/// written on refusal, and that is checked by comparing what is on disk before and after.
/// </para>
/// </remarks>
public sealed class AddingTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-adding-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public AddingTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        _connection = ParleyDatabase.OpenInMemory();
        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();

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

    private string ManifestPath => Path.Combine(_root, "projects.json");

    private string Store => Path.Combine(_root, "acme.db");

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    [Fact]
    public async Task Adding_writes_both_files_and_says_which()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await AddAsync(driving, shell, Workspace, "orders");

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;
        var env = launch["mcpServers"]!["parley"]!["env"]!;

        Assert.Equal("orders", env["PARLEY_AGENT"]!.GetValue<string>());
        Assert.Equal("acme", env[EnrollmentPlan.ProjectVariable]!.GetValue<string>());

        var catalogue = JsonNode.Parse(
            await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken))!;

        Assert.Equal(Workspace, catalogue["agents"]!["orders"]!["workspace"]!.GetValue<string>());

        Assert.Contains(Adding.Added, shell.Said, StringComparison.Ordinal);
        Assert.Contains(LaunchFile, shell.Said, StringComparison.Ordinal);
        Assert.Contains(CataloguePath, shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_added_agent_is_one_this_machine_can_start()
    {
        // **This reverses 005's amendment and the reasoning behind it is worth keeping.** 005
        // recorded that an added agent could not be promised as `startable`, because §14 defines
        // that for an agent the registry holds and nothing here may register on a repository's
        // behalf. Both halves of that are still true. What 005 missed is that §12.3's closing
        // paragraph routes spawn-on-demand through the manifest rather than through
        // self-registration -- so the agent is startable because the catalogue names it, and the
        // registration remains the agent's own act when it attaches.
        //
        // What is asserted is the fact, not the mechanism: the row says this machine can start it,
        // and it does not say either of the two phrases the owner could not read.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await AddAsync(driving, shell, Workspace, "orders");

        var standing = Assert.Single(shell.Agents.Standings, r => r.Name == "orders");

        Assert.True(standing.StartableHere);
        Assert.Equal(Standing.NeverRun, standing.Standing);
        Assert.DoesNotContain(
            AgentsRegion.CataloguedOnly, standing.Standing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_says_it_is_about_to_start_a_process_before_it_does()
    {
        // A process appearing on the operator's machine is not something a surface may do without
        // its description covering it. The two files were always named; the start is the third
        // thing and is named beside them.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await shell.Add();

        var form = (FormDialog)driving.Find(Adding.Title);

        form.Fill(0, Workspace);
        form.Fill(1, "orders");
        form.Finish(taken: true);

        Assert.Contains(Adding.Starts, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_asked_still_leaves_a_startable_agent()
    {
        // The files are written before the start is asked for, so a broker that did not answer
        // leaves exactly the state 005 shipped -- catalogued, and started by the next question for
        // it. Losing the whole enrolment because the last of three steps failed would be worse.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await AddAsync(driving, shell, Workspace, "orders");

        // The fixture's broker is not listening, which is what makes this the unreachable case.
        Assert.Contains(Adding.NotStarted, shell.Said, StringComparison.Ordinal);

        var standing = Assert.Single(shell.Agents.Standings, r => r.Name == "orders");

        Assert.True(standing.StartableHere);
    }

    [Fact]
    public async Task A_project_naming_no_catalogue_gains_one_and_is_told_so()
    {
        // FR-010. The catalogue is created beside the store and recorded on the project through
        // the command that owns the manifest, and the restart it causes is said rather than left
        // to be noticed as agents reconnecting.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: false);

        await AddAsync(driving, shell, Workspace, "orders");

        Assert.True(File.Exists(CataloguePath), "no catalogue was created");
        Assert.Contains("Created", shell.Said, StringComparison.Ordinal);
        Assert.Contains("broker restarts", shell.Said, StringComparison.Ordinal);

        Assert.True(ProjectManifest.TryRead(ManifestPath, out var projects, out _));
        Assert.Equal(CataloguePath, Assert.Single(projects).NodeConfig);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nowhere-at-all")]
    public void A_directory_that_is_not_a_repository_is_refused_in_the_field(string typed)
    {
        Assert.NotNull(Adding.WorkspaceProblem(typed));
    }

    [Fact]
    public void A_directory_with_no_git_is_refused_and_a_repository_is_not()
    {
        var bare = Path.Combine(_root, "not-a-repository");

        Directory.CreateDirectory(bare);

        Assert.Contains("no .git", Adding.WorkspaceProblem(bare), StringComparison.Ordinal);
        Assert.Null(Adding.WorkspaceProblem(Workspace));
    }

    [Fact]
    public void A_role_that_could_never_be_an_agent_name_is_refused_in_the_field()
    {
        Assert.NotNull(Adding.RoleProblem("Orders With Spaces"));
        Assert.Null(Adding.RoleProblem("orders"));
    }

    [Fact]
    public void An_ownership_line_longer_than_the_schema_allows_is_refused_in_the_field()
    {
        // Refused here rather than at registration, which happens a long way from this screen and
        // looks like an agent that simply never appeared.
        Assert.NotNull(Adding.OwnsProblem(new string('a', EnrollmentPlan.OwnsLimit + 1)));
        Assert.Null(Adding.OwnsProblem(string.Empty));
        Assert.Null(Adding.OwnsProblem("Carts and order totals. No tax, no stock."));
    }

    [Fact]
    public async Task A_repository_that_already_belongs_to_another_project_is_refused_and_nothing_is_written()
    {
        // The probe FR-009 exists for. Silently re-pointing would move an agent out of one
        // deployment's roster and into another's, from a surface whose person may not know the
        // first one exists.
        await File.WriteAllTextAsync(
            LaunchFile,
            """
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_PROJECT": "elsewhere" } }
              }
            }
            """,
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await AddAsync(driving, shell, Workspace, "orders");

        Assert.Contains("elsewhere", shell.Said, StringComparison.Ordinal);
        Assert.Contains(Adding.NothingWritten, shell.Said, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("orders", await CatalogueTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_confirmation_writes_nothing()
    {
        // The description is not decorative: it is offered so somebody can say no to it, and
        // saying no has to leave the repository exactly as it was.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await shell.Add();

        var form = (FormDialog)driving.Find(Adding.Title);

        form.Fill(0, Workspace);
        form.Fill(1, "orders");
        form.Finish(taken: true);

        var asking = (ChoiceDialog)driving.Find(Adding.ConfirmTitle);

        Assert.Equal(Adding.NotNow, asking.Chosen);

        driving.Enter();
        await shell.Settled;

        Assert.Equal(Adding.NothingWritten, shell.Said);
        Assert.False(File.Exists(LaunchFile), "a declined add wrote the launch configuration");
    }

    [Fact]
    public async Task Adding_the_same_repository_twice_changes_nothing_the_second_time()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving, catalogued: true);

        await AddAsync(driving, shell, Workspace, "orders");

        var launch = await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken);
        var catalogue = await CatalogueTextAsync();

        await AddAsync(driving, shell, Workspace, "orders");

        Assert.Equal(launch, await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken));
        Assert.Equal(catalogue, await CatalogueTextAsync());
        Assert.Contains("already correct", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void What_would_be_written_names_both_files_and_the_project()
    {
        // Shown before anything is written, because this writes into a repository that is not the
        // one the person is looking at.
        var said = Adding.Describe(Workspace, CataloguePath, "acme");

        Assert.Contains(Enrolment.LaunchPath(Workspace), said, StringComparison.Ordinal);
        Assert.Contains(CataloguePath, said, StringComparison.Ordinal);
        Assert.Contains("acme", said, StringComparison.Ordinal);
    }

    private string CataloguePath => Path.Combine(_root, "acme-node.json");

    private async Task<string> CatalogueTextAsync() =>
        File.Exists(CataloguePath)
            ? await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken)
            : string.Empty;

    /// <summary>Opens the surface on a manifest naming one project.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="catalogued">Whether the project already names a catalogue.</param>
    /// <returns>The surface, with its projects read.</returns>
    private async Task<Shell> OpenAsync(Driving.Session driving, bool catalogued)
    {
        var project = new ProjectSettings(
            ProjectName.Create("acme"),
            Store,
            _broker.Address,
            catalogued ? CataloguePath : null,
            null);

        ProjectManifest.Write(ManifestPath, [project]);

        if (catalogued)
        {
            await File.WriteAllTextAsync(
                CataloguePath, "{\n  \"agents\": {}\n}\n", TestContext.Current.CancellationToken);
        }

        var directory = BrokerDirectory.FromManifest(ManifestPath);
        var shell = TopCommand.Build(directory, manifestPath: ManifestPath);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }

    /// <summary>Fills in the form and confirms it.</summary>
    private static async Task AddAsync(
        Driving.Session driving, Shell shell, string workspace, string role)
    {
        await shell.Add();

        var form = (FormDialog)driving.Find(Adding.Title);

        form.Fill(0, workspace);
        form.Fill(1, role);
        form.Finish(taken: true);

        var asking = (ChoiceDialog)driving.Find(Adding.ConfirmTitle);

        driving.Row(asking.Choices, Adding.Write);
        driving.Enter();

        await shell.Settled;
    }
}
