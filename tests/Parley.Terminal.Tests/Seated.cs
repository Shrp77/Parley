using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Parley.Store;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// A driven surface, seated, looking at a conversation somebody has already said something in.
/// </summary>
/// <remarks>
/// <para>
/// <b>One fixture, because there were two and about to be four.</b> Every driven test of the room needs the
/// same seven steps — a store, a registry, a loopback broker, a shell, a seat taken through the real dialog,
/// a peer's Request, and a walk into the conversation — and a copy per file is how two of them come to seat
/// somebody differently and disagree about what the room should show.
/// </para>
/// <para>
/// <b>Seeded from a peer, because membership is derived</b> (SPEC.md §5.0): a conversation with no traffic has
/// no participants at all, so an unseeded one would test a room with nobody in it. A person opens a
/// conversation that exists; this is that.
/// </para>
/// </remarks>
internal sealed class Seated : IAsyncDisposable
{
    /// <summary>The conversation the fixture opens.</summary>
    internal const string Conversation = "room";

    /// <summary>Who the person is: the role of the seat the broker provides (specs/023).</summary>
    /// <remarks>
    /// It was <c>arvind</c>, typed into the Join dialog. There is no dialog now and no name to type: the broker writes
    /// <c>human-user@&lt;project&gt;</c> and the shell attaches as it when the project opens.
    /// </remarks>
    internal const string Me = PersonSeat.Role;

    /// <summary>A peer that is in the conversation and waiting on this person.</summary>
    internal static AgentName Orders { get; } = AgentName.Create("orders@test");

    /// <summary>A peer that is in the project and not in the conversation.</summary>
    internal static AgentName Billing { get; } = AgentName.Create("billing@test");

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;
    private readonly BrokerDirectory _directory;
    private readonly SeatBook _seats;

    private Seated(
        SqliteConnection connection,
        LoopbackOperator broker,
        BrokerDirectory directory,
        SeatBook seats,
        Driving.Session driving,
        Shell shell)
    {
        _connection = connection;
        _broker = broker;
        _directory = directory;
        _seats = seats;

        Session = driving;
        Shell = shell;
    }

    /// <summary>The surface, seated and looking at the conversation.</summary>
    internal Shell Shell { get; }

    /// <summary>The driven session, for laying out and reading the screen.</summary>
    /// <remarks>
    /// Named for what it is rather than for the class that opens it: a property called <c>Driving</c> shadows
    /// the static <c>Driving</c> inside this type, so its own helpers could no longer reach <c>Driving.Open</c>.
    /// </remarks>
    internal Driving.Session Session { get; }

    /// <summary>The operator endpoint, for asking what the broker actually recorded.</summary>
    internal string Address => _broker.Address;

    /// <summary>The operator client, for asking what the broker actually recorded.</summary>
    internal BrokerClient Operators => new(_broker.Operators);

    /// <summary>Opens one.</summary>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <param name="width">How wide the terminal is.</param>
    /// <param name="height">How tall the terminal is.</param>
    /// <param name="addressing">Where who-each-line-goes-to is kept, or null for this run only.</param>
    /// <param name="mailEvery">How often the waiting count is asked for, or null (the default) not to poll at all.</param>
    /// <param name="followEvery">How often the open conversation is read again, or null (the default) not to follow.</param>
    /// <returns>The fixture.</returns>
    internal static async Task<Seated> OpenAsync(
        CancellationToken cancellationToken,
        int width = 140,
        int height = 40,
        AddressBook? addressing = null,
        TimeSpan? mailEvery = null,
        TimeSpan? followEvery = null)
    {
        var connection = ParleyDatabase.OpenInMemory();
        var agents = new AgentRepository(connection);

        foreach (var agent in new[] { Orders, Billing })
        {
            await agents.UpsertAsync(
                new AgentRecord(agent, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
                cancellationToken).ConfigureAwait(false);
        }

        var broker = LoopbackOperator.Start(connection);
        var directory = new BrokerDirectory(
            [new ProjectSettings(Walking.Alpha, "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

        var seats = new SeatBook(directory.AgentsFor);
        var driving = Driving.Open(width, height);

        // The mail poll and the follow are off: both queue reads on their own schedule, which would make
        // `Settled` mean "nothing is in flight just now" rather than "the screen is true".
        var shell = new Shell(directory)
        {
            Seats = seats, MailEvery = mailEvery, FollowEvery = null, Addressing = addressing ?? new AddressBook(null),
        };

        driving.Show(shell);

        // After `Show`, which turns following off on every driven surface; a test that asks for it gets it back.
        shell.FollowEvery = followEvery;

        await shell.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await shell.Settled.ConfigureAwait(false);

        await Walking.GoAsync(shell, Viewing.Project).ConfigureAwait(false);
        await shell.Settled.ConfigureAwait(false);

        // **Seated by opening the project, with no key pressed** (specs/023): the broker provides the person seat and
        // the shell takes it. This fixture typed a name into a Join dialog until then; that the seat is here without one
        // is itself the property FR-014 asks for.
        Assert.NotNull(shell.Held);
        Assert.True(PersonSeat.Is(shell.Held!.Seat.Name), $"seated as '{shell.Held.Seat.Name}'");

        var speaking = new Broker.Broker(connection, Parley.Broker.BrokerOptions.Default);

        await speaking.StartAsync(Orders, T0, cancellationToken).ConfigureAwait(false);

        var sent = await speaking.RequestAsync(
            Orders,
            shell.Held!.Seat.Name,
            "model-a",
            """{"ask":"is the build green?"}""",
            T0.AddMinutes(1),
            Conversation,
            cancellationToken).ConfigureAwait(false);

        Assert.True(sent.Exchange is not null, $"the fixture's request was refused: {sent.Admission.Rejection}");

        await Walking.GoAsync(shell, Viewing.Conversation, Conversation).ConfigureAwait(false);
        await shell.Settled.ConfigureAwait(false);

        return new Seated(connection, broker, directory, seats, driving, shell);
    }

    /// <summary>Lays the screen out, which is what makes a frame true.</summary>
    /// <remarks>
    /// The harness never pumps the loop, so a frame read without this is whatever was last drawn — which for
    /// a region sized from the space left over is nothing at all.
    /// </remarks>
    internal void Lay() => Session.App.LayoutAndDraw(true);

    /// <summary>Has the person ask an agent something in a conversation, and goes to that conversation.</summary>
    /// <param name="to">Who is asked, which makes them a participant.</param>
    /// <param name="conversation">Where; a new name starts one.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <param name="go">Whether to go to the conversation afterwards, or leave the screen to find out by itself.</param>
    /// <returns>When the request is stored and the screen agrees.</returns>
    /// <remarks>
    /// The person rather than the agent, because that is how an agent joins a conversation: a person may name any
    /// conversation (principle 2.10), and an agent naming one it is not in is refused as not assignable.
    /// </remarks>
    internal async Task AskAsync(AgentName to, string conversation, CancellationToken cancellationToken, bool go = true)
    {
        var speaking = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);

        await speaking.StartAsync(to, T0, cancellationToken).ConfigureAwait(false);

        var sent = await speaking.RequestAsync(
            Shell.Held!.Seat.Name,
            to,
            "human",
            $$"""{"ask":"which of you should I talk to in {{conversation}}?"}""",
            T0.AddMinutes(2),
            conversation,
            cancellationToken).ConfigureAwait(false);

        Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");

        if (go)
        {
            await Walking.GoAsync(Shell, Viewing.Conversation, conversation).ConfigureAwait(false);
            await Shell.Settled.ConfigureAwait(false);
        }
    }

    /// <summary>Puts the cursor in a pane.</summary>
    /// <typeparam name="T">The region to focus.</typeparam>
    internal void Focus<T>()
        where T : FrameView
    {
        Shell.Focus(Shell.Panes.ToList().FindIndex(pane => pane is T));

        Assert.IsType<T>(Shell.Panes[Shell.FocusedRegion]);
    }

    /// <summary>Puts the cursor on the first row of a list whose text contains something.</summary>
    /// <param name="list">The list.</param>
    /// <param name="saying">What the row says.</param>
    internal void Row(GuardedList list, string saying) => Session.Row(list, saying);

    public async ValueTask DisposeAsync()
    {
        Shell.Dispose();
        Session.Dispose();
        _seats.Dispose();
        _directory.Dispose();

        await _broker.DisposeAsync().ConfigureAwait(false);

        _connection.Dispose();
    }
}
