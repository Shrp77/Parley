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
/// That a dialog the loop reaches after the wait has run out never opens, rather than opening for good.
/// </summary>
/// <remarks>
/// <b>Found by review, round 15.</b> A dialog opened from the work queue after a read waits for the loop, and the wait
/// gives up after five seconds. The open still ran when the loop got there, but its caller had taken it as not opened
/// and kept no session to end, so the dialog took every key and Escape could not close it. Now work the loop has not
/// started by then is given up (<see cref="Prompting.OnTheLoop"/>).
/// </remarks>
[Collection(nameof(OwnsTheMarshal))]
[Integration]
public sealed class ADialogTheLoopReachesLateNeverOpensTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ADialogTheLoopReachesLateNeverOpensTests()
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
    public async Task A_proposal_the_loop_reaches_after_the_wait_never_opens_so_none_is_left_that_cannot_close()
    {
        using var driving = Driving.Open(width: 160, height: 40);
        using var directory = Directory();
        using var shell = await OpenAsync(driving, directory);

        using var loop = StandInLoop.Elsewhere();

        Assert.True(shell.Roadmaps.OpenSelected());

        // The loop is busy past the wait, and gets there only after the work queue has gone on.
        await shell.Settled;
        loop.Pump();

        Assert.IsNotType<ProposalView>(driving.App.TopRunnableView);
    }

    private BrokerDirectory Directory() => new(
        [new ProjectSettings(Walking.Alpha, "unused.db", _broker.Address, null)],
        _ => GrpcChannel.ForAddress(_broker.Address));

    private async Task<Shell> OpenAsync(Driving.Session driving, BrokerDirectory directory)
    {
        var (proposal, problem) = await new RoadmapRepository(_connection).ProposeAsync(
            Orders,
            null,
            null,
            new RoadmapContent("Billing export", [new RoadmapItemDraft(null, "Export invoices", Billing, [])]),
            T0,
            cancellationToken: Ct);
        Assert.Null(problem);
        Assert.NotNull(proposal);

        var shell = new Shell(directory) { MailEvery = null, FollowEvery = null, Seats = new SeatBook(directory.AgentsFor) };

        driving.Show(shell);
        await shell.RefreshAsync(Ct);
        await shell.Settled;
        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;
        await shell.Global().Single(action => action.Label == Proposing.Title).Run();
        await shell.Settled;

        return shell;
    }
}
