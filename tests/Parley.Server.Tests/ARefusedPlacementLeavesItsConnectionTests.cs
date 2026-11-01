using Google.Protobuf;
using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A placement the broker refuses ends no other call on its connection (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by review, lens 1 run 124.</b> The broker answered a refusal before reading a byte while the client was still
/// streaming pieces, and the server treats bytes on a call it has finished as a fault in the whole connection: it ended
/// it, and with it the adapter's attachment (so the session dropped, and work already collected was handed over again)
/// or the person's seat and every operator call beside it. A file of one frame never showed it; a few megabytes did,
/// three to five times in twenty.
/// </para>
/// <para>
/// So each case is looped, on a connection that also holds an attachment and makes calls alongside, and is checked by
/// what the client could still do: every piece it wrote landed on a call still open, the client of Parley's own read no
/// byte of a file refused at once, the attachment is open and is told of a request, and every call alongside succeeded.
/// </para>
/// </remarks>
[Integration]
public sealed class ARefusedPlacementLeavesItsConnectionTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const int Rounds = 20;

    private static readonly string Person = PersonSeat.On(ProjectName.Create("test")).Value;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Megabytes(int count)
    {
        var bytes = new byte[count * 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        return bytes;
    }

    [Fact]
    public async Task A_refusal_to_a_client_that_did_not_wait_reads_its_pieces_and_leaves_the_attachment_open()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing");
        await FileWire.PlaceAsync(orders.Client, Orders, "notes.md", "orders' notes"u8.ToArray(), "pricing", Ct);

        var big = Megabytes(4);

        await using var alongside = Alongside(() => billing.Client.ListFilesAsync(
            new Wire.ListFilesRequest { Agent = Billing }, cancellationToken: Ct).ResponseAsync);

        for (var round = 0; round < Rounds; round++)
        {
            // Every piece written without waiting for the go-ahead, as the adapter did before the handshake. Each write
            // succeeds only while the call is open: the broker reads them all before it ends it.
            using var call = billing.Client.PlaceFile(cancellationToken: Ct);

            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "notes.md", ConversationId = "pricing" } },
                Ct);

            for (var offset = 0; offset < big.Length; offset += FileLimits.ChunkBytes)
            {
                await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(big, offset, FileLimits.ChunkBytes) }, Ct);
            }

            await call.RequestStream.CompleteAsync();

            var refused = await call.ReplyAsync().WaitAsync(Patience, Ct);

            Assert.Equal(Wire.FileRejection.NamePlacedByAnother, refused.Rejection);
        }

        await alongside.StopAsync();
        await AssertStillAttachedAsync(orders, billing);
    }

    [Fact]
    public async Task The_adapters_placement_reads_no_byte_of_a_file_the_broker_refuses_and_leaves_the_attachment_open()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing");
        await FileWire.PlaceAsync(orders.Client, Orders, "notes.md", "orders' notes"u8.ToArray(), "pricing", Ct);

        var big = Megabytes(4);

        await using var alongside = Alongside(() => billing.Client.ListFilesAsync(
            new Wire.ListFilesRequest { Agent = Billing }, cancellationToken: Ct).ResponseAsync);

        for (var round = 0; round < Rounds; round++)
        {
            using var call = billing.Client.PlaceFile(cancellationToken: Ct);
            await using var content = new CountedStream(big);

            var refused = await call.PlaceAsync(
                    new Wire.PlaceFileHeader { Agent = Billing, Name = "notes.md", ConversationId = "pricing" }, content, Ct)
                .WaitAsync(Patience, Ct);

            Assert.Equal(Wire.FileRejection.NamePlacedByAnother, refused.Rejection);
            Assert.Equal(0, content.Reads);
        }

        await alongside.StopAsync();
        await AssertStillAttachedAsync(orders, billing);
    }

    [Fact]
    public async Task The_persons_refused_placement_leaves_their_seat_and_the_operator_calls_on_its_connection_working()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // One connection for the seat and the operator calls, as a surface holds them.
        var channel = broker.OpenChannel();
        await using var seat = await AttachedClient.AttachAsync(broker, Person, cancellationToken: Ct, adapter: PersonSeat.Adapter, over: channel);
        var operators = new Wire.Operators.OperatorsClient(channel);

        var big = Megabytes(4);

        await using var alongside = Alongside(() => operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct).ResponseAsync);

        for (var round = 0; round < Rounds; round++)
        {
            using var call = operators.PlacePersonsFile(cancellationToken: Ct);
            await using var content = new CountedStream(big);

            var refused = await call.PlaceAsync(new Wire.PlaceFileHeader { Name = "brief.md", ConversationId = "nowhere" }, content, Ct)
                .WaitAsync(Patience, Ct);

            Assert.Equal(Wire.FileRejection.ConversationNotAssignable, refused.Rejection);
            Assert.Equal(0, content.Reads);
        }

        await alongside.StopAsync();
        await AssertStillAttachedAsync(orders, seat);
    }

    [Fact]
    public async Task A_frame_against_the_rules_partway_is_refused_only_once_the_rest_has_been_read()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        await RequestAsync(orders, Billing, "pricing");

        var big = Megabytes(4);
        var header = new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "twice.md", ConversationId = "pricing" } };

        await using var alongside = Alongside(() => billing.Client.ListFilesAsync(
            new Wire.ListFilesRequest { Agent = Billing }, cancellationToken: Ct).ResponseAsync);

        for (var round = 0; round < Rounds; round++)
        {
            using var call = billing.Client.PlaceFile(cancellationToken: Ct);

            await call.RequestStream.WriteAsync(header, Ct);
            Assert.True(await call.ResponseStream.MoveNext(Ct));
            Assert.Equal(Wire.PlaceFileAnswer.AnswerOneofCase.GoAhead, call.ResponseStream.Current.AnswerCase);

            // A second header after the first piece, and megabytes more behind it, every write landing on an open call.
            await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(big, 0, FileLimits.ChunkBytes) }, Ct);
            await call.RequestStream.WriteAsync(header, Ct);

            for (var offset = 0; offset < big.Length; offset += FileLimits.ChunkBytes)
            {
                await call.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(big, offset, FileLimits.ChunkBytes) }, Ct);
            }

            await call.RequestStream.CompleteAsync();

            var refused = await Assert.ThrowsAsync<RpcException>(() => call.ReplyAsync().WaitAsync(Patience, Ct));

            Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        }

        await alongside.StopAsync();
        await AssertStillAttachedAsync(orders, billing);

        Assert.Equal(0, (await broker.ConnectAsOperator().ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).TotalFiles);
    }

    private static async Task RequestAsync(AttachedClient from, string to, string conversation) =>
        await from.Client.RequestAsync(Requests.Open(from.Name, to, "what are the payment terms", conversation), cancellationToken: Ct);

    // Open, and told of a request sent to it now: a stream the connection took down with it hears nothing.
    private static async Task AssertStillAttachedAsync(AttachedClient sender, AttachedClient attached)
    {
        Assert.True(attached.StillAttached, $"{attached.Name}'s attachment ended with its connection.");

        await sender.Client.RequestAsync(Requests.Open(sender.Name, attached.Name, "are you still there"), cancellationToken: Ct);
        await attached.WaitForNoticeAsync(Patience);

        Assert.True(attached.StillAttached, $"{attached.Name}'s attachment ended with its connection.");
    }

    private static Calls Alongside(Func<Task> call) => new(call);

    /// <summary>Calls made on the same connection for as long as the placements go, every failure kept.</summary>
    private sealed class Calls : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;
        private readonly List<string> _failures = [];
        private int _made;

        public Calls(Func<Task> call)
        {
            _running = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await call();
                        Interlocked.Increment(ref _made);
                    }
                    catch (RpcException failed)
                    {
                        lock (_failures)
                        {
                            _failures.Add($"{failed.StatusCode}: {failed.Status.Detail}");
                        }
                    }

                    await Task.Delay(5, CancellationToken.None);
                }
            });
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await _running;

            Assert.True(_made > 0, "No call was made alongside the placements.");
            Assert.True(_failures.Count == 0, "Calls alongside the placements failed: " + string.Join("; ", _failures));
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _running;
            _stop.Dispose();
        }
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }
    }
}
