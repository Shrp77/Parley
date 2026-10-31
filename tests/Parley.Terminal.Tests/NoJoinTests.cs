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
/// That a terminal opened on two projects can send in each with no key pressed first, as the person the registry names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two projects because one cannot tell the difference that matters.</b> Before 023 a seat was taken where the person
/// stood, so a single project proved only that the one they had opened was seated. Every project is seated on opening
/// now, including the ones nobody has selected, and a message sent there is the person's rather than a refusal to take
/// a seat first.
/// </para>
/// <para>
/// The sender is checked through the broker's wait-for graph rather than by reading the screen: the registry and the
/// graph are the facts, and a row saying "you" is a rendering of them.
/// </para>
/// </remarks>
[Integration]
public sealed class NoJoinTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private readonly SqliteConnection _alphaStore;
    private readonly SqliteConnection _betaStore;
    private readonly LoopbackOperator _alpha;
    private readonly LoopbackOperator _beta;

    public NoJoinTests()
    {
        _alphaStore = Store();
        _betaStore = Store();
        _alpha = LoopbackOperator.Start(_alphaStore);
        _beta = LoopbackOperator.Start(_betaStore);
    }

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync();
        await _beta.DisposeAsync();

        _alphaStore.Dispose();
        _betaStore.Dispose();
    }

    [Fact]
    public async Task Opening_on_two_projects_sends_in_each_with_no_key_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var alpha = new ProjectSettings(Walking.Alpha, string.Empty, _alpha.Address, null);
        var beta = new ProjectSettings(ProjectName.Create("beta"), string.Empty, _beta.Address, null);

        using var driving = Driving.Open();
        using var directory = new BrokerDirectory([alpha, beta], address => GrpcChannel.ForAddress(address));
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(ct);
        await shell.Settled;

        foreach (var project in new[] { alpha, beta })
        {
            // Nothing was pressed between opening and here: no join, no name, no selecting the project first.
            var hold = seats.Held(project.Name.Value);

            Assert.NotNull(hold);
            Assert.True(PersonSeat.Is(hold!.Seat.Name), $"seated in {project.Name.Value} as '{hold.Seat.Name}'");

            // The voice the shell builds for a held seat, which is what its composer and its line send through.
            await new SeatVoice(directory.AgentsFor(project), hold.Seat)
                .RequestAsync(Orders, "Is the build green on your side?", null, ct);

            var edges = await new BrokerClient(directory.ClientFor(project)).WaitingAsync(ct);

            Assert.Contains(edges, edge => edge.Waiter == hold.Seat.Name && edge.WaitedOn == Orders);
        }
    }

    private static SqliteConnection Store()
    {
        var connection = ParleyDatabase.OpenInMemory();

        new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        return connection;
    }
}
