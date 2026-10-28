using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That what one collection handed the person is recorded against the seat even when a later collection fails.
/// </summary>
/// <remarks>
/// One collection carries a bounded amount, so the seat asks again until nothing is left, and the broker stamps each
/// collection delivered as it hands it over. Recorded only at the end, a later collection that failed (a page reloaded
/// mid-drain is enough) dropped what the earlier ones had handed over: the terminal then said the person owed no result.
/// </remarks>
public sealed class ASeatKeepsWhatAnEarlierCollectionHandedOverTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5404", null);

    [Fact]
    public async Task Messages_handed_over_by_the_first_pass_are_recorded_when_the_second_fails()
    {
        var token = TestContext.Current.CancellationToken;
        var agents = new TwoPasses();
        using var book = new SeatBook(_ => agents);

        var taken = await book.TakeAsync(Alpha, Person, token);
        Assert.True(taken.Seated);

        await Assert.ThrowsAsync<RpcException>(
            async () => await new SeatVoice(agents, new Seat(PersonSeat.On(Alpha.Name))).TakeDeliveryAsync(taken.Hold!, token));

        // The broker stamped exchange 7 delivered on the first collection; the seat must hold it.
        Assert.Equal(2, agents.Calls);
        Assert.Single(taken.Hold!.Delivered);
    }

    private static IReadOnlyList<AgentRecord> Person { get; } =
    [
        new AgentRecord(
            PersonSeat.On(Alpha.Name), PersonSeat.Adapter, AgentTier.Pull, null, null, null, false, AgentStatus.Active),
    ];

    private sealed class TwoPasses : Wire.Agents.AgentsClient
    {
        public int Calls { get; private set; }

        public override AsyncServerStreamingCall<Wire.Event> Attach(Wire.AttachRequest request, CallOptions options) =>
            new(new Acknowledged(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

        public override AsyncDuplexStreamingCall<Wire.DrainInboxFrame, Wire.InboxReply> DrainInbox(CallOptions options) =>
            FakeCollection.Answering(Answer);

        private Wire.InboxReply Answer(Wire.DrainInboxRequest request)
        {
            Calls++;

            if (Calls > 1)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "the page went away."));
            }

            var reply = new Wire.InboxReply { MessagesWaiting = 3 };
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

            return reply;
        }
    }

    private sealed class Acknowledged : IAsyncStreamReader<Wire.Event>
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

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}
