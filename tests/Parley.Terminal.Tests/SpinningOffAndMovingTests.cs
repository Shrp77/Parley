using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using System.Text.RegularExpressions;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>The terminal's two doors onto SPEC.md §5.0, pressed through a real shell against a real broker.</summary>
[Integration]
public sealed class SpinningOffAndMovingTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    private IReadOnlyList<IDisposable> _held = [];

    public SpinningOffAndMovingTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        var agents = new AgentRepository(_connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var held in _held)
        {
            held.Dispose();
        }

        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BrokerClient Client => new(_broker.Operators);

    private async Task<(Shell Shell, Driving.Session Driving, long Message)> SeatedAsync()
    {
        var driving = Driving.Open(width: 140, height: 40);
        var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));
        var seats = new SeatBook(directory.AgentsFor);
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, Ct);

        var sent = await broker.RequestAsync(Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"the disk is full"}""", T0.AddMinutes(1), Conversation, Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        _held = [driving, directory, seats];

        return (shell, driving, sent.Request!.Id);
    }

    [Fact]
    public async Task A_spin_off_opens_with_the_message_quoted_creates_nothing_and_links_when_it_is_sent()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Equal("disk-cleanup", shell.Transcript.Open);
        Assert.Contains("> the disk is full", shell.Line.Written, StringComparison.Ordinal);
        Assert.Contains("orders@test said in room:", shell.Line.Written, StringComparison.Ordinal);

        // On the screen, not only in the box: these words go out as the person's.
        driving.App.LayoutAndDraw(true);
        Assert.Contains("orders@test said in room:", driving.Text(), StringComparison.Ordinal);
        Assert.Contains("> the disk is full", driving.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain((await Client.ConversationsAsync(cancellationToken: Ct)).Items, c => c.Id == "disk-cleanup");

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        var export = await Client.ExportAsync("disk-cleanup", Ct);

        Assert.Equal("room", export.Links!.Parent);

        var sent = Assert.Single(export.Messages);

        Assert.Equal(Orders, sent.To);
        Assert.Contains("the disk is full", sent.PayloadJson, StringComparison.Ordinal);

        var listed = (await Client.ConversationsAsync(cancellationToken: Ct)).Items.Single(c => c.Id == "disk-cleanup");

        Assert.Equal("room", listed.Parent);
    }

    /// <summary>A conversation on screen with no seat in the project, the cursor on nothing.</summary>
    private async Task<(Shell Shell, Driving.Session Driving)> ReadingWithNoSeatAsync()
    {
        var driving = Driving.Open(width: 140, height: 40);
        var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));
        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, Ct);
        Assert.True((await broker.RequestAsync(Orders, Billing, "model-a", """{"ask":"the disk is full"}""", T0.AddMinutes(1), Conversation, Ct)).Succeeded);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        _held = [driving, directory];

        return (shell, driving);
    }

    [Fact]
    public async Task Alt_b_on_a_drawn_conversation_asks_for_a_name_and_nothing_else_opens()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        await driving.PressThenSettleAsync(Key.B.WithAlt, () => shell.Settled);

        _ = (FormDialog)driving.Find(SpinningOff.Title);
        Assert.False(driving.Has(Moving.Title));
        Assert.False(driving.Has(Shell.RelatedTitle));
    }

    [Fact]
    public async Task Alt_v_on_a_drawn_conversation_opens_the_list_to_tick_and_nothing_else()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        await driving.PressThenSettleAsync(Key.V.WithAlt, () => shell.Settled);

        _ = (TickDialog)driving.Find(Moving.Title);
        Assert.False(driving.Has(SpinningOff.Title));
        Assert.False(driving.Has(Shell.RelatedTitle));
        Assert.NotEqual(Shell.NoRelated, shell.Said);
    }

    [Fact]
    public async Task Alt_l_says_when_nothing_is_linked_and_otherwise_offers_what_is()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        await driving.PressThenSettleAsync(Key.L.WithAlt, () => shell.Settled);

        Assert.Equal(Shell.NoRelated, shell.Said);
        Assert.False(driving.Has(Moving.Title));
        Assert.False(driving.Has(Shell.RelatedTitle));

        // Something left it, so there is somewhere to go now.
        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"stays here"}""", T0.AddMinutes(2), Conversation, Ct)).Succeeded);

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        await driving.PressThenSettleAsync(Key.L.WithAlt, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Shell.RelatedTitle);

        Assert.Contains("disk-cleanup", choosing.Choices.Source!.ToList().Cast<string>());
        Assert.False(driving.Has(Moving.Title));
    }

    [Fact]
    public async Task Alt_b_does_nothing_without_a_seat_though_alt_v_on_the_same_screen_does()
    {
        var (shell, driving) = await ReadingWithNoSeatAsync();
        using var held = shell;

        Assert.Null(shell.Held);
        Assert.Equal(Conversation, shell.Transcript.Open);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));

        await driving.PressThenSettleAsync(Key.B.WithAlt, () => shell.Settled);

        Assert.False(driving.Has(SpinningOff.Title));

        // The same key table on the same screen and focus: it was the seat that stopped it.
        await driving.PressThenSettleAsync(Key.V.WithAlt, () => shell.Settled);

        _ = (TickDialog)driving.Find(Moving.Title);
    }

    [Fact]
    public async Task The_persons_own_message_ticks_nobody_in_a_spin_off()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var own = await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"my own words"}""", T0.AddMinutes(2), Conversation, Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.True(shell.Transcript.Reach(own.Request!.Id));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Equal("disk-cleanup", shell.Transcript.Open);
        Assert.Contains("> my own words", shell.Line.Written, StringComparison.Ordinal);
        Assert.Empty(shell.Drafting!.Pairs);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Empty((await Client.ExportAsync("disk-cleanup", Ct)).Messages);
    }

    [Fact]
    public async Task The_review_says_what_stays_behind_and_when_they_cannot_come_back_and_a_follow_up_is_left_unless_ticked()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var followUp = await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"a follow-up"}""", T0.AddMinutes(2), Conversation, Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        Assert.Contains($"you can move them back unless {Conversation} has closed", reviewing.Described, StringComparison.Ordinal);
        Assert.Contains("Anything asked as a result stays behind unless you ticked it", reviewing.Described, StringComparison.Ordinal);
        Assert.Contains("agents aren't woken", reviewing.Described, StringComparison.Ordinal);

        var row = Assert.Single(reviewing.Rows.Source!.ToList().Cast<string>());

        Assert.Contains("the disk is full", row, StringComparison.Ordinal);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        driving.Row(choosing.Choices, Moving.New);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        foreach (var key in "disk-cleanup")
        {
            driving.Find(Moving.NameTitle).NewKeyDownEvent(new Key(key));
        }

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(message, Assert.Single((await Client.ExportAsync("disk-cleanup", Ct)).Messages).Id);
        Assert.Equal(followUp.Request!.Id, Assert.Single((await Client.ExportAsync(Conversation, Ct)).Messages).Id);
    }

    [Fact]
    public async Task A_spin_off_cancelled_at_the_name_does_nothing()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Finish(taken: false);
        await shell.Settled;

        Assert.Equal(Conversation, shell.Transcript.Open);
        Assert.Single((await Client.ConversationsAsync(cancellationToken: Ct)).Items);
    }

    [Fact]
    public async Task The_list_of_where_entries_can_go_says_what_each_was_spun_off_from()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        Assert.Null(await Client.SpinOffAsync(Conversation, "kid", Ct));
        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"kid"}""", T0, "kid", Ct)).Succeeded);

        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;

        _ = (TickDialog)driving.Find(Moving.Title);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);
        _ = (TickDialog)driving.Find(Moving.ReviewTitle);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        Assert.Contains(
            choosing.Choices.Source!.ToList().Cast<string>(),
            row => row.Contains($"kid - spinoff from {Conversation}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_added_to_a_move_says_so_and_the_door_says_why()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"same words"}""", T0.AddMinutes(1), Conversation, Ct)).Succeeded);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"same words"}""", T0.AddMinutes(1), Conversation, Ct)).Succeeded);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        var said = (await Client.ExportAsync(Conversation, Ct)).Messages.First(m => m.PayloadJson.Contains("same words", StringComparison.Ordinal));

        Assert.True(shell.Transcript.Reach(said.Id));

        await shell.MoveEntries();
        await shell.Settled;

        _ = (TickDialog)driving.Find(Moving.Title);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        Assert.Contains(Parley.Client.Rendering.MovePicking.SentTogether, reviewing.Described, StringComparison.Ordinal);
        Assert.Contains(", added:", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_to_place_a_file_says_what_a_conversation_was_spun_off_from_with_nobody_in_it_too()
    {
        var nobody = new ConversationSummary("kid", [], 0, T0, T0, false, Parent: "room");

        Assert.Equal("  kid - spinoff from room", FilesPlacing.Row(nobody));
    }

    [Fact]
    public async Task A_move_lists_what_goes_starts_with_staying_and_moves_when_named()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;

        _ = (TickDialog)driving.Find(Moving.Title);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);
        var row = Assert.Single(reviewing.Rows.Source!.ToList().Cast<string>());

        Assert.Contains("the disk is full", row, StringComparison.Ordinal);
        Assert.Contains("still open", row, StringComparison.Ordinal);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        Assert.Equal(Moving.Stay, choosing.Choices.Source!.ToList().Cast<string>().First());
        Assert.Equal(Conversation, Assert.Single((await Client.ConversationsAsync(cancellationToken: Ct)).Items).Id);

        driving.Row(choosing.Choices, Moving.New);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var naming = (TextDialog)driving.Find(Moving.NameTitle);

        foreach (var key in "disk-cleanup")
        {
            naming.NewKeyDownEvent(new Key(key));
        }

        Assert.Equal("disk-cleanup", naming.Typed);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var moved = await Client.ExportAsync("disk-cleanup", Ct);

        Assert.Equal(message, Assert.Single(moved.Messages).Id);
        Assert.Equal("room", moved.Links!.Parent);
        Assert.Empty((await Client.ExportAsync(Conversation, Ct)).Messages);
        Assert.Equal("disk-cleanup", shell.Transcript.Open);
    }

    [Fact]
    public async Task Leaving_them_where_they_are_moves_nothing()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Single((await Client.ExportAsync(Conversation, Ct)).Messages);
        Assert.Empty((await Client.ExportAsync(Conversation, Ct)).Moves!);
    }

    private async Task<long> AskPersonAsync(Shell shell, string words)
    {
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, Ct);

        return (await broker.RequestAsync(Orders, shell.Held!.Seat.Name, "model-a", $$"""{"ask":"{{words}}"}""", T0.AddMinutes(1), Conversation, Ct)).Exchange!.Id;
    }

    [Fact]
    public async Task A_name_already_in_use_is_told_at_once_and_goes_nowhere()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"already here"}""", T0.AddMinutes(2), "scratch", Ct)).Succeeded);

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "scratch");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("already a conversation called scratch", shell.Said, StringComparison.Ordinal);
        Assert.Equal(Conversation, shell.Transcript.Open);
    }

    [Fact]
    public async Task Leaving_a_spin_off_unsent_leaves_nothing_armed_and_no_record()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.Empty((await Client.ExportAsync(Conversation, Ct)).Links!.Children);

        shell.Line.Written = "an ordinary line";
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.DoesNotContain("Nothing was sent", shell.Said, StringComparison.Ordinal);
        Assert.Empty((await Client.ExportAsync(Conversation, Ct)).Links!.Children);
    }

    [Fact]
    public async Task Leaving_a_spin_off_with_Escape_links_nothing_and_ticks_nobody_when_the_name_is_used_later()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        await driving.PressThenSettleAsync(Key.Esc, () => shell.Settled);

        var leaving = (ChoiceDialog)driving.Find(Shell.DiscardTitle);

        driving.Row(leaving.Choices, Shell.Discard);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(Viewing.Project, shell.Place.Viewing);

        // The person then starts something of their own, for billing, under the same name.
        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(Billing, T0, Ct);
        await seat.RequestAsync(Billing, shell.Held!.Seat.Name, "model-a", """{"ask":"a fresh one"}""", T0.AddMinutes(2), "disk-cleanup", Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, "disk-cleanup");
        await shell.Settled;

        shell.Line.Written = "my own line";
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.DoesNotContain("Nothing was sent", shell.Said, StringComparison.Ordinal);

        var there = await Client.ExportAsync("disk-cleanup", Ct);

        Assert.Null(there.Links!.Parent);
        Assert.DoesNotContain(there.Messages, m => m.To == Orders);
    }

    [Fact]
    public async Task Leaving_for_the_project_and_coming_back_leaves_no_words_that_could_go_with_no_link()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("the disk is full", shell.Line.Written, StringComparison.Ordinal);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.Empty(shell.Line.Written);
    }

    [Fact]
    public async Task A_name_whose_exchanges_all_moved_out_is_refused_before_anything_is_written()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await seat.StartAsync(Orders, T0, Ct);

        var other = await seat.RequestAsync(Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"elsewhere"}""", T0.AddMinutes(2), "emptied", Ct);

        Assert.True((await Client.MoveExchangesAsync("emptied", "kept", true, [other.Exchange!.Id], Ct)).Moved);

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "emptied");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("already a conversation called emptied", shell.Said, StringComparison.Ordinal);
        Assert.Equal(Conversation, shell.Transcript.Open);
        Assert.Empty(shell.Line.Written);
    }

    [Theory]
    [InlineData("owns-orders", "facilitator")]
    [InlineData("room", "its own parent")]
    public void A_name_the_broker_would_refuse_is_said_at_once(string name, string says)
    {
        var problem = SpinningOff.NameProblem(name, Conversation, new ConversationExport([], []));

        Assert.Contains(says, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_destroyed_name_says_it_was_destroyed_and_a_name_holding_work_says_it_is_taken()
    {
        var message = new Message(1, 1, MessageType.Request, Orders, Billing, "model-a", """{"ask":"x"}""", "free", T0, null);

        Assert.Contains(
            "was destroyed",
            SpinningOff.NameProblem("gone", Conversation, new ConversationExport([], [], new DestroyedConversation("gone", T0, 1, []))),
            StringComparison.Ordinal);

        // Either half of what a conversation holds says so alone: retention can leave an exchange with its messages pruned.
        Assert.Contains("already", SpinningOff.NameProblem("free", Conversation, new ConversationExport([message], [])), StringComparison.Ordinal);
        Assert.Contains(
            "already",
            SpinningOff.NameProblem("free", Conversation, new ConversationExport([], [new ExchangeSnapshot(1, ExchangeState.Closed, T0, T0)])),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_thing_the_broker_counts_as_taken_is_said_at_once()
    {
        ConversationExport Empty(RoadmapItemRef? item = null, ConversationLinks? links = null) => new([], [], null, item, links);

        Assert.Null(SpinningOff.NameProblem("free", Conversation, Empty()));
        Assert.Contains("roadmap", SpinningOff.NameProblem("rm1-1", Conversation, Empty(item: new RoadmapItemRef(1, 1, "t", 1, false))), StringComparison.Ordinal);
        Assert.Contains("already", SpinningOff.NameProblem("free", Conversation, Empty(links: new ConversationLinks("somewhere-else", ConversationLinkKind.SpinOff, []))), StringComparison.Ordinal);
        Assert.Contains("already", SpinningOff.NameProblem("free", Conversation, Empty(links: new ConversationLinks(null, null, ["a-child"]))), StringComparison.Ordinal);

        // Everything moved out to a conversation that already existed: no exchange, no child, only the record.
        Assert.Contains("already", SpinningOff.NameProblem("free", Conversation, new ConversationExport([], [], Moves: [new MoveRecord(1, "free", "kept", "human-user@test", T0, 1, 1, [1])])), StringComparison.Ordinal);

        // What a failed attempt to take a link back leaves: the broker lets the same parent have it.
        Assert.Null(SpinningOff.NameProblem("free", Conversation, Empty(links: new ConversationLinks(Conversation, ConversationLinkKind.SpinOff, []))));
    }

    [Fact]
    public async Task Enter_on_an_empty_box_leaves_no_link_and_the_words_can_still_be_sent()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        shell.Line.Written = string.Empty;
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Empty((await Client.ExportAsync(Conversation, Ct)).Links!.Children);

        shell.Line.Written = "now with words";
        await shell.Settled;
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Equal(["disk-cleanup"], (await Client.ExportAsync(Conversation, Ct)).Links!.Children);
    }

    [Fact]
    public async Task The_seat_can_still_send_a_result_on_what_moved()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var delivered = shell.Held!.Delivered.Count(one => one.Owes);

        Assert.True(delivered > 0, "the seat holds nothing owed");
        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        driving.Row(choosing.Choices, Moving.New);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        foreach (var key in "elsewhere")
        {
            driving.Find(Moving.NameTitle).NewKeyDownEvent(new Key(key));
        }

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal(delivered, shell.Held!.Delivered.Count(one => one.Owes));
    }

    [Fact]
    public async Task A_send_to_several_moves_together_though_one_was_under_the_cursor()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var voice = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await voice.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var one = await voice.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"same words"}""", T0.AddMinutes(2), Conversation, Ct);
        var two = await voice.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"same words"}""", T0.AddMinutes(2), Conversation, Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.True(shell.Transcript.Reach(one.Request!.Id));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);
        var rows = reviewing.Rows.Source!.ToList().Cast<string>().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Contains(two.Exchange!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal) && row.Contains(", added:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_move_list_scrolls_rather_than_cutting_anything_off_and_names_the_files()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var ids = new List<long>();

        System.Data.Common.DbConnection raw = _connection;

        using (var place = raw.CreateCommand())
        {
            place.CommandText =
                """
                INSERT INTO conversation_folders (conversation_id, folder, created_utc) VALUES ('room', 'room', '2026-01-01T00:00:00.000Z');
                INSERT INTO shared_files (conversation_id, name_key, name, size_bytes, sha256, placed_by, placed_utc, changed_utc, modified_ticks)
                VALUES ('room', 'NOTES.MD', 'notes.md', 10, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'orders@test', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 0);
                """;
            place.ExecuteNonQuery();
        }

        for (var i = 0; i < 12; i++)
        {
            ids.Add(await AskPersonAsync(shell, $"question {i}"));
        }

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await shell.MoveEntries();
        await shell.Settled;

        var ticking = (TickDialog)driving.Find(Moving.Title);

        ticking.NewKeyDownEvent(Key.A.WithAlt);

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        Assert.Contains("13 exchanges", reviewing.Described, StringComparison.Ordinal);
        Assert.Contains("These files stay in room: notes.md", reviewing.Described, StringComparison.Ordinal);
        Assert.Equal(13, reviewing.Items.Count);
        Assert.Equal(2, reviewing.Pages);
        Assert.Equal(TickDialog.PageSize, reviewing.Rows.Source!.Count);
    }

    [Fact]
    public async Task Whether_a_row_is_open_is_drawn_however_long_its_first_words_are()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var longest = string.Join(' ', Enumerable.Repeat("words", 40));

        _ = await AskPersonAsync(shell, longest);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await shell.MoveEntries();
        await shell.Settled;

        var ticking = (TickDialog)driving.Find(Moving.Title);

        Assert.Equal(2, ticking.Rows.Source!.Count);
        Assert.Equal(2, Regex.Count(driving.Text(), "still open"));

        ticking.NewKeyDownEvent(Key.A.WithAlt);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        _ = (TickDialog)driving.Find(Moving.ReviewTitle);

        Assert.Equal(2, Regex.Count(driving.Text(), "still open"));
    }

    [Fact]
    public async Task A_spin_off_with_the_cursor_on_no_message_opens_an_empty_box_for_nobody()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        shell.Transcript.List.SelectedItem = null;
        Assert.Null(shell.Transcript.Under);

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Equal("disk-cleanup", shell.Transcript.Open);
        Assert.Equal(string.Empty, shell.Line.Written.Trim());
    }

    [Fact]
    public async Task A_move_record_says_who_moved_it_and_when_across_the_wire()
    {
        var (shell, _, message) = await SeatedAsync();
        using var held = shell;

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        var move = Assert.Single((await Client.ExportAsync(Conversation, Ct)).Moves!);

        Assert.Equal(shell.Held!.Seat.Name.Value, move.MovedBy);
        Assert.InRange(move.MovedUtc, before, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task A_destroyed_conversation_still_says_what_it_is_linked_to()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);
        _ = await Client.DestroyAsync("disk-cleanup", Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, "disk-cleanup");
        await shell.Settled;

        Assert.Contains($"Made by moving entries out of {Conversation}", shell.Transcript.Written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alt_l_goes_to_a_linked_conversation_and_says_so_when_there_is_none()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        await shell.GoToRelated();
        await shell.Settled;

        Assert.Equal(Shell.NoRelated, shell.Said);

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        // Emptied, and it still says what it is related to.
        Assert.Contains("Started from this one: disk-cleanup", shell.Transcript.Written, StringComparison.Ordinal);

        await shell.GoToRelated();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Shell.RelatedTitle);

        driving.Row(choosing.Choices, "disk-cleanup");
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Equal("disk-cleanup", shell.Transcript.Open);
    }

    [Fact]
    public async Task The_conversations_list_says_what_a_conversation_was_spun_off_from()
    {
        var (shell, _, message) = await SeatedAsync();
        using var held = shell;

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        var rows = shell.Conversations.List.Source!.ToList().Cast<string>().ToList();

        Assert.Contains(rows, row => row.Contains("disk-cleanup - spinoff from room", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("room ", StringComparison.Ordinal) && !row.Contains("spinoff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_cannot_go_round_the_box_of_an_unsent_spin_off()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Transcript));
        shell.NewKeyDownEvent(Key.K.WithAlt);
        await shell.Settled;

        Assert.Equal(Shell.SpinOffGoesFromTheBox, shell.Said);
    }

    [Fact]
    public async Task A_name_taken_after_it_was_chosen_is_refused_at_the_send_and_does_not_keep_refusing()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"got here first"}""", T0.AddMinutes(2), "disk-cleanup", Ct)).Succeeded);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Contains("Nothing was sent", shell.Said, StringComparison.Ordinal);
        Assert.Single((await Client.ExportAsync("disk-cleanup", Ct)).Messages);

        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.DoesNotContain("Nothing was sent", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_refused_for_any_other_reason_goes_on_refusing()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        _ = await Client.DestroyAsync(Conversation, Ct);

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Contains("Nothing was sent", shell.Said, StringComparison.Ordinal);
        Assert.Empty((await Client.ExportAsync("disk-cleanup", Ct)).Messages);
    }

    [Fact]
    public async Task An_ownership_name_and_a_parent_with_nothing_in_it_are_told_at_once()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "owns-zz");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("facilitator", shell.Said, StringComparison.Ordinal);
        Assert.Equal(Conversation, shell.Transcript.Open);

        await shell.SpinOff();

        form = (FormDialog)driving.Find(SpinningOff.Title);
        form.Fill(0, "first");
        form.Finish(taken: true);
        await shell.Settled;

        await shell.SpinOff();

        form = (FormDialog)driving.Find(SpinningOff.Title);
        form.Fill(0, "second");
        form.Finish(taken: true);
        await shell.Settled;

        Assert.Contains("nothing in it yet", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Who_a_spin_off_was_started_for_does_not_outlast_its_first_send()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        var exchange = Assert.Single((await Client.ExportAsync("disk-cleanup", Ct)).Exchanges).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync("disk-cleanup", "elsewhere", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Conversation, "disk-cleanup");
        await shell.Settled;

        shell.Line.Written = "to nobody now";
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Empty((await Client.ExportAsync("disk-cleanup", Ct)).Messages);
    }

    [Fact]
    public async Task Moving_nothing_says_so()
    {
        var (shell, _, message) = await SeatedAsync();
        using var held = shell;

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await shell.MoveEntries();
        await shell.Settled;

        Assert.Contains("nothing in this conversation to move", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spin_off_someone_else_started_and_emptied_does_not_keep_who_it_was_for()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.SpinOff();

        var form = (FormDialog)driving.Find(SpinningOff.Title);

        form.Fill(0, "disk-cleanup");
        form.Finish(taken: true);
        await shell.Settled;

        // Started by another surface, for somebody else, and everything moved out, while this one was still drafting.
        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var other = await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"first"}""", T0.AddMinutes(2), "disk-cleanup", Ct);

        Assert.True((await Client.MoveExchangesAsync("disk-cleanup", "elsewhere", true, [other.Exchange!.Id], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Conversation, "disk-cleanup");
        await shell.Settled;

        shell.Line.Written = "to nobody now";
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Line));
        shell.NewKeyDownEvent(Key.Enter);
        await shell.Settled;

        Assert.Empty((await Client.ExportAsync("disk-cleanup", Ct)).Messages);
    }

    [Fact]
    public async Task Unticking_part_of_a_request_to_a_role_keeps_it_whole()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var one = await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"to a role a"}""", T0.AddMinutes(2), Conversation, Ct);
        var two = await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"to a role b"}""", T0.AddMinutes(2), Conversation, Ct);

        System.Data.Common.DbConnection raw = _connection;

        using (var group = raw.CreateCommand())
        {
            group.CommandText = $"UPDATE exchanges SET request_group = 'g1' WHERE id IN ({one.Exchange!.Id}, {two.Exchange!.Id});";
            group.ExecuteNonQuery();
        }

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.True(shell.Transcript.Reach(one.Request!.Id));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        reviewing.Rows.SelectedItem = 1;
        reviewing.NewKeyDownEvent(Key.Space);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Contains("2 exchanges", ((ChoiceDialog)driving.Find(Moving.WhereTitle)).Described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unticking_part_of_a_send_to_several_keeps_it_whole()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var voice = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await voice.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var one = await voice.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"same words"}""", T0.AddMinutes(2), Conversation, Ct);
        _ = await voice.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"same words"}""", T0.AddMinutes(2), Conversation, Ct);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.True(shell.Transcript.Reach(one.Request!.Id));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        reviewing.Rows.SelectedItem = 1;
        reviewing.NewKeyDownEvent(Key.Space);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        Assert.Contains("2 exchanges", choosing.Described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_destination_asks_again_with_what_was_picked()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        Assert.True(shell.Transcript.Reach(message));

        await shell.MoveEntries();
        await shell.Settled;
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var choosing = (ChoiceDialog)driving.Find(Moving.WhereTitle);

        driving.Row(choosing.Choices, Moving.New);
        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var naming = (TextDialog)driving.Find(Moving.NameTitle);

        foreach (var key in Conversation)
        {
            naming.NewKeyDownEvent(new Key(key));
        }

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        Assert.Contains("Nothing moved", shell.Said, StringComparison.Ordinal);
        _ = (ChoiceDialog)driving.Find(Moving.WhereTitle);
    }

    [Fact]
    public async Task A_destination_list_reads_every_page_says_what_was_spun_off_and_offers_only_what_could_take_them()
    {
        var (shell, _, _) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        for (var i = 0; i < 60; i++)
        {
            Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", $$"""{"ask":"work {{i}}"}""", T0.AddMinutes(i), $"work-{i}", Ct)).Succeeded);
        }

        Assert.True(await Client.ArchiveAsync("work-1", Ct));
        Assert.True(await Client.ForceConcludeAsync("work-2", null, Ct));
        Assert.Null(await Client.SpinOffAsync("work-3", "child-of-three", Ct));
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"child"}""", T0, "child-of-three", Ct)).Succeeded);

        var destinations = await Parley.Client.Rendering.MovePicking.DestinationsAsync(Client, "work-0", Ct);

        Assert.Contains(destinations, c => c.Id == "work-59");
        Assert.Contains(destinations, c => c.Id == "work-5");
        Assert.Contains(destinations, c => c.Id == "work-1");
        Assert.DoesNotContain(destinations, c => c.Id == "work-2");
        Assert.DoesNotContain(destinations, c => c.Id == "work-0");
        Assert.Equal("work-3", destinations.Single(c => c.Id == "child-of-three").Parent);
        Assert.Contains("child-of-three - spinoff from work-3", FilesPlacing.Row(destinations.Single(c => c.Id == "child-of-three")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_move_to_a_conversation_with_no_link_is_still_somewhere_to_go_and_so_is_a_child()
    {
        var (shell, driving, message) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"elsewhere"}""", T0, "other", Ct)).Succeeded);

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "other", false, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await shell.GoToRelated();
        await shell.Settled;

        var choosing = (ChoiceDialog)driving.Find(Shell.RelatedTitle);

        Assert.Contains("other", choosing.Choices.Source!.ToList().Cast<string>());
        await driving.PressThenSettleAsync(Key.Esc, () => shell.Settled);

        Assert.Null(await Client.SpinOffAsync(Conversation, "kid", Ct));
        Assert.True((await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"kid"}""", T0, "kid", Ct)).Succeeded);

        await shell.GoToRelated();
        await shell.Settled;

        Assert.Contains("kid", ((ChoiceDialog)driving.Find(Shell.RelatedTitle)).Choices.Source!.ToList().Cast<string>());
    }

    [Fact]
    public async Task A_preview_says_why_a_group_grew_through_the_client()
    {
        var (shell, _, _) = await SeatedAsync();
        using var held = shell;

        var seat = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await seat.StartAsync(shell.Held!.Seat.Name, T0, Ct);

        var one = (await seat.RequestAsync(shell.Held!.Seat.Name, Orders, "model-a", """{"ask":"a"}""", T0, Conversation, Ct)).Exchange!.Id;
        var two = (await seat.RequestAsync(shell.Held!.Seat.Name, Billing, "model-a", """{"ask":"b"}""", T0, Conversation, Ct)).Exchange!.Id;

        System.Data.Common.DbConnection raw = _connection;

        using (var group = raw.CreateCommand())
        {
            group.CommandText = $"UPDATE exchanges SET request_group = 'g1' WHERE id IN ({one}, {two});";
            group.ExecuteNonQuery();
        }

        var preview = await Client.PreviewMoveAsync(Conversation, [one], Ct);

        Assert.NotNull(preview.Exchanges.Single(e => e.Id == two).AddedBecause);
        Assert.Null(preview.Exchanges.Single(e => e.Id == one).AddedBecause);
    }

    [Fact]
    public async Task A_transcript_says_what_moved_and_what_it_came_from()
    {
        var (shell, _, message) = await SeatedAsync();
        using var held = shell;

        var exchange = (await Client.ExportAsync(Conversation, Ct)).Messages.Single(m => m.Id == message).ExchangeId;

        Assert.True((await Client.MoveExchangesAsync(Conversation, "disk-cleanup", true, [exchange], Ct)).Moved);

        await Walking.GoAsync(shell, Viewing.Conversation, "disk-cleanup");
        await shell.Settled;

        Assert.Contains("Made by moving entries out of room", shell.Transcript.Written, StringComparison.Ordinal);
        Assert.Contains("moved here from room", shell.Transcript.Written, StringComparison.Ordinal);

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        Assert.Contains("moved from here to disk-cleanup", shell.Transcript.Written, StringComparison.Ordinal);
    }
}
