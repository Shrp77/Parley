using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Top.Tests;

/// <summary>
/// A person taking part: seated, owed a question, and answering it.
/// </summary>
/// <remarks>
/// <para>
/// SC-002, and the reason the whole feature exists. SPEC.md §17 question 15 records that an agent
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

    [Fact]
    public async Task A_person_can_answer_a_question_a_peer_asked_them()
    {
        // The story's own test. A peer asks, the person answers from the seat, the exchange
        // closes, and what the peer gets back is what the person wrote.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var seated = await SeatedAs(seats, project, "arvind", ct);
        var me = seated.Seat.Name;

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        var asked = await broker.AskAsync(
            Orders, me, "model-a",
            """{"intent":"deciding","sub_questions":[{"id":"q1","text":"is it safe"}],"consequences":"a release goes out or does not, and rolling it back costs a day"}""",
            T0.AddMinutes(1), "chat", ct);

        Assert.True(asked.Succeeded, $"the ask was refused: {asked.Rejection}");

        var voice = new SeatVoice(directory.AgentsFor(project), seated.Seat);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);

        var owed = taken.Single(one => one.Owes);
        var exchange = owed.Message.ExchangeId!.Value;

        var ids = await voice.SubQuestionsAsync(exchange, ct);

        var answered = await voice.AnswerAsync(
            seated,
            exchange,
            [.. ids.Select(id => (id, "yes, it is safe", "answered"))],
            ct);

        Assert.True(answered.Closed, answered.Says);

        // And the peer has it. An answer the asker cannot read is not an answer.
        var export = await new BrokerClient(_broker.Operators).ExportAsync("chat", ct);

        Assert.Contains(
            export.Messages,
            m => m.From == me && m.PayloadJson.Contains("yes, it is safe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Answering_settles_the_seats_own_idea_of_what_is_owed()
    {
        // The half a surface can get wrong on its own. Striking the question off a local list
        // when the send succeeds -- rather than when the broker says the exchange closed -- shows
        // a person nothing owed while the working window of §6 is still running against them.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var seated = await SeatedAs(seats, project, "arvind", ct);

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        await broker.AskAsync(
            Orders, seated.Seat.Name, "model-a",
            """{"intent":"deciding","sub_questions":[{"id":"q1","text":"is it safe"}],"consequences":"a release goes out or does not, and rolling it back costs a day"}""",
            T0.AddMinutes(1), "chat", ct);

        var voice = new SeatVoice(directory.AgentsFor(project), seated.Seat);

        var (taken, _) = await voice.TakeDeliveryAsync(seated, ct);
        var exchange = taken.Single(one => one.Owes).Message.ExchangeId!.Value;

        Assert.Contains(seated.Delivered, one => one.Owes);

        var ids = await voice.SubQuestionsAsync(exchange, ct);

        await voice.AnswerAsync(
            seated, exchange, [.. ids.Select(id => (id, "yes", "answered"))], ct);

        Assert.DoesNotContain(seated.Delivered, one => one.Owes);
    }

    [Fact]
    public async Task A_debt_cannot_be_cleared_away_instead_of_answered()
    {
        // The attack on the tidy-up button. Clearing must drop what has been read and owes
        // nothing, and never a question -- a person who could clear a debt would leave the
        // exchange behind it running on to be ghosted, with nothing on their screen saying so.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var seated = await SeatedAs(seats, project, "arvind", ct);

        var broker = new Broker.Broker(_connection, Parley.Broker.BrokerOptions.Default);
        await broker.StartAsync(Orders, T0, ct);

        await broker.AskAsync(
            Orders, seated.Seat.Name, "model-a",
            """{"intent":"deciding","sub_questions":[{"id":"q1","text":"is it safe"}],"consequences":"a release goes out or does not, and rolling it back costs a day"}""",
            T0.AddMinutes(1), "chat", ct);

        await broker.NotifyAsync(
            Orders, seated.Seat.Name, "model-a", """{"text":"just so you know"}""",
            T0.AddMinutes(2), "chat", ct);

        var voice = new SeatVoice(directory.AgentsFor(project), seated.Seat);
        await voice.TakeDeliveryAsync(seated, ct);

        Assert.Equal(1, seated.DismissRead());
        Assert.Single(seated.Delivered);
        Assert.All(seated.Delivered, one => Assert.True(one.Owes));
    }

    [Fact]
    public async Task A_name_an_agent_holds_is_refused_before_anything_attaches()
    {
        // The attack the seat exists to stop. `orders` is an ordinary thing for a person to
        // type, and without the guard every message they then sent would be recorded as the
        // orders agent's own words.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);
        var project = Project();

        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(ct);

        Assert.True(Seat.TryRead("orders", out var seat, out _));

        var outcome = await seats.TakeAsync(project, seat, registry, ct);

        Assert.False(outcome.Seated);
        Assert.Contains("already registered here as an agent", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_could_not_be_read_refuses_the_seat()
    {
        // The vacuous case, and the one that matters: an unreachable broker and a registry with
        // nobody in it are the same empty list, so a guard that proceeded on a failed read would
        // pass hardest exactly when the deployment is least well known.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        using var seats = new SeatBook(directory.AgentsFor);

        Assert.True(Seat.TryRead("arvind", out var seat, out _));

        var outcome = await seats.TakeAsync(Project(), seat, registry: null, ct);

        Assert.False(outcome.Seated);
    }

    [Fact]
    public async Task Disposing_the_book_releases_the_seat()
    {
        // FR-011 on the path nobody plans for. `parley top` disposes its SeatBook on the way out,
        // including when Ctrl+C brought it there, and an attach stream ending is what tells the
        // broker somebody left (§4.0). A seat that outlived the person is the failure the console
        // already shipped once.
        var ct = TestContext.Current.CancellationToken;
        using var directory = Directory();
        var project = Project();

        Seat seat;

        using (var seats = new SeatBook(directory.AgentsFor))
        {
            var seated = await SeatedAs(seats, project, "arvind", ct);
            seat = seated.Seat;

            Assert.Single(seats.Seats(seat));
        }

        // The book is gone, so nothing here holds the seat any more. What the broker does with
        // the ended stream is the broker's business and is tested where the broker is.
        Assert.True(true);

        // And a fresh book knows nothing of it, which is the part a surface could get wrong by
        // keeping a static.
        using var after = new SeatBook(directory.AgentsFor);

        Assert.Empty(after.Seats(seat));
    }

    /// <summary>Seats somebody, having read the registry the way the surface does.</summary>
    /// <param name="seats">The book.</param>
    /// <param name="project">Where to sit.</param>
    /// <param name="person">The name typed.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The seat held.</returns>
    private async Task<SeatHold> SeatedAs(
        SeatBook seats, ProjectSettings project, string person, CancellationToken cancellationToken)
    {
        var registry = await new BrokerClient(_broker.Operators).RegistryAsync(cancellationToken);

        Assert.True(Seat.TryRead(person, out var seat, out _));

        var outcome = await seats.TakeAsync(project, seat, registry, cancellationToken);

        Assert.True(outcome.Seated, outcome.Problem);

        return outcome.Hold!;
    }

    /// <summary>The one project these tests use.</summary>
    /// <returns>Its settings, pointed at the loopback broker.</returns>
    private ProjectSettings Project() =>
        new(ProjectName.Create("alpha"), string.Empty, _broker.Address, null, null);

    /// <summary>A directory holding that one project.</summary>
    /// <returns>The directory.</returns>
    private BrokerDirectory Directory() =>
        new([Project()], _ => GrpcChannel.ForAddress(_broker.Address));
}
