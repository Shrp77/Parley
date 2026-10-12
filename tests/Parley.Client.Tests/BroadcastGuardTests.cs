using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// The guards a Request to everybody must not lose by being N sends instead of one.
/// </summary>
/// <remarks>
/// <para>
/// A loop is where a rule applied once quietly becomes a rule applied never. Rule 8.4's cap, rule 8.1's
/// ask, and the seat check the sends happen under are each checked here rather than assumed to survive
/// the plural case.
/// </para>
/// <para>
/// <b>The seat check is the one with history.</b> 0.18.9 records a composer that captured its seat when it
/// opened and went on speaking under a name the book had released — found on Linux and passing on Windows,
/// because what decided it was whether a stream had finished tearing down. Twenty sends with the idle sweep
/// running throughout leave that window twenty times wider, and every Request that lands under a released
/// name is work owed to somebody who has left.
/// </para>
/// </remarks>
public sealed class BroadcastGuardTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5402", null);

    private static AgentName[] Peers =>
    [
        AgentName.Create("orders@test"),
        AgentName.Create("billing@test"),
        AgentName.Create("inventory@test"),
    ];

    /// <summary>The ask these send, so the guards under test are the only thing that can refuse them.</summary>
    private const string Ask = "does anything here read orders.total, and can it move?";

    /// <summary>
    /// A client that holds an attachment open and refuses every send.
    /// </summary>
    /// <remarks>
    /// The attachment has to stay open or the seat is marked lost the moment it is taken, which would make
    /// every test below a test of the seat guard rather than of the thing it names. So the stream yields its
    /// acknowledgement and then waits, which is what a real one does.
    /// </remarks>
    private sealed class Refusing : Wire.Agents.AgentsClient
    {
        public int Calls { get; private set; }

        public override AsyncUnaryCall<Wire.RequestReply> RequestAsync(
            Wire.OpenRequest request,
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
        // The guard at the top of every iteration, including the first. A person whose seat went while the
        // composer was open must not have a single Request go out under it.
        var token = TestContext.Current.CancellationToken;

        using var book = new SeatBook(_ => new SeatDoubles.Stubborn(StatusCode.PermissionDenied));
        var taken = await book.TakeAsync(Alpha, Person, token);

        Assert.True(taken.Seated);

        await Eventually.UntilAsync(
            () => taken.Hold!.Lost, "the seat never gave up on a broker refusing it", token);

        var agents = new Refusing();
        var voice = new SeatVoice(agents, taken.Hold!.Seat);

        var said = await voice.RequestEveryoneAsync(
            taken.Hold!, Peers, Ask, "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Contains("Sent to 0 of 3", said, StringComparison.Ordinal);
        Assert.Contains(SeatVoice.Left, said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_over_long_request_is_refused_once_and_reaches_nobody()
    {
        // The cap is checked before the loop, not inside it. Checked inside, a Request twenty characters
        // over would be refused twenty times and recorded as twenty refusals — twenty pieces of evidence
        // about the cap from one act, which is what §17 question 16 counts.
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestEveryoneAsync(
            Hold(), Peers, new string('x', MessageLimits.MaxPayloadCharacters + 100), "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.DoesNotContain("Sent to", said, StringComparison.Ordinal);
        Assert.Equal(1, agents.Refusals);
    }

    [Fact]
    public async Task Nothing_written_reaches_nobody()
    {
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestEveryoneAsync(Hold(), Peers, "   ", "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Contains("Nothing was asked for", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_roster_says_so_and_calls_nothing()
    {
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestEveryoneAsync(Hold(), [], Ask, "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.Equal(SeatVoice.NobodyToAsk, said);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task What_the_single_send_refuses_is_refused_before_anybody_is_sent_anything(string? ask)
    {
        // Checked once, before the loop, in the single send's own words — so a Request the single form refuses
        // cannot reach nineteen people through the plural one. Since 0.7 there is one thing to refuse: an ask that
        // says nothing.
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestEveryoneAsync(Hold(), Peers, ask, "c1", token);

        Assert.Equal(0, agents.Calls);
        Assert.DoesNotContain("Sent to", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_that_answers_nothing_loses_no_recipient_silently()
    {
        // Every send is attempted and every failure is reported, rather than the first failure ending the
        // run and the other recipients vanishing from the report.
        var token = TestContext.Current.CancellationToken;

        var agents = new Refusing();
        var voice = new SeatVoice(agents, Seated());

        var said = await voice.RequestEveryoneAsync(Hold(), Peers, Ask, "c1", token);

        Assert.Equal(3, agents.Calls);
        Assert.Contains("Sent to 0 of 3", said, StringComparison.Ordinal);
        Assert.Contains("Refused for 3", said, StringComparison.Ordinal);
        Assert.DoesNotContain("owed", said, StringComparison.Ordinal);

        foreach (var peer in Peers)
        {
            Assert.Contains(peer.Value, said, StringComparison.Ordinal);
        }
    }

    /// <summary>The project's person seat, as the broker names it.</summary>
    private static Seat Seated() => new(PersonSeat.On(Alpha.Name));

    /// <summary>The project's registry, holding only its person seat.</summary>
    private static IReadOnlyList<AgentRecord> Person { get; } =
    [
        new AgentRecord(
            PersonSeat.On(Alpha.Name), PersonSeat.Adapter, AgentTier.Pull, null, null, null, false, AgentStatus.Active),
    ];

    /// <summary>A hold nothing has taken away, for the cases that are not about the seat.</summary>
    /// <returns>The hold.</returns>
    private static SeatHold Hold()
    {
        var book = new SeatBook(_ => new Refusing());

        return book.TakeAsync(
            Alpha,
            Person,
            TestContext.Current.CancellationToken).GetAwaiter().GetResult().Hold!;
    }
}
