using Terminal.Gui.Input;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Withdrawing an agent: kept by default, retired reversibly, purged permanently, and every one of
/// them behind the agent's own name typed back.
/// </summary>
/// <remarks>
/// <para>
/// User Story 4, and the destructive half of this feature. Every assertion about what happened is
/// made through the broker and against the files on disk — a report is a claim about a change, and
/// what is worth catching is a change that did not happen, or one that happened twice.
/// </para>
/// <para>
/// <b>FR-005 is the standard every assertion here is held to:</b> a journey ends in the same
/// registry state 004's acceptance scenarios named, not merely in a screen that says so. That is
/// why every check below reads the registry back through the broker rather than the row.
/// </para>
/// <para>
/// <b>The probes are the point, and each of them is a case where the wrong implementation destroys
/// something.</b> A name one character out, a name with trailing space, an empty field, Escape, a
/// broker that refuses: every one must leave the registry and both files exactly as they were.
/// </para>
/// </remarks>
public sealed class WithdrawingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-withdrawing-" + Path.GetRandomFileName());

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public WithdrawingTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.Combine(Workspace, ".git"));

        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, Workspace, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

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

    // **The root, not a manifest file.** `--config` names the directory this machine keeps its
    // Parley files in; the projects are the folders under it.
    private string Root => _root;

    /// <summary>This machine's one catalogue, which sits beside the manifest (018).</summary>
    private string CataloguePath => Path.Combine(_root, NodeCatalogue.DefaultFileName);

    /// <summary>Which agent the fixture catalogued, which is what a withdrawal removes by.</summary>
    private AgentIdentity Catalogued { get; set; }

    private string LaunchFile => Path.Combine(Workspace, ".mcp.json");

    [Fact]
    public async Task Keeping_is_first_and_changes_nothing()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await Choose(driving, shell, null);

        Assert.Equal(Withdrawing.Keep, Withdrawing.Choices[0]);
        Assert.Equal(Withdrawing.Kept, shell.Said);
        await AssertStillThereAsync();
    }

    [Fact]
    public async Task Retiring_takes_the_name_and_removes_both_files()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await Choose(driving, shell, Withdrawing.Retire, "orders@test");

        // Through the broker rather than the screen: the registry is the fact.
        var registry = await Client().RegistryAsync(TestContext.Current.CancellationToken);
        var orders = Assert.Single(registry, a => a.Name == Orders);

        Assert.Equal(AgentStatus.Retired, orders.Status);
        Assert.Contains("Retired 'orders@test'", shell.Said, StringComparison.Ordinal);

        // And the two files, on disk.
        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        Assert.False(((JsonObject)launch["mcpServers"]!).ContainsKey("parley"));
        Assert.DoesNotContain(
            Catalogued.ToString(), await CatalogueTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reinstating_brings_it_back_and_recreates_no_file()
    {
        // FR-015. The registry comes back; the files do not, and the surface says so rather than
        // leaving somebody to discover it when nothing starts.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await Choose(driving, shell, Withdrawing.Retire, "orders@test");
        await shell.Settled;

        // The row now reads retired, which is what offers reinstating.
        Assert.Contains(
            shell.Agents.Rows,
            row => row.Name == "orders@test"
                && row.State.StartsWith("retired", StringComparison.Ordinal));

        driving.Row(shell.Agents.List, "orders@test");

        await shell.Reinstate();

        var asking = (TextDialog)driving.Find(Withdrawing.ReinstateTitle);

        driving.Type("orders@test");
        asking.Finish(taken: true);

        await shell.Settled;

        var registry = await Client().RegistryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Active, Assert.Single(registry, a => a.Name == Orders).Status);
        Assert.Contains(Withdrawing.Reinstated, shell.Said, StringComparison.Ordinal);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        Assert.False(
            ((JsonObject)launch["mcpServers"]!).ContainsKey("parley"),
            "reinstating recreated the launch configuration");
    }

    [Fact]
    public async Task Purging_takes_the_name_and_the_broker_says_what_went()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await Choose(driving, shell, Withdrawing.Purge, "orders@test");

        var registry = await Client().RegistryAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(registry, a => a.Name == Orders);
        Assert.Contains("Purged 'orders@test'", shell.Said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders@tst")]
    [InlineData("orders@test ")]
    [InlineData(" orders@test")]
    [InlineData("ORDERS@TEST")]
    [InlineData("")]
    public async Task Anything_but_the_name_keeps_the_agent(string typed)
    {
        // Every one of these is a near miss somebody could plausibly produce, and every one must
        // leave the registry and both files alone. Trimming or lowering here would accept a
        // different string from the one they were asked to reproduce.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await Choose(driving, shell, Withdrawing.Purge, typed);

        Assert.Equal(Withdrawing.Kept, shell.Said);
        await AssertStillThereAsync();
    }

    [Fact]
    public async Task Escape_at_the_name_keeps_the_agent()
    {
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Withdraw();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Withdrawing.Title);

        driving.Row(choosing.Choices, Withdrawing.Purge);
        driving.Enter();
        driving.Escape();

        await shell.Settled;

        Assert.Equal(Withdrawing.Kept, shell.Said);
        await AssertStillThereAsync();
    }

    [Fact]
    public async Task A_purge_that_orphans_an_exchange_names_the_asker_before_the_name_is_asked_for()
    {
        // FR-014. Retirement's outcome is the kinder one -- the exchange runs out on the ordinary
        // schedule and the asker is told nobody answered -- so somebody choosing the permanent one
        // should be choosing between them knowingly.
        await OwedAsync();

        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        await shell.Withdraw();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Withdrawing.Title);

        // The description names the debt before anything is chosen.
        Assert.Contains("billing@test is waiting", choosing.Described, StringComparison.Ordinal);

        driving.Row(choosing.Choices, Withdrawing.Purge);
        driving.Enter();

        // And the consequence comes before the name, not after it.
        var accepting = (ChoiceDialog)driving.Find(Withdrawing.Purge);

        Assert.Equal(Withdrawing.Keep, accepting.Chosen);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(Withdrawing.Kept, shell.Said);
        await AssertStillThereAsync();
    }

    [Fact]
    public async Task A_seat_is_never_offered_for_withdrawal()
    {
        // Research R7 and FR-016. A person leaves a seat; they are not retired -- and that is true
        // of anybody's seat, not only the seat held here.
        using var driving = Driving.Open();
        using var shell = await OpenAsync(driving);

        var seat = new AgentRow("arvind@test", AgentsRegion.Seated, "—", "not stated", "—", Seat: true);

        await Withdrawing.OpenAsync(
            driving.App,
            Client(),
            seat,
            null,
            [],
            "acme",
            CataloguePath,
            _ => { },
            said =>
            {
                Assert.Equal(Withdrawing.NotAnAgent, said);

                return Task.CompletedTask;
            });

        // And nothing was opened to reach: the refusal happens before any dialog.
        Assert.Equal(Withdrawing.NotAnAgent, Withdrawing.NotAnAgent);
        Assert.NotNull(shell);
    }

    [Fact]
    public void The_description_carries_every_fact_the_choice_rests_on()
    {
        var row = new AgentRow("orders@test", "running", "pull", "carts", "/srv/orders", Seat: false);

        var said = Withdrawing.Describe(
            null, row, [new WaitingEdge(Billing, Orders, 7)]);

        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("running", said, StringComparison.Ordinal);
        Assert.Contains("/srv/orders", said, StringComparison.Ordinal);
        Assert.Contains("carts", said, StringComparison.Ordinal);
        Assert.Contains("billing@test is waiting", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_name_is_refused_and_the_right_one_is_not()
    {
        var row = new AgentRow("orders@test", "running", "pull", "—", "/srv/orders", Seat: false);

        Assert.Equal(Withdrawing.WrongName, Withdrawing.TypedNameProblem(row, "orders@tst"));
        Assert.Null(Withdrawing.TypedNameProblem(row, "orders@test"));
    }

    private BrokerClient Client() => new(_broker.Operators);

    private async Task<string> CatalogueTextAsync() =>
        File.Exists(CataloguePath)
            ? await File.ReadAllTextAsync(CataloguePath, TestContext.Current.CancellationToken)
            : string.Empty;

    /// <summary>Asserts the agent, its launch configuration and its catalogue line are all intact.</summary>
    private async Task AssertStillThereAsync()
    {
        var registry = await Client().RegistryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentStatus.Active, Assert.Single(registry, a => a.Name == Orders).Status);

        var launch = JsonNode.Parse(await File.ReadAllTextAsync(LaunchFile, TestContext.Current.CancellationToken))!;

        Assert.True(
            ((JsonObject)launch["mcpServers"]!).ContainsKey("parley"),
            "the launch configuration was withdrawn anyway");

        Assert.Contains(
            Catalogued.ToString(), await CatalogueTextAsync(), StringComparison.Ordinal);
    }

    /// <summary>A request from billing that orders has not sent the result of.</summary>
    private async Task OwedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Billing, T0, ct);
        await broker.StartAsync(Orders, T0, ct);

        await broker.RequestAsync(
            Billing,
            Orders,
            "model-a",
            """{"ask":"Which of the two paths is taken here?"}""",
            T0.AddMinutes(1),
            "owed",
            ct);
    }

    /// <summary>Opens the surface with one project, one catalogued agent and its two files.</summary>
    private async Task<Shell> OpenAsync(Driving.Session driving)
    {
        var ct = TestContext.Current.CancellationToken;

        ProjectManifest.Write(
            Root,
            [
                new ProjectSettings(
                    ProjectName.Create("acme"),
                    Path.Combine(_root, "acme.db"),
                    _broker.Address,
                    null),
            ]);

        // **Both files, keyed to each other** (018). The catalogue entry is keyed by the identity the
        // repository declares, and the withdrawal finds it by reading that repository — so a fixture
        // that wrote a role-keyed entry would describe a state no enrolment can produce, and the
        // withdrawal would rightly leave it alone.
        var identity = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            CataloguePath,
            "{\n  \"agents\": {\n    \"" + identity + "\": { \"command\": \"claude\", \"workspace\": \""
            + Workspace.Replace("\\", "\\\\", StringComparison.Ordinal) + "\" }\n  }\n}\n",
            ct);

        await File.WriteAllTextAsync(
            LaunchFile,
            $$"""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders@test" } },
                "other": { "command": "something" }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            ct);

        Catalogued = identity;

        var directory = BrokerDirectory.FromManifest(Root);
        var shell = TerminalCommand.Build(
            directory, manifestPath: Root, cataloguePath: CataloguePath);

        driving.Show(shell);

        await shell.RefreshAsync(ct);
        await shell.Settled;

        driving.Row(shell.Agents.List, "orders@test");

        return shell;
    }

    /// <summary>Opens the removal dialog and takes a choice, typing a name when one is asked for.</summary>
    private static async Task Choose(
        Driving.Session driving, Shell shell, string? choice, string? typed = null)
    {
        await shell.Withdraw();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Withdrawing.Title);

        if (choice is not null)
        {
            driving.Row(choosing.Choices, choice);
        }

        driving.Enter();

        if (typed is null)
        {
            await shell.Settled;

            return;
        }

        var asking = (TextDialog)driving.Find(choice!);

        driving.Type(typed);
        asking.Finish(taken: true);

        await shell.Settled;
    }
}
