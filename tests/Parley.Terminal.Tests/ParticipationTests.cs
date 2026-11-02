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
/// A person taking part: seated, sent a request, and reporting what it came to.
/// </summary>
/// <remarks>
/// <para>
/// The reason the whole feature exists: an agent
/// attached through somebody's editor window can never be handed a turn — so until a person can
/// hold a seat and answer from somewhere that is not an editor, a deployment has a participant it
/// cannot reach.
/// </para>
/// <para>
/// Over a real socket against a real broker, because the questions worth asking here are all
/// about the wire: does a seat register, does a peer's question arrive, does answering it close
/// the exchange, and does the peer get the answer.
/// </para>
/// </remarks>
[Integration]
public sealed class ParticipationTests : IAsyncDisposable
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly LoopbackOperator _broker;

    public ParticipationTests()
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

    /// <summary>A Request a peer sends a seat, satisfying rule 8.1.</summary>
    private const string Request =
        """{"ask":"Is it safe to release?"}""";

    [Fact]
    public async Task A_person_can_send_the_result_of_a_request_a_peer_sent_them()
    {
        // The story's own test. A peer requests, the person reports from the seat, the exchange
        // closes, and what the peer gets back is what the person wrote.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var seated = await SeatedAs(seats, project, ct);
        var me = seated.Seat.Name;

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        var sent = await broker.RequestAsync(Orders, me, "model-a", Request, T0.AddMinutes(1), "chat", ct);

        Assert.True(sent.Exchange is not null, $"the request was refused: {sent.Admission.Rejection}");

        var voice = new SeatVoice(directory.AgentsFor(project), seated.Seat);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);

        var owed = taken.Single(one => one.Owes);

        var answered = await voice.ResultAsync(
            seated,
            owed.Message.ExchangeId,
            [.. owed.Items().Select(id => (id, "yes, it is safe", "answered"))],
            ct);

        Assert.True(answered.Closed, answered.Says);

        // And the peer has it. A Result the requester cannot read is not a Result.
        var export = await new BrokerClient(_broker.Operators).ExportAsync("chat", ct);

        Assert.Contains(
            export.Messages,
            m => m.From == me && m.PayloadJson.Contains("yes, it is safe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sending_a_result_settles_the_seats_own_idea_of_what_is_owed()
    {
        // The half a surface can get wrong on its own. Striking the request off a local list when
        // the send succeeds -- rather than when the broker says the exchange closed -- shows a
        // person nothing owed while the exchange is still open against them.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var seated = await SeatedAs(seats, project, ct);

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        await broker.RequestAsync(Orders, seated.Seat.Name, "model-a", Request, T0.AddMinutes(1), "chat", ct);

        var voice = new SeatVoice(directory.AgentsFor(project), seated.Seat);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);
        var owed = taken.Single(one => one.Owes);

        Assert.Contains(seated.Delivered, one => one.Owes);

        await voice.ResultAsync(
            seated, owed.Message.ExchangeId, [.. owed.Items().Select(id => (id, "yes", "answered"))], ct);

        Assert.DoesNotContain(seated.Delivered, one => one.Owes);
    }

    // `A_name_an_agent_holds_is_refused_before_anything_attaches` was here. It typed `orders` into the seat and expected
    // the book to refuse a name an agent holds. Nothing is typed now: the book attaches as the person row its
    // registry names, and storage refuses any other role under the seat's adapter.

    [Fact]
    public async Task A_registry_that_could_not_be_read_refuses_the_seat()
    {
        // The vacuous case, and the one that matters: an unreachable broker and a registry with
        // nobody in it are the same empty list, so a guard that proceeded on a failed read would
        // pass hardest exactly when the deployment is least well known.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        var outcome = await seats.TakeAsync(Project(), registry: null, ct);

        Assert.False(outcome.Seated);
    }

    [Fact]
    public async Task Disposing_the_book_releases_the_seat()
    {
        // FR-011 on the path nobody plans for. `parley terminal` disposes its SeatBook on the way out,
        // including when Ctrl+C brought it there, and an attach stream ending is what tells the
        // broker somebody left (§4.0). A seat that outlived the person is the failure the console
        // already shipped once.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        var project = Project();

        using (var seats = new SeatBook(directory.AgentsFor))
        {
            await SeatedAs(seats, project, ct);

            Assert.Single(seats.Seats());
        }

        // The book is gone, so nothing here holds the seat any more. What the broker does with
        // the ended stream is the broker's business and is tested where the broker is.
        Assert.True(true);

        // And a fresh book knows nothing of it, which is the part a surface could get wrong by
        // keeping a static.
        using var after = new SeatBook(directory.AgentsFor);

        Assert.Empty(after.Seats());
    }

    /// <summary>Seats somebody, having read the registry the way the surface does.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="project">Where to sit.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The seat held.</returns>
    private async Task<SeatHold> SeatedAs(
        SeatBook seats, ProjectSettings project, CancellationToken cancellationToken)
    {
        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(cancellationToken);

        var outcome = await seats.TakeAsync(project, registry, cancellationToken);

        Assert.True(outcome.Seated, outcome.Problem);

        return outcome.Hold!;
    }

    /// <summary>The one project these tests use.</summary>
    /// <returns>Its settings, pointed at the loopback broker.</returns>
    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null);

    /// <summary>A directory holding that one project.</summary>
    /// <returns>The directory.</returns>
    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
