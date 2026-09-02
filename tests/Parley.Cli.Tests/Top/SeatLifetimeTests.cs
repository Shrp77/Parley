using Grpc.Core;
using Grpc.Net.Client;
using Parley.Cli.Top;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Spectre.Console.Testing;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// How long a seat lasts, and what keeps it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeatBook.IdleAfter"/> exists because a browser tab left open is not somebody at a
/// desk: §4.0 makes an open connection the evidence an agent is reachable, and a window nobody is
/// in front of manufactures that evidence. A terminal left open overnight makes exactly the same
/// false claim.
/// </para>
/// <para>
/// Which gives two failures pulling opposite ways, and both are probed here: a surface that never
/// swept would hold somebody present forever, and a surface that swept before saying "I am still
/// here" would unseat the person for the crime of thinking about a question.
/// </para>
/// </remarks>
public sealed class SeatLifetimeTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5501", null, null);

    private static readonly ProjectSettings Beta =
        new(ProjectName.Create("beta"), string.Empty, "http://127.0.0.1:5502", null, null);

    [Fact]
    public async Task Visiting_a_project_you_are_not_seated_in_does_not_forget_who_you_are()
    {
        // Seated in alpha, walk into beta, walk back. The menu asks the book whether there is a
        // seat *here* and the answer in beta is no -- but the person is still the person, and an
        // earlier version took the answer to that question as the answer to who they were. It
        // handed back nobody, so returning to alpha drew a join while the book still held the
        // seat and the registry still had them present.
        var ct = TestContext.Current.CancellationToken;
        using var book = new SeatBook(_ => new StubAgents());
        using var directory = new BrokerDirectory(
            [Alpha, Beta], _ => GrpcChannel.ForAddress(Alpha.Endpoint));

        var me = Seated("arvind");

        Assert.True((await book.TakeAsync(Alpha, me, [], ct)).Seated);

        using var console = new TestConsole();

        console.Interactive();
        console.Profile.Width = 200;

        Back(console);

        var carried = await ProjectMenu.ShowAsync(console, directory, book, Beta, me, ct);

        Assert.Equal(me, carried);
        Assert.Contains("(not seated)", console.Output, StringComparison.Ordinal);

        using var back = new TestConsole();

        back.Interactive();
        back.Profile.Width = 200;

        Back(back);

        await ProjectMenu.ShowAsync(back, directory, book, Alpha, carried, ct);

        Assert.Contains("(seated as arvind", back.Output, StringComparison.Ordinal);
    }

    /// <summary>Selects the last entry, whatever it is and however long the menu is.</summary>
    /// <param name="console">The console to push the keys onto.</param>
    private static void Back(TestConsole console)
    {
        for (var step = 0; step < 20; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }

    [Fact]
    public async Task Thinking_for_longer_than_the_idle_window_does_not_lose_the_seat()
    {
        // The defect this was written for. A person opens the inbox, reads a question, and writes
        // an answer -- the slowest thing this surface is for -- and comes back to the menu after
        // longer than the idle window. Swept before being touched, their seat is gone and the
        // menu silently offers them a join.
        using var book = new SeatBook(_ => new StubAgents());
        var me = Seated("arvind");
        // The book stamps a new seat with the real clock, so the window has to be measured from
        // it. An epoch-based instant here made the first version of this test pass by arithmetic
        // rather than by the fix -- the sweep saw a negative age and skipped everything.
        var start = DateTimeOffset.UtcNow;

        await book.TakeAsync(Alpha, me, [], TestContext.Current.CancellationToken);

        var after = ProjectMenu.Refresh(book, me, Alpha, start + SeatBook.IdleAfter.Add(TimeSpan.FromMinutes(5)));

        Assert.NotNull(after);
        Assert.Single(book.Seats(me));
    }

    [Fact]
    public async Task A_terminal_nobody_is_at_loses_its_seat()
    {
        // The other direction, and the reason sweeping happens at all. Nobody is at this menu --
        // Refresh is called for a different person, so the first seat is never touched -- and the
        // idle window passes.
        using var book = new SeatBook(_ => new StubAgents());
        var gone = Seated("arvind");
        var here = Seated("sam");
        var start = DateTimeOffset.UtcNow;

        await book.TakeAsync(Alpha, gone, [], TestContext.Current.CancellationToken);
        await book.TakeAsync(Alpha, here, [], TestContext.Current.CancellationToken);

        var stillHere = ProjectMenu.Refresh(
            book, here, Alpha, start + SeatBook.IdleAfter.Add(TimeSpan.FromMinutes(5)));

        Assert.NotNull(stillHere);
        Assert.Empty(book.Seats(gone));
    }

    [Fact]
    public async Task A_swept_seat_is_reported_as_gone_rather_than_kept_on_screen()
    {
        // The cookie-outliving-a-seat failure, in its terminal form. Refresh must return what the
        // book holds after the sweep, not what it held before -- a menu still offering an inbox
        // to somebody who has been released is the console's shipped bug with a new frame.
        using var book = new SeatBook(_ => new StubAgents());
        var me = Seated("arvind");

        await book.TakeAsync(Alpha, me, [], TestContext.Current.CancellationToken);

        // Released by hand, as an idle sweep in some other loop would.
        Assert.Equal(1, book.Leave(me));

        Assert.Null(ProjectMenu.Refresh(book, me, Alpha, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Nobody_seated_is_not_an_error()
    {
        using var book = new SeatBook(_ => new StubAgents());

        Assert.Null(ProjectMenu.Refresh(book, null, Alpha, DateTimeOffset.UnixEpoch));
    }

    /// <summary>A person's seat, by the name they typed.</summary>
    /// <param name="person">What they typed.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
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
