using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Terminal.Tests;

/// <summary>
/// How long the person is marked here, and what keeps them so.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeatBook.IdleAfter"/> exists because a window nobody is in front of is not somebody at a desk: §4.0 makes
/// an open connection the evidence of presence, and a terminal left open overnight manufactures that evidence.
/// </para>
/// <para>
/// Two failures pulling opposite ways, and both are probed here: a surface that never swept would mark somebody here
/// for ever, and one that swept before saying "I am still here" would mark the person away for the crime of thinking
/// about a question.
/// </para>
/// <para>
/// <b>Rewritten for specs/023 against the book directly.</b> These went through <c>AgentsRegion.Refresh</c> and a seat
/// per typed name; both went. What a sweep does changed too: it drops the stream and the person reads as away, but the
/// seat is the broker's and is remembered here (<see cref="SeatBook.Known"/>), so the waiting count survives and the
/// next key takes the stream back.
/// </para>
/// </remarks>
public sealed class SeatLifetimeTests
{
    private static readonly ProjectSettings Alpha =
        new(Walking.Alpha, string.Empty, "http://127.0.0.1:5501", null);

    private static readonly ProjectSettings Beta =
        new(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:5502", null);

    [Fact]
    public async Task Being_held_in_one_project_says_nothing_about_another()
    {
        // The seat is asked for per project. An earlier version took "no seat here" as the answer to who the person was,
        // so returning to a project drew a join while the book still held the seat there.
        var ct = TestContext.Current.CancellationToken;
        using var book = new SeatBook(_ => new StubAgents());

        Assert.True((await book.TakeAsync(Alpha, Walking.PersonOnly, ct)).Seated);

        Assert.Null(book.Held(Beta.Name.Value));
        Assert.NotNull(book.Held(Alpha.Name.Value));
    }

    [Fact]
    public async Task Thinking_for_longer_than_the_idle_window_keeps_the_person_here()
    {
        // The defect this was written for: touched after being swept, the person reading a question is marked away. The
        // shell touches then sweeps on every draw, which this reproduces.
        using var book = new SeatBook(_ => new StubAgents());
        var start = DateTimeOffset.UtcNow;

        await book.TakeAsync(Alpha, Walking.PersonOnly, TestContext.Current.CancellationToken);

        var later = start + SeatBook.IdleAfter.Add(TimeSpan.FromMinutes(5));

        book.Touch(later);
        book.Sweep(later);

        Assert.NotNull(book.Held(Alpha.Name.Value));
    }

    [Fact]
    public async Task Nobody_at_the_terminal_drops_the_stream_and_the_seat_is_still_known()
    {
        // The other direction, and the reason sweeping happens at all — with specs/023's half: the seat is not forgotten,
        // because the broker never releases it and the count of what is waiting has to go on working while away.
        using var book = new SeatBook(_ => new StubAgents());
        var start = DateTimeOffset.UtcNow;

        var taken = await book.TakeAsync(Alpha, Walking.PersonOnly, TestContext.Current.CancellationToken);

        Assert.Equal(1, book.Sweep(start + SeatBook.IdleAfter.Add(TimeSpan.FromMinutes(5))));

        Assert.Null(book.Held(Alpha.Name.Value));
        Assert.Empty(book.Seats());
        Assert.Equal(taken.Hold!.Seat, book.Known(Alpha.Name.Value));
    }

    [Fact]
    public void An_empty_book_holds_nobody_and_knows_nobody()
    {
        using var book = new SeatBook(_ => new StubAgents());

        Assert.Null(book.Held(Alpha.Name.Value));
        Assert.Null(book.Known(Alpha.Name.Value));
    }

    /// <summary>An Agents client whose attach never ends.</summary>
    private sealed class StubAgents : Wire.Agents.AgentsClient
    {
        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options) =>
            new(new Acknowledged(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }

    /// <summary>A stream that yields one attached event and then waits forever.</summary>
    private sealed class Acknowledged : IAsyncStreamReader<Wire.Event>
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

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return false;
        }
    }
}
