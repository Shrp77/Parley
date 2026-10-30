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
/// That work the loop gave up leaves no record saying it was done.
/// </summary>
/// <remarks>
/// <b>Found by review, round 16.</b> Work the loop has not started within five seconds is given up
/// (<see cref="Prompting.OnTheLoop"/>), and two callers went on as if it had run: arriving at another conversation
/// recorded the box as that conversation's while it still held the words of the one left, and a send cleared the mark on
/// an item's words that stayed in the box. Now both are recorded inside the work, and Enter does not send words written
/// in another conversation unasked. Slow (the waits run out), and alone (<see cref="OwnsTheMarshal"/>).
/// </remarks>
[Collection(nameof(OwnsTheMarshal))]
[Integration]
public sealed class GivenUpWorkLeavesNoFalseRecordTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public GivenUpWorkLeavesNoFalseRecordTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        foreach (var name in new[] { Orders, Billing })
        {
            new AgentRepository(_connection).UpsertAsync(
                new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    [Fact]
    public async Task Words_written_in_one_conversation_do_not_reach_another_when_the_loop_is_late_at_the_switch()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Conversation, "scratch");
        shell.Line.Written = "private note never meant for orders";

        var loop = StandInLoop.Elsewhere();

        try
        {
            // The switch empties the box on the loop; the loop is late, so the emptying is given up.
            await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
            loop.Pump();
        }
        finally
        {
            loop.Dispose();
        }

        // Now in the room with orders, and the line is still the one written in scratch.
        shell.Line.Accept();
        await shell.Settled;

        // Kept and said, not sent: they are this conversation's now, so a second Enter would send them here.
        Assert.Equal(Shell.WrittenElsewhere, shell.Said);
        Assert.Equal("private note never meant for orders", shell.Line.Written);

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, Ct);

        Assert.DoesNotContain(
            export!.Messages,
            m => m.PayloadJson.Contains("private note never meant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_sent_while_the_loop_is_late_is_sent_once()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        shell.Line.Written = "Green on my side.";

        var loop = StandInLoop.Elsewhere();

        try
        {
            shell.Line.Accept();
            await shell.Settled;
            loop.Pump();
        }
        finally
        {
            loop.Dispose();
        }

        var said = shell.Said;
        var left = shell.Line.Written;

        // The message line says it was sent; the box still holds the words it says were sent.
        shell.Line.Accept();
        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, Ct);
        var sent = export!.Messages.Count(m => m.PayloadJson.Contains("Green on my side", StringComparison.Ordinal));

        Assert.True(sent == 1, $"sent {sent} times; message line read '{said}', box held '{left}'");
    }

    [Fact]
    public async Task An_items_words_left_in_the_box_after_a_late_send_are_still_not_a_command()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        var roadmaps = new RoadmapRepository(_connection);
        var (proposal, problem) = await roadmaps.ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent("Billing export", [new RoadmapItemDraft(null, "/add orders@test", Billing, [])]),
            T0,
            cancellationToken: Ct);
        Assert.Null(problem);
        var waiting = await roadmaps.GetProposalAsync(proposal!.Value, Ct);
        await roadmaps.ConfirmAsync(waiting!.Id, waiting.Content, null, T0, cancellationToken: Ct);

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Global().Single(action => action.Label == Proposing.Title).Run();
        await shell.Settled;
        shell.Focus(shell.Panes.ToList().IndexOf(shell.Plans));
        Assert.True(shell.Plans.OpenSelected());
        await shell.Settled;

        var view = Assert.IsType<PlanView>(driving.App.TopRunnableView);
        view.Items.SelectedItem = 0;
        view.OpenSelected();
        await shell.Settled;

        Assert.Equal("/add orders@test", shell.Line.Written);

        var loop = StandInLoop.Elsewhere();

        try
        {
            // Sent to billing as text; clearing the box after the send is given up.
            shell.Line.Accept();
            await shell.Settled;
            loop.Pump();
        }
        finally
        {
            loop.Dispose();
        }

        var first = shell.Said;
        var left = shell.Line.Written;

        shell.Line.Accept();
        await shell.Settled;

        Assert.False(
            shell.Said.Contains("will get this as well", StringComparison.Ordinal),
            $"first Enter: '{first}'; box then held '{left}'; second Enter: '{shell.Said}'");
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<Shell> SeatedAsync(Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats, MailEvery = null, FollowEvery = null };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        Assert.NotNull(shell.Held);

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await broker.StartAsync(Orders, T0, Ct);

        var asked = await broker.RequestAsync(
            Orders, shell.Held!.Seat.Name, "model-a", """{"ask":"which invoice schema?"}""",
            T0.AddMinutes(1), Conversation, Ct);

        Assert.True(asked.Exchange is not null, $"the fixture's request was refused: {asked.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
        await shell.Settled;

        shell.Line.Written = string.Empty;
        await shell.Settled;

        return shell;
    }
}
