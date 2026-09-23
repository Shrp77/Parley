using Grpc.Core;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// That a seat's drain leaves file change notices alone (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's call, answering <c>/speckit-analyze</c> L1: one test, and no change to either surface.</b> A seat is a
/// person, a person never fetches a file through an agent's operation, and notices are raised only for agents that
/// did — so a seat holds none. The inbox reply carries a field for them regardless, and this pins what the seat does
/// if one ever arrives: nothing, rather than recording it as a message the person owes an answer to.
/// </para>
/// </remarks>
public sealed class SeatFileNoticeTests
{
    private static readonly ProjectSettings Alpha =
        new(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:5404", null);

    [Fact]
    public async Task A_drain_carrying_file_notices_records_only_the_messages()
    {
        var token = TestContext.Current.CancellationToken;
        var agents = new NoticesAndOneMessage();
        using var book = new SeatBook(_ => agents);

        Assert.True(Seat.TryRead("arvind", out var me, out _));

        var taken = await book.TakeAsync(Alpha, me, [], token);

        Assert.True(taken.Seated);

        var (messages, polls) = await new SeatVoice(agents, me).TakeDeliveryAsync(taken.Hold!, token);

        Assert.Single(messages);
        Assert.Equal(0, polls);
        Assert.Single(taken.Hold!.Delivered);
    }

    /// <summary>An Agents client holding a seat, whose drain hands back one message and two file notices.</summary>
    private sealed class NoticesAndOneMessage : Wire.Agents.AgentsClient
    {
        /// <inheritdoc/>
        public override AsyncServerStreamingCall<Wire.Event> Attach(Wire.AttachRequest request, CallOptions options) =>
            new(new Acknowledged(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

        /// <inheritdoc/>
        public override AsyncUnaryCall<Wire.InboxReply> DrainInboxAsync(Wire.DrainInboxRequest request, CallOptions options)
        {
            var reply = new Wire.InboxReply { FileNoticesWaiting = 3 };

            reply.Messages.Add(new Wire.DeliveredMessage
            {
                Message = new Wire.Message
                {
                    Id = 1,
                    ExchangeId = 7,
                    From = "orders@alpha",
                    To = request.Agent,
                    Type = Wire.MessageType.Request,
                    PayloadJson = "{\"intent\":\"probe\",\"questions\":[]}",
                    CreatedUtc = "2026-09-14T00:00:00Z",
                    ConversationId = string.Empty
                },
                Envelope = string.Empty
            });

            reply.FileNotices.Add(new Wire.FileNotice
            {
                ConversationId = "pricing",
                Name = "notes.md",
                Sha256Had = new string('a', 64),
                Sha256Now = new string('b', 64),
                SizeBytes = 12,
                ChangedUtc = "2026-09-14T00:00:00Z"
            });

            reply.FileNotices.Add(new Wire.FileNotice
            {
                ConversationId = "pricing",
                Name = "rates.md",
                Sha256Had = new string('c', 64),
                ChangedUtc = "2026-09-14T00:00:00Z",
                Gone = true
            });

            return new(Task.FromResult(reply), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
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
