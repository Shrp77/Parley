using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;
using Parley.Store;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// One repository enrolled into two projects, driven through the screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's first report, end to end.</b> <c>SecondProjectTests</c> checks what the screen
/// says about it; this checks what lands on disk, because a report is a claim about a write and the
/// failure worth catching is a write that did not happen — or one that did and should not have.
/// </para>
/// <para>
/// Both halves matter and they pull opposite ways. The launch configuration must gain a second
/// entry, which is what makes the repository answer in the second project at all. The second
/// project's catalogue must gain nothing, which is FR-013: a node starts a client with the
/// repository as its working directory, and two of them is two models editing one checkout.
/// </para>
/// </remarks>
public sealed class TwoProjectsTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-two-" + Path.GetRandomFileName());

    // **A broker apiece, because the manifest refuses two projects on one port** — and it is right
    // to: only one broker can hold a port, and a fixture that shared one would be describing a
    // deployment that cannot exist.
    private readonly List<SqliteConnection> _connections = [];
    private readonly List<LoopbackOperator> _brokers = [];

    public TwoProjectsTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        foreach (var _ in new[] { "alpha", "beta" })
        {
            var connection = ParleyDatabase.OpenInMemory();

            _connections.Add(connection);
            _brokers.Add(LoopbackOperator.Start(connection));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var broker in _brokers)
        {
            await broker.DisposeAsync();
        }

        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

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

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    private string CatalogueFor(string project) => Path.Combine(_root, project + "-node.json");

    /// <summary>Opens the surface on a manifest naming two projects, both catalogued.</summary>
    private async Task<Shell> OpenAsync(Driving.Session driving)
    {
        var projects = new[] { "alpha", "beta" }
            .Select((name, at) => new ProjectSettings(
                ProjectName.Create(name),
                Path.Combine(_root, name + ".db"),
                _brokers[at].Address,
                CatalogueFor(name),
                null))
            .ToList();

        ProjectManifest.Write(ManifestPath, projects);

        foreach (var project in projects)
        {
            await File.WriteAllTextAsync(
                project.NodeConfig!, "{\n  \"agents\": {}\n}\n", TestContext.Current.CancellationToken);
        }

        var shell = TerminalCommand.Build(BrokerDirectory.FromManifest(ManifestPath), manifestPath: ManifestPath);

        driving.Show(shell);

        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        return shell;
    }

    /// <summary>Opens a project and adds this repository to it under a role.</summary>
    private async Task AddAsync(Driving.Session driving, Shell shell, string project, string role)
    {
        // In by the route a person takes: select the project and press Enter. What is being checked
        // is which project the add lands in, so arriving the direct way would settle by fiat the
        // question the test is asking.
        driving.Row(shell.Projects.List, project);
        driving.Enter();

        await shell.Settled;

        Assert.Equal(project, shell.Place.Project?.Value);

        await shell.Add();

        ((Browsing)driving.Find(Browsing.Name)).Choose(Workspace);

        var form = (FormDialog)driving.Find(Adding.Title);

        form.Fill(0, Workspace);
        form.Fill(1, role);
        form.Finish(taken: true);

        var asking = (ChoiceDialog)driving.Find(Adding.ConfirmTitle);

        driving.Row(asking.Choices, Adding.Write);
        driving.Enter();

        await shell.Settled;

        await Walking.GoAsync(shell, Client.Views.Viewing.Projects);
    }

    [Fact]
    public async Task The_same_repository_answers_in_both_projects()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await AddAsync(driving, shell, "alpha", "orders");
        await AddAsync(driving, shell, "beta", "orders");

        var launch = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        // Both entries, each naming its own project. This is the whole of what makes a repository
        // answer in two places: brokers do not federate, and membership is which endpoint it
        // connects to.
        Assert.NotNull(launch["mcpServers"]!["parley-alpha"]);
        Assert.NotNull(launch["mcpServers"]!["parley-beta"]);
    }

    [Fact]
    public async Task Only_the_first_project_may_start_it_unattended()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await AddAsync(driving, shell, "alpha", "orders");
        await AddAsync(driving, shell, "beta", "orders");

        var alpha = JsonNode.Parse(
            await File.ReadAllTextAsync(CatalogueFor("alpha"), TestContext.Current.CancellationToken))!;
        var beta = JsonNode.Parse(
            await File.ReadAllTextAsync(CatalogueFor("beta"), TestContext.Current.CancellationToken))!;

        Assert.NotNull(alpha["agents"]!["orders"]);
        Assert.Null(beta["agents"]!["orders"]);
    }

    [Fact]
    public async Task The_second_add_says_which_project_starts_it()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await AddAsync(driving, shell, "alpha", "orders");
        await AddAsync(driving, shell, "beta", "orders");

        // It was added, and it says why the second project's catalogue was left alone. Neither of
        // those was true before: the whole enrolment was refused.
        Assert.Contains(Adding.Added, shell.Said, StringComparison.Ordinal);
        Assert.Contains("alpha", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_name_does_not_buy_a_second_unattended_start()
    {
        // The owner tried this, and it has to keep not working: the rule is keyed on the workspace
        // because the hazard is two nodes running one working directory, and renaming the agent
        // does not make it two directories.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await AddAsync(driving, shell, "alpha", "orders");
        await AddAsync(driving, shell, "beta", "billing");

        var beta = JsonNode.Parse(
            await File.ReadAllTextAsync(CatalogueFor("beta"), TestContext.Current.CancellationToken))!;

        Assert.Null(beta["agents"]!["billing"]);

        // And it is still in the launch configuration under the name it was given here.
        var launch = JsonNode.Parse(
            await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        Assert.Equal(
            "billing",
            launch["mcpServers"]!["parley-beta"]!["env"]!["PARLEY_AGENT"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawing_from_one_project_leaves_the_other_entry_alone()
    {
        // **Lens 2, and it is 005's gate finding with a new cause.** That one caught a withdrawal
        // deleting a launch entry belonging to a different agent, because two rows pointed at one
        // workspace. This branch makes the same shape reachable from the screen for the first
        // time: one repository, two projects, two entries in one file. Removing one must leave the
        // other exactly as it was, and the damage in 005 was not the deletion — it was that the
        // surface reported success.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await AddAsync(driving, shell, "alpha", "orders");
        await AddAsync(driving, shell, "beta", "orders");

        var token = TestContext.Current.CancellationToken;

        await Enrolment.WithdrawAsync(Workspace, "orders", "beta", CatalogueFor("beta"), token);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, token))!;

        Assert.Null(launch["mcpServers"]!["parley-beta"]);
        Assert.NotNull(launch["mcpServers"]!["parley-alpha"]);

        // And the project that starts it still does.
        var alpha = JsonNode.Parse(await File.ReadAllTextAsync(CatalogueFor("alpha"), token))!;

        Assert.NotNull(alpha["agents"]!["orders"]);
    }

    [Fact]
    public async Task Four_projects_adding_one_repository_at_once_all_land()
    {
        // **The contention this branch makes reachable.** Before it, one repository could be
        // enrolled in one project, so its launch configuration had one writer; now four projects
        // may write into one file. `Enrolment` records what that costs when it goes wrong — eight
        // racing writers left one entry in the file and told two of them it had worked — which is
        // the shape where the damage is not the loss but the report.
        //
        // All attended, so none of them races another for the unattended consent: what is being
        // probed is the file they share, not the rule about who may start it.
        var token = TestContext.Current.CancellationToken;

        var deployment = new[] { "alpha", "beta", "gamma", "delta" }
            .Select((name, at) => new ProjectSettings(
                ProjectName.Create(name), Path.Combine(_root, name + ".db"),
                $"http://127.0.0.1:{9100 + at}", CatalogueFor(name), null))
            .ToList();

        ProjectManifest.Write(ManifestPath, deployment);

        foreach (var project in deployment)
        {
            await File.WriteAllTextAsync(project.NodeConfig!, """{ "agents": {} }""", token);
        }

        await Task.WhenAll(deployment.Select(project => Task.Run(
            async () => await Enrolment.AddAsync(
                new EnrollmentEntry(Workspace, "orders", null, Unattended: false) { Enroll = true },
                project.Name.Value,
                project.NodeConfig!,
                Path.Combine(Workspace, Adding.Instructions),
                token,
                deployment,
                ManifestPath),
            token)));

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, token))!;

        foreach (var project in deployment)
        {
            Assert.NotNull(launch["mcpServers"]![$"parley-{project.Name.Value}"]);
        }
    }

    [Fact]
    public void Nothing_else_starting_it_is_not_a_shared_workspace()
    {
        // The non-vacuous half. If `StartedElsewhere` answered null for everything, every test
        // above would pass while describing an ordinary first enrolment.
        Assert.Null(Enrolment.StartedElsewhere(null, Workspace, null));
        Assert.Null(Enrolment.StartedElsewhere([], Workspace, null));
    }
}
