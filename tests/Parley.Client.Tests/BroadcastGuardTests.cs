using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// The guards a broadcast must not lose by being N sends instead of one.
/// </summary>
/// <remarks>
/// <para>
/// A loop is where a rule applied once quietly becomes a rule applied never. Rule 8.5's cap, the
/// identity a message is sent under, and the seat check the sends happen under are each checked
/// here rather than assumed to survive the plural case.
/// </para>
/// <para>
/// <b>The seat check is the one with history.</b> 0.18.9 records a composer that captured its seat
/// when it opened and went on speaking under a name the book had released — found on Linux and
/// passing on Windows, because what decided it was whether a stream had finished tearing down. A
/// broadcast is twenty round trips with the idle sweep running throughout, so the window it left
/// open is twenty times wider.
/// </para>
/// </remarks>
public sealed class BroadcastGuardTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5402", null, null);

    private static AgentName[] Peers =>
    [
        AgentName.Create("orders@test"),
        AgentName.Create("billing@test"),
        AgentName.Create("inventory@test"),
    ];

    /// <summary>
    /// A client that holds an attachment open and refuses every send.
    /// </summary>
    /// <remarks>
    /// The attachment has to stay open or the seat is marked lost the moment it is taken, which
    /// would make every test below a test of the seat guard rather than of the thing it names. So
    /// the stream yields its acknowledgement and then waits, which is what a real one does.
    /// </remarks>
    private sealed class Refusing : Wire.Agents.AgentsClient
    {
        public int Calls { get; private set; }

        public override AsyncUnaryCall<Wire.NotifyReply> NotifyAsync(
            Wire.NotifyRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new RpcException(new Status(StatusCode.Unavailable, "nothing is listening"));
        }

        /// <summary>How many refusals were recorded as evidence about the cap.</summary>
        public int Refusals { get; private set; }

        public override AsyncUnaryCall<Wire.Empty> RecordRefusalAsync(
            Wire.RecordRefusalRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Refusals++;

            return new(
                Task.FromResult(new Wire.Empty()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options) =>
            new(new Waiting(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }

    /// <summary>A stream that acknowledges an attach and then holds, until it is cancelled.</summary>
    private sealed class Waiting : IAsyncStreamReader<Wire.Event>
    {
        private bool _sent;

        public Wire.Event Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                Current = new Wire.Event { Attached = new Wire.Attached() };

                return true;
            }

            // Held rather than ended. A stream that ended here would mark the seat lost, which is a
            // different test from every one in this file except the first.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }
    }

    [Fact]
    public async Task A_seat_already_lost_sends_to_nobody()
    {
        // The guard at the top of every iteration, including the first. A person whose seat went
        // while the composer was open must not have a single message go out under it.
        var token = TestContext.Current.CancellationToken;

        using var book = new SeatBook(_ => new SeatDoubles.Stubborn(StatusCode.PermissionDenied));
        var taken = await book.TakeAsync(Alpha, Seated("arvind"), [], token);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => taken.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

        var agents = new Refusing();
        var voice = new SeatVoice(agents, taken.Hold!.Seat);

        var said = await voice.NotifyEveryoneAsync(
            taken.Hold!, Peers, "nobody should hear this", "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Contains("Told 0 of 3", said, StringComparison.Ordinal);
        Assert.Contains(SeatVoice.Left, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_over_long_message_is_refused_once_and_reaches_nobody()
    {
        // The cap is checked before the loop, not inside it. Checked inside, a message twenty
        // characters over would be refused twenty times and recorded as twenty refusals — twenty
        // pieces of evidence about the cap from one act, which is what §17 question 16 counts.
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated("arvind"));

        var said = await voice.NotifyEveryoneAsync(
            Hold(), Peers, new string('x', MessageLimits.MaxPayloadCharacters + 100), "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.DoesNotContain("Told", said, StringComparison.Ordinal);

        // **Once, not once per recipient.** §17 question 16 counts refusals as evidence about where
        // the cap should sit, and three rows for one over-long message somebody wrote once would be
        // three pieces of evidence about a single act.
        Assert.Equal(1, agents.Refusals);
    }

    [Fact]
    public async Task Nothing_written_reaches_nobody()
    {
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated("arvind"));

        var said = await voice.NotifyEveryoneAsync(Hold(), Peers, "   ", "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Contains("Nothing was written", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_roster_says_so_and_calls_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated("arvind"));

        var said = await voice.NotifyEveryoneAsync(Hold(), [], "anybody?", "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Equal(SeatVoice.NobodyToTell, said);
    }

    [Fact]
    public async Task A_broker_that_answers_nothing_loses_no_recipient_silently()
    {
        // One unreachable peer must not lose the other two sends, and a broker that is gone
        // entirely must be reported rather than reported as success.
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated("arvind"));

        var said = await voice.NotifyEveryoneAsync(Hold(), Peers, "into the void", "c1", token);

        Assert.Equal(3, agents.Calls);
        Assert.Contains("Told 0 of 3", said, StringComparison.Ordinal);
        Assert.Contains("Refused for", said, StringComparison.Ordinal);

        foreach (var peer in Peers)
        {
            Assert.Contains(peer.Value, said, StringComparison.Ordinal);
        }
    }

    /// <summary>A seat, read the way the surfaces read one.</summary>
    /// <param name="person">The person's name.</param>
    /// <returns>The seat.</returns>
    private static Seat Seated(string person)
    {
        Assert.True(Seat.TryRead(person, out var seat, out _));

        return seat;
    }

    /// <summary>A hold nothing has taken away, for the cases that are not about the seat.</summary>
    /// <returns>The hold.</returns>
    private static SeatHold Hold()
    {
        var book = new SeatBook(_ => new Refusing());

        return book.TakeAsync(
            Alpha,
            Seated("arvind"),
            [],
            TestContext.Current.CancellationToken).GetAwaiter().GetResult().Hold!;
    }
}
