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
/// That an item's words never reach the box unmarked when the loop reaches them late.
/// </summary>
/// <remarks>
/// <b>Found by review, round 14.</b> Starting an item fills the box on the loop and marks the words as the item's, so
/// Enter sends them rather than running them as a command (PlansTests). The mark was made from the work queue after the
/// fill returned, and the wait for the loop gives up after five seconds: a loop later than that showed the words
/// unmarked, and <c>/add orders@test</c> as an item's words added orders to who the line reaches. Now work the loop has
/// not started by then is given up (<see cref="Prompting.OnTheLoop"/>), so a late fill leaves the box as it was, and
/// the person starts the item again. Slow (the waits run out), and alone, because it stands a loop in
/// (<see cref="OwnsTheMarshal"/>).
/// </remarks>
[Collection(nameof(OwnsTheMarshal))]
public sealed class AnItemsWordsStayWordsWhenTheLoopIsLateTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public AnItemsWordsStayWordsWhenTheLoopIsLateTests()
    {
        _connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(_connection);

        foreach (var name in new[] { Orders, Billing })
        {
            agents.UpsertAsync(
                    new AgentRecord(name, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                    TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();
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
    public async Task An_item_the_loop_reaches_after_the_wait_leaves_the_box_empty_rather_than_unmarked()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
            _ => GrpcChannel.ForAddress(_broker.Address));
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

        using var loop = StandInLoop.Elsewhere();

        view.OpenSelected();

        // The loop is busy: nothing handed over runs until the work queue has gone past every wait.
        await shell.Settled;
        loop.Pump();

        Assert.Equal(string.Empty, shell.Line.Written);
    }
}
