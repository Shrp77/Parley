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

    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory, bool seated = false)
    {
        // Seated only where a test reads who a line is addressed to: the room ticks nobody for a person not connected.
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = seated ? new SeatBook(directory.AgentsFor) : null };

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
        // and its intended agent is ticked. Nothing is sent. Must fail against sending on Enter, or against opening
        // nowhere.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory, seated: true);

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
        // Ticked is the item's own agent and no other the roadmap names: item 1 is billing's, and orders is in the room
        // (every project agent is) but not addressed (SPEC.md §5.7). Must fail against ticking the whole roadmap's agents.
        Assert.Contains(
            shell.Room.Written,
            row => row.StartsWith(RoomRegion.Addressed, StringComparison.Ordinal) && row.Contains("billing", StringComparison.Ordinal));
        Assert.DoesNotContain(
            shell.Room.Written,
            row => row.StartsWith(RoomRegion.Addressed, StringComparison.Ordinal) && row.Contains("orders", StringComparison.Ordinal));
        Assert.Contains(shell.Room.Written, row => row.Contains("orders", StringComparison.Ordinal));
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
    public async Task Removing_an_item_whose_work_is_open_is_described_and_saved_only_when_chosen()
    {
        // T053: the request stays open and its agent is not told, which deleting a line does not say. Must fail against
        // saving straight from the form, and against a warning that cannot be gone past.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await ReselectAsync(shell);

        var (form, _) = await EditingAsync(driving, shell);

        form.Fill(0, "title: Billing export\n1. Add CSV export | for: billing@test | item: 1");
        form.Finish(taken: true);
        await shell.Settled;

        var asking = (ChoiceDialog)driving.Find(Revising.Title);
        var client = new BrokerClient(_broker.Operators);
        var id = Assert.Single(await client.RoadmapsAsync(Ct)).Id;

        Assert.Contains("item 2, \"Call it from the report\", in rm1-2", asking.Described, StringComparison.Ordinal);
        Assert.Equal(2, (await client.RoadmapAsync(id, Ct))!.Items.Count(i => !i.Removed));

        driving.Row(asking.Choices, Revising.RemoveAnyway);
        driving.Enter();
        await shell.Settled;

        Assert.Equal(1, (await client.RoadmapAsync(id, Ct))!.Items.Count(i => !i.Removed));
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

    /// <summary>A seated shell with the roadmap confirmed and the first item's conversation begun by the person.</summary>
    private async Task<Shell> StartedAsync(Driving.Session driving, BrokerDirectory directory, int row = 0)
    {
        var shell = await OpenAsync(driving, directory, seated: true);

        await ConfirmedAsync();
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);

        view.Items.SelectedItem = row;
        view.OpenSelected();
        await shell.Settled;

        shell.Line.Accept();
        await shell.Settled;

        return shell;
    }

    [Fact]
    public async Task An_item_conversation_is_marked_as_the_items_in_the_list_and_in_its_title()
    {
        // FR-026: rm1-1 alone tells a reader nothing. Must fail against a row or a title that shows the name bare.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory);

        Assert.Contains("item 1 of #1 Billing export", shell.Transcript.Title, StringComparison.Ordinal);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        driving.App.LayoutAndDraw(true);

        Assert.Contains("rm1-1  [item 1 of #1 Billing export]", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task From_an_items_conversation_one_key_opens_its_roadmap_on_that_item()
    {
        // FR-025 and SC-003: to the item and back. Must fail against no way back, or a way back that lands on the next
        // item rather than the one the person came from: item 2 is not next while item 1 is in progress.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        Assert.Equal("rm1-2", shell.Place.Title);
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        var back = Assert.Single(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle);
        Assert.Equal(Shell.ItemRoadmapKey, back.Key);

        // Run directly rather than by Alt+o: the key draws on this thread while the queue opens the view.
        await back.Run();
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        Assert.Equal(2, view.Selected!.Item.Number);
    }

    [Fact]
    public async Task A_removed_items_conversation_says_formerly_and_offers_no_way_to_a_roadmap()
    {
        // FR-023a: the conversation stays, belongs to no item, and says which it was.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        var repository = new RoadmapRepository(_connection);
        var id = (await repository.ListAsync(Ct)).Single().Id;
        var edit = await repository.EditAsync(id, 1, new RoadmapContent("Billing export", [new RoadmapItemDraft(1, "Add CSV export", Billing, [])]), T0, cancellationToken: Ct);
        Assert.Null(edit.Problem);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        driving.App.LayoutAndDraw(true);
        Assert.Contains("rm1-2  [formerly item 2 of #1 Billing export]", driving.Text(), StringComparison.Ordinal);

        await Walking.GoAsync(shell, Viewing.Conversation, "rm1-2");
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        Assert.Contains("formerly item 2 of #1 Billing export", shell.Transcript.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle);
    }

    [Fact]
    public async Task A_roadmap_opens_on_its_next_item_and_a_key_returns_there()
    {
        // T039: on a long roadmap the next item can be far down. Item 1 is in progress and item 2 no longer needs it, so
        // item 2 is next and is not the first row. Must fail against opening on the first row always, and against a key
        // that does nothing.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory);

        var repository = new RoadmapRepository(_connection);
        var id = (await repository.ListAsync(Ct)).Single().Id;
        var edit = await repository.EditAsync(
            id,
            1,
            new RoadmapContent("Billing export",
            [
                new RoadmapItemDraft(1, "Add CSV export", Billing, []),
                new RoadmapItemDraft(2, "Call it from the report", Orders, []),
            ]),
            T0,
            cancellationToken: Ct);
        Assert.Null(edit.Problem);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        Assert.Equal(2, view.Selected!.Item.Number);

        view.Items.SelectedItem = 0;
        Assert.Equal(1, view.Selected!.Item.Number);

        // Pressed rather than called: the key is what a person has, and moving the cursor starts no queued work.
        driving.Press(new Key(PlanView.NextKey).WithAlt);
        Assert.Equal(2, view.Selected!.Item.Number);
        Assert.Contains("2. Call it from the report", view.DetailText, StringComparison.Ordinal);
    }

    /// <summary>Opens the roadmap from the project view and starts its item at <paramref name="row"/>.</summary>
    private static async Task OpenRowAsync(Driving.Session driving, Shell shell, int row)
    {
        await Walking.GoAsync(shell, Viewing.Project);
        await ReselectAsync(shell);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Items.SelectedItem = row;
        view.OpenSelected();
        await shell.Settled;
    }

    [Fact]
    public async Task Leaving_and_discarding_an_items_words_leaves_nothing_in_the_box()
    {
        // "Leaving throws it away" must be true: the item's words were put in the box for the person, and must not follow
        // them into the next conversation they open. Must fail against a discard that only leaves.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        await OpenRowAsync(driving, shell, 0);
        Assert.Equal("Add CSV export", shell.Line.Written);

        _ = shell.BackAsync();
        await shell.Settled;
        var asking = (ChoiceDialog)driving.Find(Shell.DiscardTitle);
        driving.Row(asking.Choices, Shell.Discard);
        asking.Choices.InvokeCommand(global::Terminal.Gui.Input.Command.Accept);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Conversation, "rm1-2");
        await shell.Settled;

        Assert.Equal(string.Empty, shell.Line.Written);
    }

    [Fact]
    public async Task Going_from_one_items_words_to_another_item_asks_first_and_carries_nothing_there()
    {
        // From item 1's conversation, with its words unsent, back to the roadmap and on to item 2, which has begun. Must
        // fail against arriving in rm1-2 with item 1's words in the box, where the next Enter sends them to its agent.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        await OpenRowAsync(driving, shell, 0);
        Assert.Equal("Add CSV export", shell.Line.Written);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));
        await Assert.Single(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle).Run();
        await shell.Settled;
        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Items.SelectedItem = 1;
        view.OpenSelected();
        await shell.Settled;

        // Asked, as Escape asks; keeping stays where the words are.
        var asking = (ChoiceDialog)driving.Find(Shell.DiscardTitle);
        driving.Row(asking.Choices, Shell.KeepWriting);
        asking.Choices.InvokeCommand(global::Terminal.Gui.Input.Command.Accept);
        await shell.Settled;
        Assert.Equal("rm1-1", shell.Place.Title);
        Assert.Equal("Add CSV export", shell.Line.Written);

        await Assert.Single(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle).Run();
        await shell.Settled;
        view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Items.SelectedItem = 1;
        view.OpenSelected();
        await shell.Settled;
        asking = (ChoiceDialog)driving.Find(Shell.DiscardTitle);
        driving.Row(asking.Choices, Shell.Discard);
        asking.Choices.InvokeCommand(global::Terminal.Gui.Input.Command.Accept);
        await shell.Settled;

        Assert.Equal("rm1-2", shell.Place.Title);
        Assert.Equal(string.Empty, shell.Line.Written);
    }

    [Fact]
    public async Task Choosing_the_item_whose_conversation_is_open_keeps_the_words_the_person_changed()
    {
        // In item 1's conversation, unsent, the person trims its words, goes to the roadmap and chooses item 1 again.
        // Nothing is asked, since they stay where they are, so nothing may be replaced. Must fail against filling the box
        // with the item's words again.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        await OpenRowAsync(driving, shell, 0);
        shell.Line.Written = "Add CSV export, headers only";

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));
        await Assert.Single(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle).Run();
        await shell.Settled;
        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Items.SelectedItem = 0;
        view.OpenSelected();
        await shell.Settled;

        Assert.Equal("rm1-1", shell.Place.Title);
        Assert.Equal("Add CSV export, headers only", shell.Line.Written);
    }

    [Fact]
    public async Task A_cleared_transcript_offers_no_way_to_an_items_roadmap()
    {
        // Selecting clears the transcript while the person stays in the conversation view (after a Clarify, say). Must
        // fail against a clear that keeps the item, which left Alt+o offered with no conversation open.
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await StartedAsync(driving, directory, row: 1);

        shell.Transcript.Clear();
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        Assert.Null(shell.Transcript.Open);
        Assert.DoesNotContain(shell.Actions(), a => a.Label == Shell.ItemRoadmapTitle);
    }
}
