using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// A seat whose attachment has ended for good, and what the book owes about it.
/// </summary>
/// <remarks>
/// <para>
/// SPEC.md §4.0 makes an open connection the evidence an agent is reachable. A hold whose stream
/// has ended and whose reattachment is refused has no such evidence and will never get it back —
/// so a book that went on handing it out would be manufacturing exactly the presence §14 was
/// rewritten to stop manufacturing, and both surfaces would draw somebody as present in a
/// deployment that had never heard of them.
/// </para>
/// <para>
/// Two things pull the other way and both are kept. A broker that is merely <i>down</i> is
/// retried indefinitely, because a restart should give a person their seat back and nobody
/// restarts a person; that is <see cref="BrokerDirectory"/>'s neighbourhood rather than this
/// file's, and it is the reason this hinges on the refusal's status rather than on the stream
/// ending. And the idle sweep is unchanged: this is not an aged-out seat, it is one with nothing
/// behind it, which is why it goes without waiting out the window.
/// </para>
/// <para>
/// Probed rather than read. The rejoin case below is the one inspection would miss: the guard
/// works everywhere a surface asks whether somebody is seated, and the door it did not cover was
/// the one marked <c>TakeAsync</c>.
/// </para>
/// </remarks>
public sealed class LostSeatTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5402", null, null);

    /// <summary>How long to allow for a reattach that waits <c>SeatHold.RetryAfter</c> first.</summary>
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_refused_reattachment_ends_the_seat()
    {
        var token = TestContext.Current.CancellationToken;
        using var book = new SeatBook(_ => new Stubborn(StatusCode.PermissionDenied));
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await UntilAsync(() => taken.Hold!.Lost, token);

        Assert.Null(book.Held(Alpha.Name.Value, seat));
        Assert.Empty(book.Seats(seat));
    }

    [Fact]
    public async Task A_broker_that_is_only_down_keeps_the_seat()
    {
        // The other side of the same switch, and the reason this is decided on the status rather
        // than on the stream ending: Unavailable is a broker that may be coming back, and a
        // person unseated by a restart is worse off than one who waited through it.
        var token = TestContext.Current.CancellationToken;
        var flaky = new Stubborn(StatusCode.Unavailable);
        using var book = new SeatBook(_ => flaky);
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await UntilAsync(() => flaky.Refusals > 0, token);

        Assert.False(taken.Hold!.Lost);
        Assert.NotNull(book.Held(Alpha.Name.Value, seat));
    }

    [Fact]
    public async Task Rejoining_after_a_lost_seat_attaches_again_rather_than_handing_back_the_dead_one()
    {
        // The door the first version of this guard did not cover. TakeAsync returns an existing
        // hold under the same key without looking at it, so somebody rejoining after their
        // attachment ended was told they were already seated -- and handed the dead hold, whose
        // pump had stopped. The surface would then draw them as present forever, through the one
        // path they took in order to fix it.
        var token = TestContext.Current.CancellationToken;
        var agents = new Stubborn(StatusCode.PermissionDenied);
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");

        var first = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(first.Seated);

        await UntilAsync(() => first.Hold!.Lost, token);

        // The broker comes back. Nothing is racing for this: a hold that has given up has
        // stopped retrying, which is what made it lost in the first place.
        agents.Allow();

        var again = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(again.Seated);
        Assert.NotSame(first.Hold, again.Hold);
        Assert.Equal(2, agents.Grants);
    }

    [Fact]
    public async Task A_lost_seat_is_swept_without_waiting_out_the_idle_window()
    {
        // Not an aged-out seat: one with nothing behind it. Waiting twenty minutes to reclaim the
        // entry would buy nothing, since every reader already treats it as gone.
        var token = TestContext.Current.CancellationToken;
        using var book = new SeatBook(_ => new Stubborn(StatusCode.PermissionDenied));
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await UntilAsync(() => taken.Hold!.Lost, token);

        Assert.Equal(1, book.Sweep(taken.Hold!.Since));
    }

    /// <summary>Waits for something a background pump is going to do, or fails saying it did not.</summary>
    /// <param name="done">What is being waited for.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>When it has happened.</returns>
    private static async Task UntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        var giveUp = DateTimeOffset.UtcNow + Eventually;

        while (!done())
        {
            Assert.True(DateTimeOffset.UtcNow < giveUp, "the reattach never resolved");

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>A person's seat, by the name they typed.</summary>
    /// <param name="person">What they typed.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
    }

    /// <summary>
    /// An Agents client that grants a fixed number of attachments, ends each, and refuses the rest.
    /// </summary>
    /// <param name="refuseWith">What to refuse attachments beyond its allowance with.</param>
    /// <remarks>
    /// A real broker cannot be told to acknowledge an attach and then refuse the next, and the
    /// window between a broker going away and its seat noticing is not something a test should
    /// race. This is that state, held still.
    /// </remarks>
    private sealed class Stubborn(StatusCode refuseWith) : Wire.Agents.AgentsClient
    {
        private int _allowance = 1;
        private int _grants;
        private int _refusals;

        /// <summary>How many attachments have been acknowledged.</summary>
        public int Grants => Volatile.Read(ref _grants);

        /// <summary>How many attachments have been turned down.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        /// <summary>Permits one more attachment, as a broker coming back would.</summary>
        public void Allow() => Interlocked.Increment(ref _allowance);

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            if (Interlocked.Decrement(ref _allowance) < 0)
            {
                Interlocked.Increment(ref _allowance);
                Interlocked.Increment(ref _refusals);

                throw new RpcException(new Status(refuseWith, "no."));
            }

            Interlocked.Increment(ref _grants);

            // Acknowledged and then finished: a broker that went away with the stream open,
            // rather than one that was never there.
            return new(new Acknowledged(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }

    /// <summary>A stream that yields one attached event and then ends.</summary>
    private sealed class Acknowledged : IAsyncStreamReader<Wire.Event>
    {
        private bool _sent;

        /// <inheritdoc/>
        public Wire.Event Current { get; private set; } = new();

        /// <inheritdoc/>
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_sent)
            {
                return Task.FromResult(false);
            }

            _sent = true;
            Current = new Wire.Event { Attached = new Wire.Attached() };

            return Task.FromResult(true);
        }
    }
}
