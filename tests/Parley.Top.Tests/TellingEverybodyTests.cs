using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// That a person can tell every agent in a project one thing, in one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>N ordinary notifications, not a protocol operation.</b> SPEC.md §5.5 settled the fan-out
/// shape for questions and gave the reason — a one-to-many exchange needs its own admission rules,
/// its own cap arithmetic and its own definition of concluded. All three costs are absent for a
/// message that opens no exchange, so the restraint is easier here than in the case that took it.
/// </para>
/// <para>
/// <b>All agents means agents.</b> Every seat is excluded, the sender's and everybody else's, and
/// a retired agent is not addressed rather than addressed and refused — §14 would refuse it, and a
/// broadcast reporting refusals for agents somebody deliberately retired reads as a fault.
/// </para>
/// </remarks>
public sealed class TellingEverybodyTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly AgentName Inventory = AgentName.Create("inventory@test");

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public TellingEverybodyTests()
    {
        _connection = ParleyDatabase.OpenInMemory();

        foreach (var agent in new[] { Orders, Billing })
        {
            new AgentRepository(_connection).UpsertAsync(
                new AgentRecord(
                    agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }

        _broker = LoopbackOperator.Start(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();

        _connection.Dispose();
    }

    private ProjectSettings Project() =>
        new(ProjectName.Create("acme"), "unused.db", _broker.Address, null, null);

    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }

    private static AgentRecord Agent(
        AgentName name, AgentStatus status = AgentStatus.Active, string adapter = "mcp") =>
        new(name, adapter, AgentTier.Pull, null, null, null, false, status);

    [Fact]
    public async Task Telling_offers_everybody_and_names_how_many()
    {
        // FR-020. Telling nineteen agents starts up to nineteen of them, each a model taking a turn
        // on somebody's machine, and a row reading only "All agents" would hide the difference
        // between three and thirty.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        await shell.StartByTelling();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);
        var rows = Drawn.Rows(picking.Choices).Select(row => row.Trim()).ToList();

        Assert.Contains(Participation.Everyone(2), rows);
        Assert.Equal(Participation.Everyone(2), rows[0]);
    }

    [Fact]
    public async Task Asking_does_not_offer_everybody()
    {
        // A question to each of them opens an exchange apiece and holds a slot apiece under §7.1,
        // so a broadcast question would saturate a small deployment and leave the asker owing
        // answers it cannot conclude.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        await shell.Start();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);
        var rows = Drawn.Rows(picking.Choices).Select(row => row.Trim()).ToList();

        Assert.DoesNotContain(rows, row => row.StartsWith("All agents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_seat_is_counted_among_the_agents()
    {
        // SC-007. A seat is somebody at a desk rather than something that can be started, and
        // "tell all the agents" addressed to a colleague is a different act from the one chosen.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show(
            [
                Agent(Orders),
                Agent(AgentName.Create("arvind@test"), adapter: Seat.Adapter),
                Agent(AgentName.Create("jo@test"), adapter: Seat.Adapter),
            ],
            NodeCatalogue.Empty,
            null);

        await shell.StartByTelling();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);
        var rows = Drawn.Rows(picking.Choices).Select(row => row.Trim()).ToList();

        // One agent, though the roster shows three rows and two of them are people.
        Assert.Equal(Participation.Everyone(1), rows[0]);
    }

    [Fact]
    public async Task A_retired_agent_is_not_addressed_at_all()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show(
            [Agent(Orders), Agent(Inventory, AgentStatus.Retired)], NodeCatalogue.Empty, null);

        await shell.StartByTelling();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);
        var rows = Drawn.Rows(picking.Choices).Select(row => row.Trim()).ToList();

        Assert.Equal(Participation.Everyone(1), rows[0]);
    }

    [Fact]
    public async Task A_project_holding_only_seats_offers_no_everybody_row()
    {
        // There are peers to speak to -- another person's seat is one -- so this is not the empty
        // roster case. There is simply nobody a broadcast would reach.
        //
        // The seat is on another machine deliberately. `Peers()` excludes every seat on *this* one,
        // which is what `Mine` means -- not "my seat" but "a seat here" -- so a colleague sharing
        // this box is already absent from the picker. That is pre-existing and not this feature's
        // to change, but it decides which fixture states the case: a seat here would have produced
        // an empty picker and tested the wrong thing.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show(
            [Agent(AgentName.Create("jo@elsewhere"), adapter: Seat.Adapter)], NodeCatalogue.Empty, null);

        await shell.StartByTelling();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);
        var rows = Drawn.Rows(picking.Choices).Select(row => row.Trim()).ToList();

        Assert.DoesNotContain(rows, row => row.StartsWith("All agents", StringComparison.Ordinal));
        Assert.Contains("jo@elsewhere", rows);
    }

    [Fact]
    public async Task Choosing_everybody_sends_to_each_and_reports_counts()
    {
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        var form = await Composing.WithAsync(
            driving,
            shell.StartByTelling,
            Participation.TellTitle,
            Participation.Everyone(2),
            () => shell.Settled);

        form.Fill(0, "the schema moved");
        form.Finish(taken: true);

        await shell.Settled;

        // Both were told. This deployment has no node, so neither can be started -- which is a fact
        // about the recipients and not a failed send, and the sentence has to say so.
        Assert.Contains("Told all 2", shell.Said, StringComparison.Ordinal);
        Assert.Contains(Orders.Value, shell.Said, StringComparison.Ordinal);
        Assert.Contains(Billing.Value, shell.Said, StringComparison.Ordinal);

        Assert.Equal(2, Sent());
    }

    [Fact]
    public async Task Telling_one_agent_still_works_from_the_same_picker()
    {
        // The everybody row is added to the list rather than replacing it, and a regression here
        // would be invisible in the tests above.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        var form = await Composing.WithAsync(
            driving, shell.StartByTelling, Participation.TellTitle, Orders.Value, () => shell.Settled);

        form.Fill(0, "just you");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.Equal(1, Sent());
    }

    /// <summary>How many notifications reached the store.</summary>
    /// <returns>The count.</returns>
    private int Sent()
    {
        using var command = _connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM messages WHERE type = 'notify';";

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
