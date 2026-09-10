using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;

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

    [Fact]
    public async Task A_refused_reattachment_ends_the_seat()
    {
        var token = TestContext.Current.CancellationToken;
        using var book = new SeatBook(_ => new SeatDoubles.Stubborn(StatusCode.PermissionDenied));
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => taken.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

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
        var flaky = new SeatDoubles.Stubborn(StatusCode.Unavailable);
        using var book = new SeatBook(_ => flaky);
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => flaky.Refusals > 0, "the reattach was never attempted", token);

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
        var agents = new SeatDoubles.Stubborn(StatusCode.PermissionDenied);
        using var book = new SeatBook(_ => agents);
        var seat = Seated("arvind");

        var first = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(first.Seated);

        await Eventually.UntilAsync(
            () => first.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

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
        using var book = new SeatBook(_ => new SeatDoubles.Stubborn(StatusCode.PermissionDenied));
        var seat = Seated("arvind");

        var taken = await book.TakeAsync(Alpha, seat, [], token);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => taken.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

        Assert.Equal(1, book.Sweep(taken.Hold!.Since));
    }


    /// <summary>A person's seat, by the name they typed.</summary>
    /// <param name="person">What they typed.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
    }
}
