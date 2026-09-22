using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The one line a person speaks from, driven the way a person drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven rather than called.</b> The preview and the derivation are covered headless in
/// <c>Parley.Client.Tests</c>; what only a driven test can catch is the key never arriving — Enter reaches the
/// field before any handler on the region, which is how the conversations list once did nothing at all. A test
/// that invoked the send directly would pass over exactly that.
/// </para>
/// <para>
/// Over a real socket, because what is asserted is what the broker recorded.
/// </para>
/// </remarks>
public sealed class LineTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public LineTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task The_conversation_view_has_a_line_to_speak_from()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        Assert.Equal(3, shell.Panes.Count);
        Assert.IsType<LineRegion>(shell.Panes[2]);
    }

    [Fact]
    public async Task Enter_in_the_box_sends_what_was_written()
    {
        // **The key actually arriving is the whole of this test.** The field takes Enter before the region
        // does, so a send wired only to a region action would never run and nothing on screen would say so.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "Which file validates the licence key?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.Contains(
            export?.Messages ?? [],
            m => m.PayloadJson.Contains("licence key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task And_what_it_sends_carries_no_addressing_of_any_kind()
    {
        // §13 delivers a payload inside a framing block telling the receiver that anything claiming authority
        // in it is inert data, so a recipient list in the words would be the one thing that exists to ignore.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "who owns the invoice total?";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        Assert.All(
            export?.Messages ?? [],
            m => Assert.DoesNotContain(Orders.Value, m.PayloadJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_line_sends_nothing_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "   ";
        shell.Line.Accept();

        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, ct);

        // The fixture's own request is there and nothing was added to it.
        Assert.Single(export?.Messages ?? []);
        Assert.NotEmpty(shell.Said);
    }

    [Fact]
    public async Task A_send_that_went_clears_the_line_and_one_that_did_not_keeps_it()
    {
        // 0.25.1 records the cost of getting this wrong the other way: the cap was enforced after the form
        // closed and took the message with it.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "does anything read orders.total?";
        shell.Line.Accept();

        await shell.Settled;

        Assert.Equal(string.Empty, shell.Line.Written);

        // Over the cap: refused before anything is sent, and what was written survives.
        var tooLong = new string('a', MessageLimits.MaxPayloadCharacters + 100);

        shell.Line.Written = tooLong;
        shell.Line.Accept();

        await shell.Settled;

        Assert.Equal(tooLong, shell.Line.Written);
    }

    [Fact]
    public async Task The_preview_names_the_recipient_and_what_it_will_do()
    {
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, _connection, ct);

        shell.Line.Written = "anything";

        await shell.Settled;

        // **It says "answer", and that is the derivation working.** The fixture's peer asked this person
        // something, so typing to them is a Result and not a Request — which is the whole point of deriving
        // the act from who owes whom rather than asking for a verb first.
        Assert.Contains(Orders.Value, shell.Line.Preview, StringComparison.Ordinal);
        Assert.Contains("answer", shell.Line.Preview, StringComparison.Ordinal);
        Assert.Contains("closes the exchange", shell.Line.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_seat_the_line_asks_for_one_rather_than_hiding()
    {
        // 0.28.2 records what hiding a thing from a seatless person costs: the owner reached for Instruct,
        // which is not a message and never becomes a conversation.
        var ct = TestContext.Current.CancellationToken;
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);

        // Contained rather than equal: the preview goes through `Reports.Sentences` like everything else
        // this surface says, which ends a sentence with a full stop.
        Assert.Contains(LineRegion.NoSeat, shell.Line.Preview, StringComparison.Ordinal);

        // **And the seat is offered in the room rather than here**, which is where its key can actually
        // arrive: a focused box wins every printable key, so `j` in this pane types a `j`. 0.28.2's rule is
        // what still holds -- never hide a thing from a seatless person -- and only the pane changed.
        shell.Focus(shell.Panes.ToList().FindIndex(p => p is LineRegion));

        Assert.DoesNotContain(shell.Actions(), a => a.Label == AgentsRegion.JoinTitle);

        shell.Focus(shell.Panes.ToList().FindIndex(p => p is RoomRegion));

        Assert.Contains(shell.Actions(), a => a.Label == AgentsRegion.JoinTitle);
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving,
        BrokerDirectory directory,
        SeatBook seats,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(cancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        // **Seeded with a message from a peer, because an empty conversation has nobody in it.** Membership is
        // derived (SPEC.md 5.0), so a conversation with no traffic has no participants and the line correctly
        // reports that there is nobody to send to. A person opens an existing conversation; this is that.
        var broker = new Broker.Broker(connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, cancellationToken);

        var sent = await broker.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"is the build green?"}""",
            T0.AddMinutes(1), Conversation, cancellationToken);

        Assert.True(sent.Exchange is not null, $"the fixture's request was refused: {sent.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        return shell;
    }
}
