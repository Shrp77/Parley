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
/// That a person takes a seat from the agents region, is present in the registry within seconds,
/// and is refused a name that belongs to a real agent.
/// </summary>
/// <remarks>
/// <para>
/// User Story 2's first scenario. The guard is <c>SeatBook</c>'s and is not re-implemented here:
/// what a seat adds that no operator operation grants is the ability to <i>speak as</i> somebody,
/// and a transcript recording words an agent never said is a different kind of wrong from an
/// operator visibly instructing it.
/// </para>
/// <para>
/// The registry is read before the seat is taken and the read is <b>not</b> contained into a
/// shrug. Everywhere else on this surface an unreachable broker leaves one project unusable and
/// the rest fine; here it has to stop the join, because the check that makes seating safe is a
/// question asked of that broker.
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
    public async Task A_person_takes_a_seat_and_the_registry_shows_them_present()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();

        var dialog = await OpenJoinAsync(driving, shell, "arvind");

        Assert.Equal(string.Empty, dialog.Problem);
        Assert.Contains(AgentsRegion.Took, shell.Said, StringComparison.Ordinal);
        Assert.NotNull(shell.Held);

        // Verified through the broker rather than by reading the screen, which is what SC-002 asks
        // of every journey: the registry is the fact, the row is a rendering of it.
        var registry = await new BrokerClient(directory.ClientFor(Project()))
            .RegistryAsync(TestContext.Current.CancellationToken);

        Assert.Contains(registry, agent => agent.Name.Role == "arvind" && agent.Adapter == Seat.Adapter);
        Assert.Contains(shell.Agents.Rows, row => row.Seat && row.State == AgentsRegion.Seated);
    }

    [Fact]
    public async Task A_name_the_registry_holds_for_a_real_agent_is_refused_with_the_reason()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();
        await OpenJoinAsync(driving, shell, "orders");

        Assert.Null(shell.Held);
        Assert.DoesNotContain(AgentsRegion.Took, shell.Said, StringComparison.Ordinal);
        Assert.Contains("orders", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_releases_the_seat_and_says_so()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        driving.Tab();
        await OpenJoinAsync(driving, shell, "arvind");

        await shell.LeaveSeat();
        await shell.Settled;

        Assert.Null(shell.Held);
        Assert.Contains(AgentsRegion.Left, shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_name_is_refused_while_it_is_being_typed()
    {
        // Checked as it is typed rather than on submission, which is what 004's forms did and what
        // use added to them after its plan. A dialog that only refused at the end would be a step
        // backwards wearing a new toolkit.
        Assert.NotNull(AgentsRegion.NameProblem("not a name at all!"));
        Assert.Null(AgentsRegion.NameProblem("arvind"));
    }

    [Fact]
    public async Task The_status_line_offers_the_seat_from_the_agents_region_and_nowhere_else()
    {
        // Actions belong to the region that shows the thing acted on (User Story 2). Offering the
        // seat from the projects view would be the menu this replaced, wearing a status line.
        //
        // 006 moved the agents region inside a project rather than beside the projects list, so
        // reaching it is one step in rather than one Tab across. The property is unchanged.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);

        Assert.DoesNotContain(shell.Actions(), action => action.Label == AgentsRegion.JoinTitle);

        await Walking.GoAsync(shell, Viewing.Project);

        Assert.Contains(shell.Actions(), action => action.Label == AgentsRegion.JoinTitle);
        Assert.Contains(AgentsRegion.JoinTitle, shell.Status, StringComparison.Ordinal);
    }

    /// <summary>Opens the join dialog, types a name, and accepts it.</summary>
    /// <param name="driving">The application.</param>
    /// <param name="shell">The surface.</param>
    /// <param name="name">What to type.</param>
    /// <returns>The dialog, after it has been answered.</returns>
    private static async Task<TextDialog> OpenJoinAsync(
        Driving.Session driving, Shell shell, string name)
    {
        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type(name);
        dialog.Finish(taken: true);

        await shell.Settled;

        return dialog;
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
