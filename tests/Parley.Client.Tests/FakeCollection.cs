using Grpc.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>A collection a fake broker answers: one reply to the collect frame, and the call ends on the acknowledgement.</summary>
/// <remarks>
/// The fakes answer as the broker does when it records the collection delivered, so a seat reads them as delivered. A
/// fake that throws from its answer is a broker that refused the collection, with nothing recorded.
/// </remarks>
internal static class FakeCollection
{
    internal static AsyncDuplexStreamingCall<Wire.DrainInboxFrame, Wire.InboxReply> Answering(
        Func<Wire.DrainInboxRequest, Wire.InboxReply> answer,
        bool breakAfterTheReply = false)
    {
        var replies = new Replies { BreakAfterTheReply = breakAfterTheReply };

        return new(
            new Frames(frame =>
            {
                if (frame.FrameCase == Wire.DrainInboxFrame.FrameOneofCase.Collect)
                {
                    replies.Waiting.Enqueue(answer(frame.Collect));
                }
            }),
            replies,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });
    }

    private sealed class Frames(Action<Wire.DrainInboxFrame> sent) : IClientStreamWriter<Wire.DrainInboxFrame>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync() => Task.CompletedTask;

        public Task WriteAsync(Wire.DrainInboxFrame message)
        {
            sent(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(Wire.DrainInboxFrame message, CancellationToken cancellationToken) => WriteAsync(message);
    }

    private sealed class Replies : IAsyncStreamReader<Wire.InboxReply>
    {
        public Queue<Wire.InboxReply> Waiting { get; } = new();

        public bool BreakAfterTheReply { get; init; }

        public Wire.InboxReply Current { get; private set; } = new();

        private bool _replied;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!Waiting.TryDequeue(out var next))
            {
                // A connection lost after the reply: the broker may or may not have recorded the acknowledgement.
                return _replied && BreakAfterTheReply
                    ? Task.FromException<bool>(new RpcException(new Status(StatusCode.Unavailable, "the connection went.")))
                    : Task.FromResult(false);
            }

            _replied = true;

            Current = next;
            return Task.FromResult(true);
        }
    }
}
