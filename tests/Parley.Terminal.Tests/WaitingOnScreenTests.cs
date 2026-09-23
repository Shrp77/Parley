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
/// That a seated person is told what is waiting for them without going to look.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner, an hour after the console was fixed for the same fault: "where do I go to see what's waiting on
/// me?"</b> The answer was to take a seat, open a conversation — any conversation — and press a key inside the
/// transcript, which reported what it found. Nothing on the screen ever said there was anything to find, and what
/// is waiting is frequently in a conversation nobody has opened, which is exactly the one you would not think to
/// open.
/// </para>
/// <para>
/// <b>The console's fix was argued not to apply here and that argument was wrong.</b> It held that the terminal
/// remembers no name between runs, so there is nothing to count for — true only until somebody takes a seat, which
/// is the whole of the case that matters. A surface that knows who you are and will not say what is waiting wastes
/// more of somebody's time than one that does not know, because they are sitting in front of it.
/// </para>
/// <para>
/// Over a real socket, because what is being checked is that the broker is asked at all.
/// </para>
/// </remarks>
public sealed class WaitingOnScreenTests : IDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static readonly TimeSpan Quickly = TimeSpan.FromMilliseconds(150);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Ask = """{"ask":"Which file validates the licence key?"}""";

    private readonly SqliteConnection _connection;

    public WaitingOnScreenTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(_connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task What_is_waiting_is_on_the_status_line_without_anybody_going_to_look()
    {
        // The report itself. Nothing is opened, no key is pressed, and the number appears.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await SendAsync(shell, ct);

        await Eventually.UntilAsync(
            () => shell.Mail is { Certain: true, Waiting: 1 },
            "the seat was never told anything was waiting",
            ct);

        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Contains("1 waiting", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_count_says_which_key_collects_it()
    {
        // A number with no way to act on it is worse than no number: it tells somebody they are behind and
        // leaves them hunting. The key it names is the one the agents pane offers.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await SendAsync(shell, ct);

        await Eventually.UntilAsync(
            () => shell.Mail is { Certain: true, Waiting: > 0 }, "nothing was ever counted", ct);

        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.Contains(Participation.DeliveryTitle, driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taking_delivery_is_offered_where_the_seat_is_rather_than_only_inside_a_transcript()
    {
        // It was offered only in the transcript region, so collecting meant opening a conversation first --
        // and what is waiting is often in one nobody has seen.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open();
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        // Focused where the seat is listed, which is the pane a person sits in rather than a transcript.
        var pane = Enumerable.Range(0, shell.Panes.Count)
            .First(at => shell.Panes[at] is AgentsRegion);

        shell.Focus(pane);

        Assert.Contains(
            shell.Actions(),
            action => action.Label == Participation.DeliveryTitle);
    }

    [Fact]
    public async Task The_count_goes_when_the_seat_does()
    {
        // A number left on the line under nobody's name is somebody else's mail. The seat owns it.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await SendAsync(shell, ct);

        await Eventually.UntilAsync(
            () => shell.Mail is { Certain: true, Waiting: > 0 }, "nothing was ever counted", ct);

        await shell.LeaveSeat();
        await shell.Settled;

        Assert.Null(shell.Held);
        Assert.Null(shell.Mail);

        driving.App.LayoutAndDraw(true);

        Assert.DoesNotContain("waiting (", driving.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_waiting_puts_nothing_on_the_line()
    {
        // The line is for what is owed. A seated person with an empty inbox must not be given a zero to read,
        // any more than the console is: "none" is not news.
        var ct = TestContext.Current.CancellationToken;

        await using var broker = LoopbackOperator.Start(_connection);
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Eventually.UntilAsync(
            () => shell.Mail is { Certain: true }, "the seat never asked what was waiting", ct);

        Assert.Equal(0, shell.Mail!.Value.Waiting);

        await shell.Settled;
        driving.App.LayoutAndDraw(true);

        Assert.DoesNotContain("0 waiting", driving.Text(), StringComparison.Ordinal);
    }

    /// <summary>Sends the seat a Request from a peer, the way one really arrives.</summary>
    private async Task SendAsync(Shell shell, CancellationToken cancellationToken)
    {
        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, cancellationToken);

        var sent = await broker.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", Ask, T0.AddMinutes(1), "chat", cancellationToken);

        Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");
    }

    private static BrokerDirectory Directory(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("acme"), "unused.db", broker.Address, null, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = Quickly };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);

        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }
}
