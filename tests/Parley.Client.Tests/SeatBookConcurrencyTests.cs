using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// <see cref="SeatBook"/> under contention, counted by streams rather than by seats.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConcurrencyTests</c> probes <see cref="BrokerDirectory"/> and nothing had probed the book,
/// which is the half that holds state across requests: a dictionary written by a web request, a
/// terminal loop, a one-minute timer and a background pump at the same time.
/// </para>
/// <para>
/// <b>The invariant asserted here is not "the right number of seats" but "every stream opened was
/// closed exactly once".</b> A seat is an open connection (SPEC.md §4.0), so a hold the book drops
/// without disposing is not a tidiness problem — it is a person the deployment believes is present
/// with nothing left alive that could ever release them, and it would read as a correct seat count
/// from every angle except the broker's. Counting attaches against disposals is the only view that
/// sees it.
/// </para>
/// </remarks>
public sealed class SeatBookConcurrencyTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5403", null);

    /// <summary>How many threads pile on.</summary>
    private const int Crowd = 24;

    [Fact]
    public async Task A_storm_of_joins_for_one_person_leaves_exactly_one_attachment()
    {
        // Everybody racing through TakeAsync for the same key. One wins the TryAdd; every loser
        // has already opened a stream and has to close its own, or the broker holds attachments
        // for a person sitting in exactly one seat.
        var token = TestContext.Current.CancellationToken;
        var agents = new CountingAgents();
        using var book = new SeatBook(_ => agents);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, Crowd).Select(_ =>
                Task.Run(async () => await book.TakeAsync(Alpha, Person, token), token)));

        Assert.All(outcomes, outcome => Assert.True(outcome.Seated));
        Assert.Single(book.Seats());
        Assert.Equal(1, agents.Live);
    }

    [Fact]
    public async Task Taking_leaving_and_sweeping_at_once_closes_every_stream_it_opened()
    {
        // The three writers of this dictionary going at once: joins, departures, and a sweep
        // aggressive enough to take everything it sees. Whatever order they interleave in, a
        // stream opened here is a stream that has to end.
        var token = TestContext.Current.CancellationToken;
        var agents = new CountingAgents();
        var book = new SeatBook(_ => agents);

        // Twelve projects rather than twelve people: since specs/023 a book holds one person seat per project.
        var projects = Enumerable.Range(0, 12).Select(Project).ToArray();

        await Task.WhenAll(projects.Select(project => Task.Run(async () =>
        {
            for (var round = 0; round < 8; round++)
            {
                await book.TakeAsync(project, Registry(project), token);

                // An hour into the future, so it releases whatever it can see -- including seats
                // another thread took a microsecond ago. That is the point: the sweep is a timer
                // and does not wait its turn.
                book.Sweep(DateTimeOffset.UtcNow.AddHours(1));
            }
        }, token)));

        book.Dispose();

        Assert.Equal(0, agents.Live);
    }

    [Fact]
    public async Task Rejoining_a_lost_seat_from_several_threads_leaves_one_attachment()
    {
        // The path this branch added, under contention. A lost hold is removed and disposed by
        // whoever gets to it, while several threads are also trying to attach a fresh one -- so
        // the dead stream must be closed once, not none and not twice, and exactly one live
        // attachment must remain.
        var token = TestContext.Current.CancellationToken;
        var agents = new CountingAgents(refuseAfter: 1);
        using var book = new SeatBook(_ => agents);

        var first = await book.TakeAsync(Alpha, Person, token);

        Assert.True(first.Seated);

        await Eventually.UntilAsync(
            () => first.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

        Assert.Equal(0, agents.Live);

        agents.AllowMore();

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, Crowd).Select(_ =>
                Task.Run(async () => await book.TakeAsync(Alpha, Person, token), token)));

        // Some of the crowd legitimately lose: TakeAsync says so rather than handing back a hold
        // it did not manage to seat, and "try once more" is an honest answer to a race. What is
        // not allowed is a stream nobody closed.
        Assert.Contains(outcomes, outcome => outcome.Seated);
        Assert.Equal(book.Seats().Count, agents.Live);
        Assert.True(agents.Live <= 1, $"{agents.Live} attachments for one seat");
    }

    [Fact]
    public async Task Disposing_the_book_while_seats_are_being_taken_strands_nothing()
    {
        // Shutdown is the case the book already guards twice by hand -- once before the add and
        // once after -- because a hold added after the drain is a person nothing can release.
        // Raced rather than read.
        var token = TestContext.Current.CancellationToken;
        var agents = new CountingAgents();
        var book = new SeatBook(_ => agents);

        var joining = Enumerable.Range(0, Crowd).Select(index => Task.Run(async () =>
        {
            try
            {
                await book.TakeAsync(Project(index), Registry(Project(index)), token);
            }
            catch (ObjectDisposedException)
            {
                // The documented answer to joining a book that is shutting down.
            }
        }, token)).ToArray();

        book.Dispose();

        await Task.WhenAll(joining);

        // Disposal can have finished before a straggler attached, so the book is disposed again
        // to drain anything that landed after -- which is exactly what a second Dispose is for,
        // and what the guard inside TakeAsync is meant to make unnecessary.
        book.Dispose();

        Assert.Equal(0, agents.Live);
    }


    /// <summary>The person seat the broker writes for the one project most of these use (specs/023).</summary>
    private static IReadOnlyList<AgentRecord> Person => Registry(Alpha);

    /// <summary>One of the crowd of projects, by index.</summary>
    private static ProjectSettings Project(int index) =>
        new(ProjectName.Create($"project{(char)('a' + index)}"), string.Empty, Alpha.Endpoint, null);

    /// <summary>A project's registry, holding only its person seat.</summary>
    private static IReadOnlyList<AgentRecord> Registry(ProjectSettings project) =>
    [
        new AgentRecord(
            PersonSeat.On(project.Name), PersonSeat.Adapter, AgentTier.Pull, null, null, null, false, AgentStatus.Active),
    ];

    /// <summary>An Agents client that counts the streams it hands out and the ones given back.</summary>
    /// <param name="refuseAfter">
    /// How many attachments to grant before refusing, or <see langword="null"/> to grant them all.
    /// </param>
    private sealed class CountingAgents(int? refuseAfter = null) : Wire.Agents.AgentsClient
    {
        private int _attaches;
        private int _disposals;
        private int _allowance = refuseAfter ?? int.MaxValue;

        /// <summary>Streams opened and not yet closed.</summary>
        public int Live => Volatile.Read(ref _attaches) - Volatile.Read(ref _disposals);

        /// <summary>Permits a further round of attachments, as a broker coming back would.</summary>
        public void AllowMore() => Interlocked.Add(ref _allowance, int.MaxValue / 2);

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            if (Interlocked.Decrement(ref _allowance) < 0)
            {
                Interlocked.Increment(ref _allowance);

                throw new RpcException(new Status(StatusCode.PermissionDenied, "no."));
            }

            Interlocked.Increment(ref _attaches);

            return new(new Acknowledged(ending: refuseAfter is not null),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => Interlocked.Increment(ref _disposals));
        }
    }

    /// <summary>A stream that yields one attached event, then ends or waits forever.</summary>
    /// <param name="ending">Whether the stream finishes after the acknowledgement.</param>
    private sealed class Acknowledged(bool ending) : IAsyncStreamReader<Wire.Event>
    {
        private bool _sent;

        /// <inheritdoc/>
        public Wire.Event Current { get; private set; } = new();

        /// <inheritdoc/>
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                Current = new Wire.Event { Attached = new Wire.Attached() };

                return true;
            }

            if (ending)
            {
                return false;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return false;
        }
    }
}
