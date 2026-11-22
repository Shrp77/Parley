using System.Text.Json;
using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That a checkout enrolled in two projects reads as startable on the terminal's agents list, and its row menu offers Start.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review, lens 1 run C on fc26cb8a.</b> A repository enrolled in two projects has two launch entries in its
/// <c>.mcp.json</c>, <c>parley-alpha</c> and <c>parley-beta</c>, each naming the same role. The terminal drew its agents
/// list without saying which project it was drawing, so the catalogue counted that role once per project, called it
/// contested, and left it out of what this machine can start: the row read "only runs when somebody opens it" and its
/// menu had no Start, while the web console, the command line and the broker all said it could be started.
/// </para>
/// <para>
/// <b>Read from the drawn list and the menu Enter opens</b>, never from the region's fields: the defect was in what was
/// passed to the draw, and a seam called directly would have been given the project the shell forgot.
/// </para>
/// </remarks>
[Integration]
public sealed class AnAgentInTwoProjectsCanBeStartedTests : IDisposable
{
    private const string Role = "orders";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("parley-two-projects-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The agent, on the machine this catalogue is, which is the one machine it can be started on.</summary>
    private static AgentName Orders { get; } = AgentName.Create(Role + "@" + NodeCatalogue.Machine);

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_checkout_enrolled_in_two_projects_reads_startable_and_offers_Start(bool afterTheListIsReadAgain)
    {
        var catalogue = Catalogue(Workspace());

        // Non-vacuity: the catalogue really does name the role twice when no project is said, which is what the shell
        // asked before, and once when this project is.
        var read = NodeCatalogue.ReadFrom(catalogue);
        Assert.Contains(Role, read.Announceable(project: null).Contested);
        Assert.Contains(Role, read.Announceable(Walking.Alpha.Value).Names);

        using var connection = ParleyDatabase.OpenInMemory();

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            Ct);

        await using var broker = LoopbackOperator.Start(connection);

        // This machine's node, saying it can start the role here, as the broker hears it from a node that has read the same
        // catalogue for this project. Without a node the broker calls the machine unreachable and the row reads that instead.
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var nodes = new Parley.Transport.Nodes.NodesClient(channel);

        using var joined = nodes.Join(
            new Parley.Transport.JoinRequest
            {
                Machine = Orders.Machine,
                StartableRoles = { Role },
                ProtocolVersion = ProtocolVersion.Current,
                ReportsTurns = true,
            },
            cancellationToken: Ct);

        Assert.True(await joined.ResponseStream.MoveNext(Ct), "the node was never told it joined");

        ProjectSettings[] projects = [new ProjectSettings(Walking.Alpha, "unused.db", broker.Address, null)];

        using var directory = new BrokerDirectory(projects, address => GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var session = Driving.Open(140, 40);
        using var shell = new Shell(directory)
        {
            Seats = seats, FollowEvery = null, Addressing = new AddressBook(null), CataloguePath = catalogue,
            MailEvery = afterTheListIsReadAgain ? TimeSpan.FromMilliseconds(100) : null,
        };

        session.Show(shell);
        shell.FollowEvery = null;

        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);

        if (afterTheListIsReadAgain)
        {
            // The list is read again on the mail watch's interval and drawn when what it says has changed: that draw named no
            // project either (review, lens 3 sweep A on 44c8e1e6). What it last said is set to something it never says, as an
            // agent starting or ending a turn changes it, so the next read draws over the list the project's opening drew. The
            // field is touched only to cause that draw and know it happened; the row is still read from the screen.
            const string Stale = "a list from before something changed";
            var said = typeof(Shell).GetField("_agentsSaid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            said.SetValue(shell, Stale);

            while (said.GetValue(shell) is Stale)
            {
                Assert.True(DateTime.UtcNow < deadline, "the agents list was never read again on the mail watch");
                await Task.Delay(50, Ct);
            }

            await shell.Settled;
        }

        shell.Focus(shell.Panes.ToList().FindIndex(pane => pane is AgentsRegion));
        Assert.IsType<AgentsRegion>(shell.Panes[shell.FocusedRegion]);

        session.App.LayoutAndDraw(true);

        var rows = Drawn.Rows(shell.Agents.List);
        var at = rows.FindIndex(row => string.Equals(row.Trim(), Orders.Value, StringComparison.Ordinal));

        Assert.True(at > 0, $"no row for {Orders.Value} was drawn:\n{string.Join('\n', rows)}");

        // The state is the heading above an agent's row.
        var heading = rows.Take(at).Last(row => !row.StartsWith("  ", StringComparison.Ordinal)).Trim();

        Assert.NotEqual(Standing.OnlyWhenOpened, heading);
        Assert.Contains(heading, new[] { Standing.Idle, Standing.NeverRun });

        session.Row(shell.Agents.List, Orders.Value);
        session.Enter();

        var menu = (ChoiceDialog)session.Find(Orders.Value);
        var offered = menu.Choices.Source!.ToList().Cast<object?>().Select(choice => choice?.ToString() ?? string.Empty).ToList();

        Assert.Contains("Start " + Orders.Value, offered);
    }

    /// <summary>A checkout enrolled in alpha and beta under the same role, as two enrolments leave its launch file.</summary>
    private string Workspace()
    {
        var workspace = _root.CreateSubdirectory("orders").FullName;

        var launch = new Dictionary<string, object>
        {
            [LaunchConfiguration.IdentityMember] = Identity.ToString(),
            ["mcpServers"] = new Dictionary<string, object>
            {
                [LaunchKey.For(Walking.Alpha.Value)] = new { command = "parley-mcp", env = new { PARLEY_AGENT = Role } },
                [LaunchKey.For("beta")] = new { command = "parley-mcp", env = new { PARLEY_AGENT = Role } },
            },
        };

        File.WriteAllText(Path.Combine(workspace, LaunchConfiguration.FileName), JsonSerializer.Serialize(launch));

        return workspace;
    }

    /// <summary>This machine's catalogue, listing the one checkout.</summary>
    private string Catalogue(string workspace)
    {
        var path = Path.Combine(_root.FullName, NodeCatalogue.DefaultFileName);

        File.WriteAllText(
            path,
            "{ \"agents\": { \"" + Identity + "\": { \"command\": \"claude\", \"workspace\": "
            + JsonSerializer.Serialize(workspace) + " } } }");

        return path;
    }

    private AgentIdentity Identity { get; } = AgentIdentity.Mint();
}
