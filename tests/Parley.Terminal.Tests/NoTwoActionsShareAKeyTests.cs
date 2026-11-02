using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That no pane offers two actions on one key, counting the message line's and those that belong to no pane.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key handler runs one action per key, so the second one on a key is an action the surface names and never
/// runs.</b> The agents pane had two: <c>a</c> was both adding a repository and asking the selected agent what it owns;
/// and <c>w</c> was both what was told to the selected agent and taking delivery for a held seat. Later the roadmaps
/// pane's edit and the message line's whole message were both <c>e</c>, which this test could not see while it compared
/// a pane's own letters and walked no roadmap.
/// </para>
/// <para>
/// <b>Driven into the richest state each pane has</b> — a seat held, a peer selected, a roadmap confirmed, one proposed
/// and a message cut short — because the offers are conditional, and a clash that only appears once every condition
/// holds is exactly the one a fixture with none would miss. <c>CommandTableTests</c> holds the table itself; this holds
/// that what is live on the drawn surface agrees with it.
/// </para>
/// </remarks>
[Integration]
public sealed class NoTwoActionsShareAKeyTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private const int Columns = 100;
    private const int Tall = 30;

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public NoTwoActionsShareAKeyTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_pane_in_a_project_offers_two_actions_on_one_key()
    {
        await using var seated = await Seated.OpenAsync(Ct);
        var shell = seated.Shell;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        seated.Focus<AgentsRegion>();
        seated.Session.Row(shell.Agents.List, "orders@test");

        Assert.NotNull(shell.Agents.Selected);
        Assert.NotNull(shell.Held);

        EveryPaneIsDistinct(shell);
    }

    [Fact]
    public async Task No_pane_in_a_conversation_offers_two_actions_on_one_key()
    {
        await using var seated = await Seated.OpenAsync(Ct);

        EveryPaneIsDistinct(seated.Shell);
    }

    [Fact]
    public async Task Every_place_with_a_roadmap_a_proposal_and_a_message_cut_short_offers_one_action_per_key()
    {
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await OpenAsync(driving, directory, seats);

        var visited = new HashSet<KeyContext>();

        foreach (var viewing in new[] { Viewing.Projects, Viewing.Project, Viewing.Conversation, Viewing.Configuration })
        {
            await Walking.GoAsync(shell, viewing);

            shell.Report(Long());
            driving.App.LayoutAndDraw(true);

            Assert.True(shell.SaidMore, $"the message is not cut short on {viewing}");

            EveryPaneIsDistinct(shell);

            for (var pane = 0; pane < shell.Panes.Count; pane++)
            {
                shell.Focus(pane);
                visited.Add(shell.Context);
            }
        }

        // Non-vacuity: every place the table declares was stood in, the two roadmap panes and the settings included.
        Assert.Superset(
            shell.CommandTable.Places.Select(place => place.Context).Where(context => context != KeyContext.Elsewhere).ToHashSet(),
            visited);
    }

    [Fact]
    public async Task Alt_and_the_whole_message_key_on_the_roadmaps_pane_shows_the_whole_message()
    {
        // The pane where the message line's key used to lose: the roadmaps pane's edit had it first.
        using var driving = Driving.Open(Columns, Tall);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await OpenAsync(driving, directory, seats);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.Equal(KeyContext.Plans, shell.Context);
        Assert.Contains(shell.Actions(), action => action.Label == Revising.Title);

        shell.Report(Long());
        driving.App.LayoutAndDraw(true);

        // What the screen says the key is, read off the drawn screen rather than out of a field.
        var named = $"{Shell.Alt(Shell.WholeKey)}: {Shell.WholeTitle}";

        Assert.Contains(named, driving.Text(), StringComparison.Ordinal);

        driving.Press(new Key(Shell.WholeKey).WithAlt);
        driving.App.LayoutAndDraw(true);

        Assert.True(driving.Has(Saying.Heading), "the whole message is not on screen");
        Assert.Contains(Saying.Heading, driving.Text(), StringComparison.Ordinal);
        Assert.False(driving.Has(Revising.Title), "the key opened the roadmap's edit form");
    }

    private static string Long() =>
        "Told 0 of 40. Refused for 40: nothing has registered under that name yet, so nothing can be addressed to it. "
        + string.Join(", ", Enumerable.Range(0, 40).Select(at => $"agent-{at}@machine"))
        + ".";

    private static void EveryPaneIsDistinct(Shell shell)
    {
        for (var pane = 0; pane < shell.Panes.Count; pane++)
        {
            shell.Focus(pane);

            var clashes = shell.Actions()
                .Concat(shell.Messaging())
                .Concat(shell.Global())
                .GroupBy(action => action.Chord?.ToString() ?? Shell.Alt(char.ToLowerInvariant(action.Key)), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key}: {string.Join(" / ", group.Select(action => action.Label))}")
                .ToList();

            Assert.True(
                clashes.Count == 0,
                $"{shell.Panes[pane].GetType().Name} offers two actions on one key: {string.Join("; ", clashes)}");
        }
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    /// <summary>A seated surface on the project, with one roadmap confirmed and another proposed.</summary>
    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);

        var repository = new RoadmapRepository(_connection);

        foreach (var (title, confirm) in new[] { ("Billing export", true), ("Inventory sync", false) })
        {
            var (id, problem) = await repository.ProposeAsync(
                Orders,
                null,
                null,
                new RoadmapContent(title,
                [
                    new RoadmapItemDraft(null, "Add CSV export", Billing, []),
                    new RoadmapItemDraft(null, "Call it from the report", Orders, [1]),
                ]),
                T0,
                cancellationToken: Ct);

            Assert.Null(problem);

            if (confirm)
            {
                var proposal = await repository.GetProposalAsync(id!.Value, Ct);

                await repository.ConfirmAsync(proposal!.Id, proposal.Content, null, T0, cancellationToken: Ct);
            }
        }

        // Read again, the way the proposals key does before it goes to a pane.
        await shell.Global().Single(action => action.Label == Proposing.Title).Run();
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Contains(shell.Plans, shell.Panes);
        Assert.Contains(shell.Roadmaps, shell.Panes);

        return shell;
    }
}
