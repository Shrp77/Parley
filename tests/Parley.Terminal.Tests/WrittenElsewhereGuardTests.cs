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
/// That Enter's guard against words written in another conversation neither drops what makes an item's words text nor
/// lets words through a box whose owner was never recorded.
/// </summary>
/// <remarks>
/// <b>Found by review, round 17.</b> Both need an arrival whose emptying the loop gave up. The guard cleared the item mark,
/// so the second Enter ran an item's words as a command; and it ran only when an owner was recorded, so a first arrival
/// given up let the words of one conversation reach another. Slow, and alone (<see cref="OwnsTheMarshal"/>).
/// </remarks>
[Collection(nameof(OwnsTheMarshal))]
public sealed class WrittenElsewhereGuardTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Conversation = "room";

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public WrittenElsewhereGuardTests()
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

    /// <summary>
    /// An item's words carried out of its conversation by a late switch are kept by the guard with their mark, so the
    /// second Enter, which the guard's sentence invites, sends them as words rather than running them as a command.
    /// </summary>
    [Fact]
    public async Task An_items_words_carried_into_another_conversation_by_a_late_switch_are_still_not_a_command()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, enterRoom: true);

        var roadmaps = new RoadmapRepository(_connection);
        var (proposal, problem) = await roadmaps.ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent("Billing export", [new RoadmapItemDraft(null, "/add billing@test", Orders, [])]),
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

        Assert.Equal("/add billing@test", shell.Line.Written);

        var loop = StandInLoop.Elsewhere();

        try
        {
            // F3 and the like: into the room with orders, and the emptying is given up.
            await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
            loop.Pump();
        }
        finally
        {
            loop.Dispose();
        }

        shell.Line.Accept();
        await shell.Settled;
        Assert.Equal(Shell.WrittenElsewhere, shell.Said);
        Assert.Equal("/add billing@test", shell.Line.Written);

        // "Press Enter again to send them here": the person does.
        shell.Line.Accept();
        await shell.Settled;

        Assert.False(
            shell.Said.Contains("will get this as well", StringComparison.Ordinal),
            $"second Enter ran the item's words as a command: '{shell.Said}'");
    }

    /// <summary>
    /// A box whose owner was never recorded, because the first arrival at a conversation was given up, is not anyone's:
    /// a later late switch does not let the first conversation's words through.
    /// </summary>
    [Fact]
    public async Task Words_written_before_any_arrival_was_recorded_do_not_reach_another_conversation()
    {
        using var driving = Driving.Open(width: 140, height: 40);
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats, enterRoom: false);

        var first = StandInLoop.Elsewhere();

        try
        {
            await Walking.GoAsync(shell, Viewing.Conversation, "scratch");
            first.Pump();
        }
        finally
        {
            first.Dispose();
        }

        shell.Line.Written = "private note never meant for orders";

        var second = StandInLoop.Elsewhere();

        try
        {
            await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
            second.Pump();
        }
        finally
        {
            second.Dispose();
        }

        shell.Line.Accept();
        await shell.Settled;

        var export = await new BrokerClient(_broker.Operators).ExportAsync(Conversation, Ct);

        Assert.True(
            !export!.Messages.Any(m => m.PayloadJson.Contains("private note never meant", StringComparison.Ordinal)),
            $"words written in scratch were sent to orders in the room; message line: '{shell.Said}'");
    }

    private BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<Shell> SeatedAsync(Driving.Session driving, BrokerDirectory directory, SeatBook seats, bool enterRoom)
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

        if (enterRoom)
        {
            await Walking.GoAsync(shell, Viewing.Conversation, Conversation);
            await shell.Settled;

            shell.Line.Written = string.Empty;
            await shell.Settled;
        }

        return shell;
    }
}
