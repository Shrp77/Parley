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

namespace Parley.Terminal.Tests;

/// <summary>
/// That a person can send every agent in a project one request, in one act.
/// </summary>
/// <remarks>
/// <para>
/// <b>N ordinary Requests, not a protocol operation.</b> SPEC.md §5.5 settled the fan-out shape and gave
/// the reason — a one-to-many exchange needs its own admission rules, its own cap arithmetic and its own
/// definition of finished. N exchanges needs none of those, and each is owed its own Result.
/// </para>
/// <para>
/// <b>All agents means agents.</b> Every seat is excluded, the sender's and everybody else's, and a retired
/// agent is not addressed rather than addressed and refused — §14 would refuse it, and a report of
/// refusals for agents somebody deliberately retired reads as a fault.
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

        foreach (var machine in _machines)
        {
            machine.Dispose();
        }

        _connection.Dispose();
    }

    private ProjectSettings Project() =>
        // `Walking.Alpha` rather than a name of its own: `Walking.GoAsync` places the shell in `alpha`,
        // and a directory holding a different name is a project view for a project the directory does
        // not contain -- which production cannot produce, since the only route in is a row that came
        // from the directory.
        new(Walking.Alpha, "unused.db", _broker.Address, null);

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

    /// <summary>Fills the composer with a Request the surface accepts.</summary>
    /// <param name="form">The composer.</param>
    /// <param name="ask">What is being asked for.</param>
    private static void Written(FormDialog form, string ask)
    {
        form.Fill(0, ask);
    }

    private async Task<IReadOnlyList<string>> PickerRowsAsync(Driving.Session driving, Shell shell)
    {
        await shell.Start();

        var picking = (ChoiceDialog)driving.Find(Participation.WhichPeer);

        return [.. Drawn.Rows(picking.Choices).Select(row => row.Trim())];
    }

    [Fact]
    public async Task Everybody_is_offered_first_and_says_how_many()
    {
        // FR-020. Sending nineteen agents a request starts up to nineteen of them, each a model taking a turn
        // on somebody's machine, and a row reading only "All agents" would hide the difference between three
        // and thirty. First, because it is what somebody scrolling a long roster came for.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        var rows = await PickerRowsAsync(driving, shell);

        Assert.Equal(Participation.Everyone(2), rows[0]);
    }

    [Fact]
    public async Task Choosing_everybody_opens_one_exchange_each_and_says_what_is_owed()
    {
        // §5.5: one ordinary exchange with each agent, not a new kind of message. Two agents, two Requests,
        // two open exchanges — counted from the store rather than from the sentence, because a report saying
        // two and a store holding one is the failure worth catching.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.StartTitle, Participation.Everyone(2), () => shell.Settled);

        Written(form, "does anything here read orders.total?");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.Contains("Sent to all 2", shell.Said, StringComparison.Ordinal);
        Assert.Contains("owed a Result", shell.Said, StringComparison.Ordinal);

        // This deployment has no node, so neither can be started -- a fact about the recipients and not a
        // failed send, and the sentence names them.
        Assert.Contains(Orders.Value, shell.Said, StringComparison.Ordinal);
        Assert.Contains(Billing.Value, shell.Said, StringComparison.Ordinal);

        Assert.Equal(2, Requests());
        Assert.Equal(2, Open());

        // And all in one conversation, so the person can read it in one place.
        Assert.Equal(1, Count("SELECT count(DISTINCT conversation_id) FROM exchanges;"));
    }

    [Fact]
    public async Task One_agent_still_works_from_the_same_picker()
    {
        // The everybody row is added to the list rather than replacing it, and a regression here would be
        // invisible in the tests above.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], NodeCatalogue.Empty, null);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.StartTitle, Orders.Value, () => shell.Settled);

        Written(form, "just you");
        form.Finish(taken: true);

        await shell.Settled;

        Assert.Equal(1, Requests());
    }

    [Fact]
    public async Task No_seat_is_counted_among_the_agents()
    {
        // SC-007. A seat is somebody at a desk rather than something that can be started, and "all the
        // agents" addressed to a colleague is a different act from the one chosen.
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

        var rows = await PickerRowsAsync(driving, shell);

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

        var rows = await PickerRowsAsync(driving, shell);

        Assert.Equal(Participation.Everyone(1), rows[0]);
    }

    [Fact]
    public async Task A_project_holding_only_seats_offers_no_everybody_row()
    {
        // There are peers to speak to -- another person's seat is one -- so this is not the empty roster
        // case. There is simply nobody the everybody row would reach.
        //
        // The seat is on another machine deliberately. `Peers()` excludes every seat on *this* one, so a
        // colleague sharing this box is already absent from the picker, and a seat here would have produced
        // an empty picker and tested the wrong thing.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show(
            [Agent(AgentName.Create("jo@elsewhere"), adapter: Seat.Adapter)], NodeCatalogue.Empty, null);

        var rows = await PickerRowsAsync(driving, shell);

        Assert.DoesNotContain(rows, row => row.StartsWith("All agents", StringComparison.Ordinal));
        Assert.Contains("jo@elsewhere", rows);
    }

    [Fact]
    public async Task A_role_in_the_catalogue_and_a_registration_do_not_become_two_recipients()
    {
        // **The 009 shape, probed rather than assumed safe.** A registration is `role@machine` and a catalogue
        // key is a role. If the roster ever produced both spellings of one agent, everybody would address it
        // twice and the second would come back refused as a duplicate: reported as a refusal, from one act.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders), Agent(Billing)], Catalogue("orders"), null);

        var form = await Composing.WithAsync(
            driving, shell.Start, Participation.StartTitle, Participation.Everyone(2), () => shell.Settled);

        Written(form, "once each, please");
        form.Finish(taken: true);

        await shell.Settled;

        // Two agents, two Requests. Not three, and no duplicate reported.
        Assert.Equal(2, Requests());
        Assert.DoesNotContain("already sent exactly this", shell.Said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalogued_role_nothing_has_registered_is_still_offered()
    {
        // §12.3 routes spawn-on-demand through the catalogue, so a never-run-but-startable role is a
        // legitimate thing to send to — and leaving it out would omit exactly the agents somebody sends to
        // everybody in order to wake.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent(Orders)], Catalogue("inventory"), null);

        var rows = await PickerRowsAsync(driving, shell);

        Assert.Equal(Participation.Everyone(2), rows[0]);
    }

    /// <summary>The checkouts this machine has been told it can start, kept until the test ends.</summary>
    private readonly List<Catalogued> _machines = [];

    /// <summary>A catalogue naming some checkouts, written where they really live.</summary>
    /// <param name="roles">What each checkout answers to.</param>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// Through files rather than a constructed object, because there is no constructor that takes these --
    /// and building one for a test would be a second way to make a catalogue, which is how the reader and
    /// the writer come to disagree about the shape. **Two files since 018**: an entry is keyed by an
    /// identity and says nothing about names, so what this pane offers comes back out of the repository,
    /// and a catalogue whose checkouts are gone offers nothing at all.
    /// </remarks>
    private NodeCatalogue Catalogue(params string[] roles)
    {
        var machine = Catalogued.Offering(roles);

        _machines.Add(machine);

        return machine.Catalogue;
    }

    /// <summary>How many Requests reached the store.</summary>
    private int Requests() => Count("SELECT count(*) FROM messages WHERE type = 'request';");

    /// <summary>How many exchanges are open, which is what a Request actually costs a peer.</summary>
    /// <remarks>
    /// Counted beside the messages rather than instead of them. A Request stored with no open exchange behind
    /// it would be work nobody owes a Result on, which would make sending to everybody a different operation
    /// from N single sends.
    /// </remarks>
    private int Open() => Count("SELECT count(*) FROM exchanges WHERE state = 'OPEN';");

    private int Count(string sql)
    {
        using var command = _connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
