using System.Text.Json.Nodes;
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
/// The room's <i>waiting on you</i>: each agent once with a count, and Enter for a list that takes the person to each
/// question in its context (the owner's design of 2026-10-05).
/// </summary>
/// <remarks>
/// <b>Read off the drawn rows and driven by keys</b>, over a real socket. Each fails against 0.40.1, which showed one row
/// per agent reading "waiting for your reply", pointing at one question picked by the order the broker listed them.
/// </remarks>
public sealed class WaitingOnYouListTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;
    private readonly Broker.Broker _seeding;

    public WaitingOnYouListTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        foreach (var agent in new[] { Orders, Billing })
        {
            new AgentRepository(_connection).UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
        _seeding = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_agent_is_one_row_with_its_count()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await AskedAsync(driving, directory, seats);

        driving.App.LayoutAndDraw(true);

        Assert.Single(shell.Room.Written, row => row.Trim() == "orders (3)");
        Assert.Single(shell.Room.Written, row => row.Trim() == "billing (1)");
        Assert.DoesNotContain(shell.Room.Written, row => row.Contains("waiting for your reply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clarify_lists_each_question_waiting_on_the_person_once()
    {
        // The chooser is what the seat holds joined with what the room shows waiting, so a question that is both must
        // appear once. Must fail against a join that adds the room's questions without removing the ones already held.
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await AskedAsync(driving, directory, seats);

        await shell.Clarify();

        var chooser = (ChoiceDialog)driving.Find(Participation.ClarifyTitle);
        var entries = chooser.Choices.Source!.ToList().Cast<string>().ToList();

        Assert.Equal(4, entries.Count);
        Assert.Single(entries, e => e.Contains("which invoice schema?", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Contains("which currency?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enter_lists_the_questions_oldest_first_and_choosing_one_shows_it()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await AskedAsync(driving, directory, seats);

        driving.App.LayoutAndDraw(true);

        var listing = OpenList(shell, driving, "orders (3)");
        var entries = listing.Choices.Source!.ToList().Cast<string>().ToList();

        Assert.Equal(3, entries.Count);
        Assert.Contains("which invoice schema?", entries[0], StringComparison.Ordinal);
        Assert.Contains("gross or net?", entries[1], StringComparison.Ordinal);
        Assert.Contains("who owns refunds?", entries[2], StringComparison.Ordinal);

        driving.Row(listing.Choices, "gross or net?");
        driving.Enter();

        await shell.Settled;

        Assert.Equal(shell.Panes.ToList().IndexOf(shell.Transcript), shell.FocusedRegion);

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, Ct);
        var asked = export!.Messages.Single(m => m.PayloadJson.Contains("gross or net?", StringComparison.Ordinal));

        Assert.Equal(shell.Transcript.Rows.ToList().IndexOf(asked.Id), shell.Transcript.List.SelectedItem);
    }

    [Fact]
    public async Task A_question_settled_while_the_list_was_open_says_so_and_moves_nothing()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await AskedAsync(driving, directory, seats);

        driving.App.LayoutAndDraw(true);

        var listing = OpenList(shell, driving, "orders (3)");

        // orders settles its first question in a reply to something the person wrote.
        var person = shell.Held!.Seat.Name;
        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, Ct);
        var first = export!.Messages.First(m => m.PayloadJson.Contains("which invoice schema?", StringComparison.Ordinal));
        var line = await _seeding.RequestAsync(
            person, Orders, "human:human-user", Ask("schema 12"), T0.AddMinutes(20), Conversation, Ct);
        var settled = await _seeding.ResultAsync(
            line.Exchange!.Id, Orders, "model-a",
            new JsonObject
            {
                ["outcomes"] = new JsonArray(new JsonObject { ["id"] = "request", ["outcome"] = "answered", ["text"] = "ok" })
            }.ToJsonString(),
            T0.AddMinutes(21), Ct, settles: [first.ExchangeId]);

        Assert.True(settled.Succeeded, $"refused: {settled.Error}");

        // The room is read again, as every follow does.
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        var before = shell.Transcript.List.SelectedItem;

        driving.Row(listing.Choices, "which invoice schema?");
        driving.Enter();

        await shell.Settled;

        Assert.Equal(RoomRegion.SettledSince, shell.Said);
        Assert.Equal(before, shell.Transcript.List.SelectedItem);
    }

    private static ChoiceDialog OpenList(Shell shell, Driving.Session driving, string row)
    {
        shell.Focus(shell.Panes.ToList().FindIndex(pane => pane is RoomRegion));
        driving.Row(shell.Room.List, row);
        shell.Room.List.NewKeyDownEvent(Key.Enter);

        return (ChoiceDialog)driving.Find(RoomRegion.ListTitle(shell.Room.Selected!.Peer.Value));
    }

    private static string Ask(string said) => new JsonObject { ["ask"] = said }.ToJsonString();

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<Shell> AskedAsync(Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.NotNull(shell.Held);

        var person = shell.Held!.Seat.Name;

        await _seeding.StartAsync(Orders, T0, Ct);
        await _seeding.StartAsync(Billing, T0, Ct);

        var minute = 1;

        foreach (var asked in new[] { "which invoice schema?", "gross or net?", "who owns refunds?" })
        {
            var sent = await _seeding.RequestAsync(
                Orders, person, "model-a", Ask(asked), T0.AddMinutes(minute++), Conversation, Ct);

            Assert.True(sent.Exchange is not null, $"refused: {sent.Admission.Rejection}");
        }

        // billing joins by being asked something by the person, then asks the person one thing back.
        await _seeding.RequestAsync(person, Billing, "human:human-user", Ask("are you in?"), T0.AddMinutes(minute++), Conversation, Ct);

        var billings = await _seeding.RequestAsync(
            Billing, person, "model-a", Ask("which currency?"), T0.AddMinutes(minute), Conversation, Ct);

        Assert.True(billings.Exchange is not null, $"refused: {billings.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        return shell;
    }
}
