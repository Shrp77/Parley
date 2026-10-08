using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The roadmaps a person has confirmed: listed above the conversations, opened to their items, and started from them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner confirmed a roadmap and could not find it.</b> The proposal left the proposed pane and nothing else in
/// the project showed a roadmap. Each of these fails against that: a project with a confirmed roadmap has to show it.
/// </para>
/// <para>Over a real socket, because what is asserted is what the broker holds after the person acted.</para>
/// </remarks>
public sealed class PlansTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public PlansTests()
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

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    /// <summary>Proposes and confirms a roadmap, the way a person does.</summary>
    private async Task ConfirmedAsync(string title = "Billing export")
    {
        var repository = new RoadmapRepository(_connection);

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

        var proposal = await repository.GetProposalAsync(id!.Value, Ct);

        await repository.ConfirmAsync(proposal!.Id, proposal.Content, null, T0, cancellationToken: Ct);
    }

    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        return shell;
    }

    /// <summary>Reads the project again, the way the proposals key does before it goes to a pane.</summary>
    private static async Task ReselectAsync(Shell shell)
    {
        await shell.Global().Single(a => a.Label == Proposing.Title).Run();
        await shell.Settled;
    }

    [Fact]
    public async Task A_confirmed_roadmap_is_listed_above_the_conversations()
    {
        // The report. Must fail against confirming a roadmap and showing it nowhere in the project.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        Assert.DoesNotContain(shell.Plans, shell.Panes);

        await ConfirmedAsync();
        await ReselectAsync(shell);
        driving.App.LayoutAndDraw(true);

        Assert.Contains(shell.Plans, shell.Panes);
        Assert.Contains(PlansRegion.RegionTitle, driving.Text(), StringComparison.Ordinal);
        Assert.Contains("Billing export — 2 items, 0 started", driving.Text(), StringComparison.Ordinal);

        // Above the conversations, in the same column.
        Assert.True(shell.Plans.Frame.Y < shell.Conversations.Frame.Y, "the roadmaps are not above the conversations");
        Assert.Equal(shell.Plans.Frame.X, shell.Conversations.Frame.X);
    }

    [Fact]
    public async Task A_project_with_no_confirmed_roadmap_looks_as_it_did()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        Assert.Equal([shell.Agents, (View)shell.Conversations], shell.Panes);
    }

    [Fact]
    public async Task Enter_on_a_roadmap_opens_its_items_with_where_each_stands()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        // Chosen directly rather than by Enter: a key press draws on this thread while the queue opens the view, and the
        // toolkit's draw is not safe to run twice at once. The program opens it on the loop's thread (`Shell.OnTheLoop`).
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);

        Assert.Equal(2, view.RowTexts.Count);
        Assert.Contains("1. [not started] Add CSV export", view.RowTexts[0], StringComparison.Ordinal);
        Assert.Contains("Stands: not started", view.DetailText, StringComparison.Ordinal);
        Assert.Contains("Next: item 1.", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enter_on_an_item_that_has_not_started_opens_its_conversation_with_its_text_in_the_box()
    {
        // Starting an item is the person's request: the conversation does not exist yet, the item's text is in the box,
        // and its intended agent is in the room. Nothing is sent. Must fail against sending on Enter, or against opening
        // nowhere.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        // Chosen directly rather than by Enter: a key press draws on this thread while the queue opens the view, and the
        // toolkit's draw is not safe to run twice at once. The program opens it on the loop's thread (`Shell.OnTheLoop`).
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);

        view.OpenSelected();
        await shell.Settled;

        Assert.Equal(Viewing.Conversation, shell.Place.Viewing);
        Assert.Equal("rm1-1", shell.Place.Title);
        Assert.Equal("Add CSV export", shell.Line.Written);
        Assert.Empty((await new BrokerClient(_broker.Operators).ConversationsAsync(cancellationToken: Ct)).Items);
    }

    [Fact]
    public async Task Escape_leaves_the_roadmap_without_choosing()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        // Chosen directly rather than by Enter: a key press draws on this thread while the queue opens the view, and the
        // toolkit's draw is not safe to run twice at once. The program opens it on the loop's thread (`Shell.OnTheLoop`).
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        driving.Press(Key.Esc);
        await shell.Settled;

        Assert.IsNotType<PlanView>(driving.App.TopRunnableView);
        Assert.Equal(Viewing.Project, shell.Place.Viewing);
    }
}
