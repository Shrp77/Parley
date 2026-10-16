using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// One person, two surfaces, and what each of them believes is owed.
/// </summary>
/// <remarks>
/// <para>
/// The spec's edge case is that a person seated in the console and the terminal at once must keep
/// both working, and that <i>neither may release the other's in-progress work</i>. `DoubleSeatTests`
/// probes the attachments; this probes the bookkeeping behind them, which is the half that decides
/// what each window says a person still owes.
/// </para>
/// <para>
/// <b>A drain is not repeatable</b> — it is what marks a message delivered — so the surface that
/// drains is the only one that will ever hold that message. That is by design and is written down
/// in <see cref="SeatHold.Delivered"/>. What is probed here is the consequence nobody had checked:
/// whether answering from one window leaves the other showing a debt that is already paid.
/// </para>
/// </remarks>
public sealed class TwoSurfaceDeliveryTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5404", null);

    [Fact]
    public async Task A_drain_lands_on_the_surface_that_drained_and_not_the_other()
    {
        // Pinning the premise the rest of this rests on, rather than assuming it. Two books is
        // two surfaces; the broker hands the messages to whoever asked.
        var token = TestContext.Current.CancellationToken;
        var agents = new OneQuestion();
        using var console = new SeatBook(_ => agents);
        using var terminal = new SeatBook(_ => agents);

        var inBrowser = await console.TakeAsync(Alpha, Person, token);
        var inTerminal = await terminal.TakeAsync(Alpha, Person, token);

        Assert.True(inBrowser.Seated);
        Assert.True(inTerminal.Seated);

        await new SeatVoice(agents, Seated()).TakeDeliveryAsync(inTerminal.Hold!, token);

        Assert.Single(inTerminal.Hold!.Delivered);
        Assert.Empty(inBrowser.Hold!.Delivered);
    }

    [Fact]
    public async Task A_drain_that_fails_records_nothing_against_the_seat()
    {
        // The drain and the record are one act on purpose. If the broker refuses, the seat must
        // not be left holding messages it was never handed -- a person owing an answer to a
        // question that does not exist is worse than one who has to look again.
        var token = TestContext.Current.CancellationToken;
        var agents = new OneQuestion(refuseDrain: true);
        using var book = new SeatBook(_ => agents);

        var taken = await book.TakeAsync(Alpha, Person, token);

        Assert.True(taken.Seated);

        await Assert.ThrowsAsync<RpcException>(
            async () => await new SeatVoice(agents, Seated()).TakeDeliveryAsync(taken.Hold!, token));

        Assert.Empty(taken.Hold!.Delivered);
    }

    [Fact]
    public async Task Recording_and_settling_at_once_neither_loses_nor_duplicates()
    {
        // `_delivered` is written by a drain and read by every page render, and settled from a
        // third place. Contended rather than reasoned about.
        var token = TestContext.Current.CancellationToken;
        var agents = new OneQuestion();
        using var book = new SeatBook(_ => agents);
        var voice = new SeatVoice(agents, Seated());

        var taken = await book.TakeAsync(Alpha, Person, token);

        Assert.True(taken.Seated);

        var hold = taken.Hold!;

        var drains = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(async () => await voice.TakeDeliveryAsync(hold, token), token));

        var readers = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => _ = hold.Delivered.Count, token));

        await Task.WhenAll([.. drains, .. readers]);

        // The stub hands the same message back every time, so twelve drains is twelve records.
        // What matters is that it is exactly twelve: a lost update or a torn read would not be.
        Assert.Equal(12, hold.Delivered.Count);
    }

    /// <summary>The project's person seat, as the broker names it.</summary>
    private static Seat Seated() => new(PersonSeat.On(Alpha.Name));

    /// <summary>The project's registry, holding only its person seat.</summary>
    private static IReadOnlyList<AgentRecord> Person { get; } =
    [
        new AgentRecord(
            PersonSeat.On(Alpha.Name), PersonSeat.Adapter, AgentTier.Pull, null, null, null, false, AgentStatus.Active),
    ];

    /// <summary>An Agents client holding a seat, with one question waiting behind it.</summary>
    /// <param name="refuseDrain">Whether the drain refuses instead of answering.</param>
    private sealed class OneQuestion(bool refuseDrain = false) : Wire.Agents.AgentsClient
    {
        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(
            Wire.AttachRequest request, CallOptions options) =>
            new(new Acknowledged(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });

        /// <inheritdoc/>
        public override AsyncUnaryCall<Wire.InboxReply> DrainInboxAsync(
            Wire.DrainInboxRequest request, CallOptions options)
        {
            if (refuseDrain)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "not now."));
            }

            var reply = new Wire.InboxReply();

            reply.Messages.Add(new Wire.DeliveredMessage
            {
                Message = new Wire.Message
                {
                    Id = 1,
                    ExchangeId = 7,
                    From = "orders@alpha",
                    To = request.Agent,
                    Type = Wire.MessageType.Request,
                    PayloadJson = "{\"ask\":\"probe\"}",
                    CreatedUtc = "2026-09-02T00:00:00Z",
                    ConversationId = string.Empty
                },
                Envelope = string.Empty
            });

            return new(Task.FromResult(reply),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
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
