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
        Assert.Contains(rows, row => row.Contains(two.Exchange!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal) && row.Contains("Added:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_move_list_scrolls_rather_than_cutting_anything_off_and_names_the_files()
    {
        var (shell, driving, _) = await SeatedAsync();
        using var held = shell;

        var ids = new List<long>();

        for (var i = 0; i < 12; i++)
        {
            ids.Add(await AskPersonAsync(shell, $"question {i}"));
        }

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;
        await shell.MoveEntries();
        await shell.Settled;

        var ticking = (TickDialog)driving.Find(Moving.Title);

        foreach (var _ in Enumerable.Range(0, 1))
        {
            ticking.NewKeyDownEvent(Key.A.WithAlt);
        }

        await driving.PressThenSettleAsync(Key.Enter, () => shell.Settled);

        var reviewing = (TickDialog)driving.Find(Moving.ReviewTitle);

        Assert.Contains("13 exchanges", reviewing.Described, StringComparison.Ordinal);
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
