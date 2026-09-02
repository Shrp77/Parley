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
/// What happens when the broker goes away underneath somebody who is seated.
/// </summary>
/// <remarks>
/// <para>
/// The spec names this as an edge case: <i>the broker restarts underneath a running session; the
/// seat is gone with it, and the surface must say the person is no longer seated rather than
/// continue showing them as present.</i>
/// </para>
/// <para>
/// <b>Probing it split the case in two, and the two want opposite things.</b> A broker that is
/// down and coming back must keep the seat — <see cref="SeatHold"/> retries for exactly that
/// reason, because nobody restarts a person. A broker that <i>refuses</i> the reattach is never
/// granting it, and from that moment the hold is a seat with no attachment behind it. Which of
/// those a status means is <c>LostSeatTests</c>'s question, in the assembly the book lives in;
/// what is here is the half that is this surface's own — <b>what a person sees</b> once the book
/// has decided.
/// </para>
/// <para>
/// The second case is narrower and was not handled at all: an agent operation that throws takes
/// the process with it. Every operator read here goes through
/// <see cref="BrokerDirectory.ReadAsync"/> and comes back as a sentence; the operations behind a
/// seat go nowhere near it. In the console an exception is a 500 page, ugly and contained. In a
/// terminal it is the end of the session, and every other project's view with it.
/// </para>
/// </remarks>
public sealed class BrokerRestartTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5601", null, null);

    [Fact]
    public async Task A_seat_that_is_over_is_drawn_as_no_seat()
    {
        // The same fact, one layer up: what a person sees. A menu still offering an inbox to
        // somebody whose attachment ended is the cookie-outliving-a-seat failure with a new
        // frame, and the menu asks the book rather than trusting the name it was handed.
        var ct = TestContext.Current.CancellationToken;
        var refusing = new Stubborn(StatusCode.PermissionDenied);

        using var directory = new BrokerDirectory(
            [Alpha], _ => GrpcChannel.ForAddress(Alpha.Endpoint));

        using var seats = new SeatBook(_ => refusing);

        Assert.True(Seat.TryRead("arvind", out var me, out _));
        var taken = await seats.TakeAsync(Alpha, me, [], ct);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => taken.Hold!.Lost, "the seat never gave up on a broker refusing it", ct);

        using var console = Interactive();

        Leave(console);

        await ProjectMenu.ShowAsync(console, directory, seats, Alpha, me, ct);

        Assert.Contains("(not seated)", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_operation_that_fails_does_not_end_the_session()
    {
        // The containment that did not exist. A seat is held, and the operation behind the inbox
        // refuses -- which in a terminal, uncaught, is the process going away and taking every
        // other project's view with it.
        var ct = TestContext.Current.CancellationToken;
        var refusing = new RefusingAgents();

        using var directory = new BrokerDirectory(
            [Alpha], _ => GrpcChannel.ForAddress(Alpha.Endpoint));

        using var seats = new SeatBook(_ => refusing);

        Assert.True(Seat.TryRead("arvind", out var me, out _));
        Assert.True((await seats.TakeAsync(Alpha, me, [], ct)).Seated);

        using var console = Interactive();

        Pick(console, ProjectMenu.Choices(seated: true), Inbox);
        Leave(console);

        await ProjectMenu.ShowAsync(console, directory, seats, Alpha, me, ct);

        // Said, not thrown. And the seat is left alone: this screen's last exception is not what
        // decides whether an attachment survived, and the hold behind this one is still open.
        Assert.Contains("did not answer", console.Output, StringComparison.Ordinal);
        Assert.Single(seats.Seats(me));
    }


    /// <summary>A console that answers prompts from pushed keys.</summary>
    /// <returns>The console.</returns>
    private static TestConsole Interactive()
    {
        var console = new TestConsole();

        console.Interactive();
        console.Profile.Width = 200;

        return console;
    }

    /// <summary>Chooses one entry by name, whatever position it is in.</summary>
    /// <param name="console">The console to push the keys onto.</param>
    /// <param name="choices">The entries the screen is about to offer.</param>
    /// <param name="wanted">The one to take.</param>
    private static void Pick(TestConsole console, IReadOnlyList<string> choices, string wanted)
    {
        var at = choices.ToList().IndexOf(wanted);

        Assert.True(at >= 0, $"'{wanted}' is not on the screen it was expected on");

        for (var step = 0; step < at; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>Selects the last entry, whatever it is and however long the menu is.</summary>
    /// <param name="console">The console to push the keys onto.</param>
    /// <remarks>
    /// By overshooting rather than by counting. "Back to projects" is last on either form of the
    /// menu and a highlight does not run off the end, so this lands on it whichever menu is drawn
    /// — which matters here because which menu is drawn is the thing under test.
    /// </remarks>
    private static void Leave(TestConsole console)
    {
        for (var step = 0; step < 20; step++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>The menu entry that reaches the broker as the seated person.</summary>
    private const string Inbox = "What is waiting for you";

    /// <summary>
    /// An Agents client that grants one attachment, ends it, and refuses every one after.
    /// </summary>
    /// <remarks>
    /// A real broker cannot be made to accept an attach and then refuse the next on demand, and
    /// the window between a broker dying and its seat noticing is not something a test should
    /// race. This is that state, held still, with the refusal's status chosen by the caller —
    /// because the status is the whole of what separates "wait for it" from "it is over".
    /// </remarks>
    /// <param name="refuseWith">What to refuse reattachment with.</param>
    private sealed class Stubborn(StatusCode refuseWith) : Wire.Agents.AgentsClient
    {
        private int _granted;

        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options)
        {
            if (Interlocked.Exchange(ref _granted, 1) == 1)
            {
                throw new RpcException(new Status(refuseWith, "no."));
            }

            // Acknowledged and then finished, which is a broker that went away with the stream
            // open rather than one that was never there.
            return new(new Acknowledged(ending: true),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }

    /// <summary>An Agents client that holds a seat and refuses everything else.</summary>
    private sealed class RefusingAgents : Wire.Agents.AgentsClient
    {
        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options) =>
            new(new Acknowledged(ending: false),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        /// <inheritdoc/>
        public override AsyncUnaryCall<Wire.Notice> WaitingAsync(
            Wire.WaitingRequest request, CallOptions options) =>
            throw new RpcException(
                new Status(StatusCode.Unavailable, "the broker went away mid-session"));
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
