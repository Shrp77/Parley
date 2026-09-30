using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That opening a project seats the person with nothing typed, as the broker names them, and that nothing offers a
/// seat to take or give back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rewritten for specs/023.</b> This file tested taking a seat from the agents region by typing a name, a name held
/// by a real agent being refused, leaving, and a malformed name refused as it was typed. All of that was a person
/// choosing who they were; the broker decides now, providing <c>human-user@&lt;project&gt;</c>, and a surface on any
/// machine attaches as the row its registry names. What survives is the property the first test was for — the
/// registry shows the person, verified through the broker rather than read off the screen — and it now needs no key.
/// </para>
/// <para>
/// The refusal of a name an agent holds lives in storage now: the seat's adapter is refused under any other role.
/// </para>
/// </remarks>
public sealed class SeatingRegionTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public SeatingRegionTests()
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
    public async Task Opening_a_project_seats_the_person_and_the_registry_shows_them_connected()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.NotNull(shell.Held);
        Assert.True(PersonSeat.Is(shell.Held!.Seat.Name), $"seated as '{shell.Held.Seat.Name}'");

        // Verified through the broker rather than by reading the screen, which is what SC-002 asks of every journey:
        // the registry is the fact, the row is a rendering of it.
        var client = new BrokerClient(directory.ClientFor(Project()));

        await Eventually.UntilAsync(
            () => client.RegistryAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()
                .Any(agent => PersonSeat.Is(agent.Name) && agent.Adapter == Seat.Adapter && agent.Connected),
            "the person was never connected in the registry",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nothing_anywhere_offers_to_join_or_leave()
    {
        // The actions that existed only because the seat did not: taking one, taking one first, taking the one under
        // the cursor, and giving one back. Asserted by walking every pane of every view, so a leftover in a pane nobody
        // thought to check is still found.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            Assert.DoesNotContain(
                shell.Actions(),
                action => action.Label.Contains("seat", StringComparison.OrdinalIgnoreCase)
                    || action.Label.Contains("join", StringComparison.OrdinalIgnoreCase));
        }
    }

    private ProjectSettings Project() =>
        new(Walking.Alpha, string.Empty, _broker.Address, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
