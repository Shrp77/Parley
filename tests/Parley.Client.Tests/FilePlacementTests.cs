using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Client.Tests;

/// <summary>
/// How a client places a file: the header alone, the broker's answer, and only then the bytes (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// Lens 1 run 124 found a refusal answered while the client was still streaming take down the connection it shared with
/// the adapter's attachment and the person's seat. The broker's half is held over a real socket in
/// <c>ARefusedPlacementLeavesItsConnectionTests</c>; this holds the client's, against a scripted call, including what
/// a lost connection is said as: never the person's file being unreadable, never a broker that was never there.
/// </para>
/// </remarks>
[Unit]
public sealed class FilePlacementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Wire.PlaceFileHeader Header = new() { Agent = "billing@alpha", Name = "notes.md", ConversationId = "pricing" };

    private static Wire.PlaceFileAnswer GoAhead => new() { GoAhead = new Wire.Empty() };

    private static Wire.PlaceFileAnswer Reply(Wire.FileRejection rejection) =>
        new() { Reply = new Wire.PlaceFileReply { Rejection = rejection, Name = "notes.md", ConversationId = "pricing" } };

    [Fact]
    public async Task A_refusal_of_the_header_is_returned_with_no_byte_read_and_the_stream_ended()
    {
        var call = new ScriptedPlacement { First = Reply(Wire.FileRejection.NamePlacedByAnother) };
        await using var content = new CountedStream(new byte[3 * FileLimits.ChunkBytes]);

        var reply = await call.Call().PlaceAsync(Header, content, Ct);

        Assert.Equal(Wire.FileRejection.NamePlacedByAnother, reply.Rejection);
        Assert.Equal(0, content.Reads);
        Assert.Equal(["header", "answer", "end"], call.Log);
    }

    [Fact]
    public async Task No_piece_is_sent_before_the_go_ahead_and_every_piece_is_sent_after_it()
    {
        var call = new ScriptedPlacement { First = GoAhead, Last = Reply(Wire.FileRejection.None) };
        await using var content = new CountedStream(new byte[(2 * FileLimits.ChunkBytes) + 1]);

        var reply = await call.Call().PlaceAsync(Header, content, Ct);

        Assert.Equal(Wire.FileRejection.None, reply.Rejection);
        Assert.Equal(["header", "answer", "piece", "piece", "piece", "end"], call.Log);
    }

    [Fact]
    public async Task A_refusal_while_the_pieces_go_stops_them()
    {
        var call = new ScriptedPlacement { First = GoAhead, Last = Reply(Wire.FileRejection.NoSpace), AnswerAfterPieces = 1 };
        await using var content = new CountedStream(new byte[40 * FileLimits.ChunkBytes]);

        var reply = await call.Call().PlaceAsync(Header, content, Ct);

        Assert.Equal(Wire.FileRejection.NoSpace, reply.Rejection);
        Assert.True(call.Log.Count(e => e == "piece") < 40, string.Join(", ", call.Log));
        Assert.Equal("end", call.Log[^1]);
    }

    [Fact]
    public async Task A_connection_lost_while_the_pieces_go_is_said_to_have_placed_nothing()
    {
        var call = new ScriptedPlacement { First = GoAhead, FailPiece = new IOException("The connection was reset.") };
        await using var content = new CountedStream(new byte[4 * FileLimits.ChunkBytes]);

        var dropped = await Assert.ThrowsAsync<FilePlacement.DroppedException>(() => call.Call().PlaceAsync(Header, content, Ct));

        Assert.False(dropped.SentWhole);
    }

    [Fact]
    public async Task A_connection_lost_before_the_header_was_answered_is_a_drop_not_an_escaped_transport_failure()
    {
        var call = new ScriptedPlacement { FailHeader = new HttpRequestException("The connection was reset.") };
        await using var content = new CountedStream([1, 2, 3]);

        var dropped = await Assert.ThrowsAsync<FilePlacement.DroppedException>(() => call.Call().PlaceAsync(Header, content, Ct));

        Assert.False(dropped.SentWhole);
        Assert.Equal(0, content.Reads);
    }

    [Fact]
    public async Task A_connection_lost_after_the_whole_file_was_sent_may_have_placed_it()
    {
        var call = new ScriptedPlacement
        {
            First = GoAhead,
            FailLast = new RpcException(new Status(StatusCode.Unavailable, "Error reading next message.")),
        };
        await using var content = new CountedStream(new byte[FileLimits.ChunkBytes]);

        var dropped = await Assert.ThrowsAsync<FilePlacement.DroppedException>(() => call.Call().PlaceAsync(Header, content, Ct));

        Assert.True(dropped.SentWhole);
    }

    [Fact]
    public async Task A_connection_whose_answers_stopped_partway_is_said_to_have_placed_nothing_though_the_stream_was_ended()
    {
        var call = new ScriptedPlacement
        {
            First = GoAhead,
            AnswerAfterPieces = 1,
            FailLast = new RpcException(new Status(StatusCode.Unavailable, "Error reading next message.")),
        };
        await using var content = new CountedStream(new byte[40 * FileLimits.ChunkBytes]);

        var dropped = await Assert.ThrowsAsync<FilePlacement.DroppedException>(() => call.Call().PlaceAsync(Header, content, Ct));

        Assert.False(dropped.SentWhole);
    }

    [Fact]
    public async Task A_status_the_broker_set_is_its_answer_and_passes_as_it_came()
    {
        var call = new ScriptedPlacement
        {
            First = GoAhead,
            FailLast = new RpcException(new Status(StatusCode.InvalidArgument, "A placement has one header, and it comes first.")),
        };
        await using var content = new CountedStream([1, 2, 3]);

        var refused = await Assert.ThrowsAsync<RpcException>(() => call.Call().PlaceAsync(Header, content, Ct));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_fails_as_the_file_and_not_as_the_connection()
    {
        var call = new ScriptedPlacement { First = GoAhead, Last = Reply(Wire.FileRejection.None) };
        await using var content = new UnreadableStream();

        await Assert.ThrowsAsync<IOException>(() => call.Call().PlaceAsync(Header, content, Ct));
    }

    [Fact]
    public async Task The_persons_placement_that_lost_its_connection_says_it_may_have_placed_the_file()
    {
        var call = new ScriptedPlacement
        {
            First = GoAhead,
            FailLast = new RpcException(new Status(StatusCode.Unavailable, "Error reading next message.")),
        };
        var broker = new BrokerClient(new Wire.Operators.OperatorsClient(new PlacementOnly(call)));
        await using var content = new CountedStream([1, 2, 3]);

        var dropped = await Assert.ThrowsAsync<RpcException>(() => broker.PlacePersonsFileAsync("pricing", "notes.md", content, Ct));

        Assert.True(Files.PlacementSaid.IsDropped(dropped));
        Assert.Contains("may or may not have been placed", dropped.Status.Detail, StringComparison.Ordinal);
    }

    /// <summary>A placement call answered from a script, recording what the client did in order.</summary>
    private sealed class ScriptedPlacement : IClientStreamWriter<Wire.PlaceFileFrame>, IAsyncStreamReader<Wire.PlaceFileAnswer>
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answerEarly = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _answers;
        private int _pieces;

        public List<string> Log { get; } = [];

        public Wire.PlaceFileAnswer? First { get; init; }

        public Wire.PlaceFileAnswer? Last { get; init; }

        public Exception? FailHeader { get; init; }

        public Exception? FailPiece { get; init; }

        public Exception? FailLast { get; init; }

        public int? AnswerAfterPieces { get; init; }

        public WriteOptions? WriteOptions { get; set; }

        public Wire.PlaceFileAnswer Current { get; private set; } = new();

        public AsyncDuplexStreamingCall<Wire.PlaceFileFrame, Wire.PlaceFileAnswer> Call() =>
            new(this, this, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

        public Task CompleteAsync()
        {
            lock (Log)
            {
                Log.Add("end");
            }

            _ended.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WriteAsync(Wire.PlaceFileFrame message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(Wire.PlaceFileFrame message, CancellationToken cancellationToken)
        {
            var header = message.FrameCase == Wire.PlaceFileFrame.FrameOneofCase.Header;

            if ((header ? FailHeader : FailPiece) is { } failure)
            {
                return Task.FromException(failure);
            }

            lock (Log)
            {
                Log.Add(header ? "header" : "piece");
            }

            if (!header && ++_pieces == AnswerAfterPieces)
            {
                _answerEarly.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_answers++ == 0)
            {
                lock (Log)
                {
                    Log.Add("answer");
                }

                Current = First!;
                return true;
            }

            // As a broker answers: once the client has ended its stream, or partway when it refuses partway.
            await Task.WhenAny(_ended.Task, _answerEarly.Task).ConfigureAwait(false);

            if (FailLast is { } failure)
            {
                throw failure;
            }

            if (Last is null)
            {
                return false;
            }

            Current = Last;
            return true;
        }
    }

    /// <summary>A channel that carries only the person's placement, to the script.</summary>
    private sealed class PlacementOnly(ScriptedPlacement call) : CallInvoker
    {
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)call.Call();

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }

    /// <summary>The bytes of a file, counting how often they were read.</summary>
    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int Reads { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reads++;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    /// <summary>A file that fails when it is read, as one locked by another program does.</summary>
    private sealed class UnreadableStream() : MemoryStream([1, 2, 3], writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The process cannot access the file."));
    }
}
