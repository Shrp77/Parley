using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Roadmaps;
using Parley.Client.Seating;
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
        var room = string.Join(' ', shell.Room.Written);
        Assert.Contains("billing", room, StringComparison.Ordinal);
        Assert.Contains("orders", room, StringComparison.Ordinal);
        Assert.Empty((await new BrokerClient(_broker.Operators).ConversationsAsync(cancellationToken: Ct)).Items);
    }

    /// <summary>Opens the confirmed roadmap's view and asks to edit it, leaving the form open.</summary>
    private async Task<(FormDialog Form, PlanView View)> EditingAsync(Driving.Session driving, Shell shell)
    {
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);

        // Asked directly rather than by Alt+e: the key draws on this thread while the queue opens the form.
        view.Edit();
        await shell.Settled;

        return ((FormDialog)driving.Find(Revising.Title), view);
    }

    [Fact]
    public async Task Editing_a_confirmed_roadmap_is_offered_on_the_roadmap_and_in_its_view()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));

        var edit = Assert.Single(shell.Actions(), a => a.Label == Revising.Title);

        Assert.Equal(PlanView.EditKey, edit.Key);
        Assert.Equal("Alt+e", edit.Stroke);

        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.IsType<PlanView>(driving.App.TopRunnableView);
        Assert.Contains(PlanView.EditLabel, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_edit_form_opens_holding_the_roadmap_as_text()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        var (form, _) = await EditingAsync(driving, shell);

        Assert.Contains("title: Billing export", form.Values[0], StringComparison.Ordinal);
        Assert.Contains("1. Add CSV export | for: billing@test | item: 1", form.Values[0], StringComparison.Ordinal);
        Assert.Contains("2. Call it from the report | for: orders@test | needs: 1 | item: 2", form.Values[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_an_edit_removes_changes_and_adds_items_and_the_pane_shows_it()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        var (form, _) = await EditingAsync(driving, shell);

        // Item 2 is taken out, item 1 is reworded and a new one is written.
        form.Fill(0, "title: Billing exports\n1. Add CSV export as a file | for: billing@test | item: 1\n2. Write it up | for: orders@test");
        form.Finish(taken: true);
        await shell.Settled;

        var client = new BrokerClient(_broker.Operators);
        var roadmap = Assert.Single(await client.RoadmapsAsync(Ct));
        var whole = await client.RoadmapAsync(roadmap.Id, Ct);

        Assert.Equal("Billing exports", whole!.Title);
        Assert.Equal(["Add CSV export as a file", "Write it up"], whole.Items.Where(i => !i.Removed).OrderBy(i => i.Position).Select(i => i.Text));
        Assert.Contains(whole.Items, i => i.Removed && i.Text == "Call it from the report");
        Assert.Contains("Saved 'Billing exports'", shell.Said, StringComparison.Ordinal);
        Assert.Contains("Billing exports", Assert.Single(shell.Plans.Roadmaps).Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_made_on_a_stale_revision_is_said_and_applies_nothing()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        var (form, _) = await EditingAsync(driving, shell);

        // Somebody else saves first, so the revision this form was opened on is no longer the roadmap's.
        var client = new BrokerClient(_broker.Operators);
        var roadmap = Assert.Single(await client.RoadmapsAsync(Ct));
        var current = (await client.RoadmapAsync(roadmap.Id, Ct))!;
        var other = RoadmapDraft.From(current);

        other.Title = "Changed elsewhere";

        Assert.True((await client.EditRoadmapAsync(roadmap.Id, current.Revision, other.ToContent(), Ct)).Done);

        form.Fill(0, "title: Mine\n1. Something else | item: 1");
        form.Finish(taken: true);
        await shell.Settled;

        var after = (await client.RoadmapAsync(roadmap.Id, Ct))!;

        Assert.Equal(Revising.Stale, shell.Said);
        Assert.Equal("Changed elsewhere", after.Title);
        Assert.Equal(2, after.Items.Count(i => !i.Removed));
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

    [Fact]
    public async Task An_item_conversation_that_has_been_written_in_is_listed_with_the_other_conversations()
    {
        // The report: conversations begun from a roadmap were not in the conversations list.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await ConfirmedAsync();
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        Assert.IsType<PlanView>(driving.App.TopRunnableView).OpenSelected();
        await shell.Settled;

        shell.Line.Accept();
        await shell.Settled;

        var page = await new BrokerClient(_broker.Operators).ConversationsAsync(cancellationToken: Ct);

        Assert.True(page.Items.Any(c => c.Id == "rm1-1"), $"said: {shell.Said}");

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.Contains(shell.Conversations.Conversations, c => c.Id == "rm1-1");
    }

    [Fact]
    public async Task An_agent_added_after_the_roadmap_is_offered_in_an_item_conversation_that_has_not_started()
    {
        // The owner's report of 2026-10-09: an agent added to the project was not listed under "not in it yet" in the
        // first roadmap conversation. Must fail against a room that offers nobody until the conversation has a message.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await ConfirmedAsync();

        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(AgentName.Create("inventory@test"), "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            Ct);

        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        Assert.IsType<PlanView>(driving.App.TopRunnableView).OpenSelected();
        await shell.Settled;

        var room = string.Join('|', shell.Room.Written);

        Assert.Contains(RoomRegion.Elsewhere, room, StringComparison.Ordinal);
        Assert.Contains("inventory", room, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_added_after_an_item_conversation_began_is_offered_when_it_is_opened_again()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await ConfirmedAsync();
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;
        Assert.IsType<PlanView>(driving.App.TopRunnableView).OpenSelected();
        await shell.Settled;

        shell.Line.Accept();
        await shell.Settled;

        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(AgentName.Create("inventory@test"), "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            Ct);

        await Walking.GoAsync(shell, Viewing.Project);
        await Walking.GoAsync(shell, Viewing.Conversation, "rm1-1");
        await shell.Settled;

        var room = string.Join('|', shell.Room.Written);

        Assert.Contains(RoomRegion.Elsewhere, room, StringComparison.Ordinal);
        Assert.Contains("inventory", room, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_registers_is_not_also_listed_as_never_run_in_the_room()
    {
        // The owner's report of 2026-10-09: started from the room, the agent was listed twice, once registered and once as
        // never run. Must fail against a room that trusts the agents pane's older read of what is catalogued only.
        using var machine = Catalogued.Offering("zeta");
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        // The pane has read the project when zeta was catalogued and had not registered.
        shell.Agents.Show([], machine.Catalogue, null, Catalogued.Project);

        await Walking.GoAsync(shell, Viewing.Conversation, "talk");
        Assert.Contains("zeta (never run)", string.Join('|', shell.Room.Written), StringComparison.Ordinal);

        await new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(
                AgentName.Create($"zeta@{Parley.Configuration.NodeCatalogue.Machine}"),
                "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, "talk");
        await shell.Settled;

        var room = shell.Room.Written;

        Assert.DoesNotContain(room, row => row.Contains("never run", StringComparison.Ordinal));
        Assert.Single(room, row => row.Contains("zeta", StringComparison.Ordinal));
    }
}
