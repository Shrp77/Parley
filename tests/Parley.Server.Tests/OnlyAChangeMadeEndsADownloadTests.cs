using Google.Protobuf;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That only a change that is made can end somebody else's download: not one refused, given up, or of the same bytes
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 24.</b> A removal or a placement took the file's change turn before it asked who placed
/// the file, and a turn that waits on a download makes that download overdue once it has waited the stall bound. So an
/// agent with no right to the file ended every slow download of it, as often as it liked, and was refused anyway. A
/// change given up while it waited left its waiting stamp behind, and every later download of the file failed at its
/// first frame. Found in round 25: placing the same bytes again took the turn too, ended the download, and wrote
/// nothing.
/// </remarks>
public sealed class OnlyAChangeMadeEndsADownloadTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(StatusCode Status, string? Detail, long Bytes)> ReadSteadilyAsync(
        AsyncServerStreamingCall<Wire.FetchFileFrame> call, int delayMs)
    {
        var bytes = 0L;

        try
        {
            while (await call.ResponseStream.MoveNext(Ct))
            {
                bytes += call.ResponseStream.Current.Chunk.Length;
                await Task.Delay(delayMs, Ct);
            }
        }
        catch (RpcException failure)
        {
            return (failure.StatusCode, failure.Status.Detail, bytes);
        }

        return (StatusCode.OK, null, bytes);
    }

    private static AsyncServerStreamingCall<Wire.FetchFileFrame> Fetch(AttachedClient billing) =>
        billing.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Billing, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);

    private static Wire.RemoveFilesRequest RemoveBig() => new()
    {
        Agent = Inventory,
        Files = { new Wire.FileRef { ConversationId = "bait", Name = "big.md" } },
    };

    [Fact]
    public async Task A_removal_by_an_agent_that_did_not_place_the_file_does_not_end_a_steady_download()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(2);

        var big = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = Fetch(billing);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        var reading = ReadSteadilyAsync(call, 200);
        var removed = await inventory.Client.RemoveOwnFilesAsync(RemoveBig(), cancellationToken: Ct);

        var (status, detail, bytes) = await reading;

        Assert.Empty(removed.Removed);
        Assert.Single(removed.Refused);
        Assert.True(status == StatusCode.OK, $"status={status} detail={detail} bytes={bytes} of {big.Length}");
    }

    [Fact]
    public async Task A_placement_over_a_file_another_placed_while_its_bytes_arrived_does_not_end_a_steady_download()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(2);
        await FileWire.PlaceAsync(orders.Client, Orders, "start.md", "x"u8.ToArray(), "bait", Ct);
        await orders.Client.RequestAsync(Requests.Open(Orders, Inventory, "what are the stock levels", "bait"), cancellationToken: Ct);

        // Inventory begins placing big.md while no file has that name, so the first look lets it through.
        using var placing = inventory.Client.PlaceFile(cancellationToken: Ct);
        await placing.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Inventory, Name = "big.md", ConversationId = "bait" } },
            Ct);
        await placing.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFromUtf8("mine") }, Ct);

        // Orders places big.md meanwhile, and billing starts reading it slowly.
        var big = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = Fetch(billing);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var reading = ReadSteadilyAsync(call, 200);

        await placing.RequestStream.CompleteAsync();
        var placed = await placing.ResponseAsync;

        var (status, detail, bytes) = await reading;

        Assert.True(placed.Outcome == Wire.PlaceOutcome.PlacedByAnother, placed.ToString());
        Assert.True(status == StatusCode.OK, $"status={status} detail={detail} bytes={bytes} of {big.Length}");
    }

    [Fact]
    public async Task A_placement_whose_first_look_saw_its_own_file_does_not_end_a_download_of_the_file_another_placed_since()
    {
        // Found by review, round 25: the turn was decided by the look made before the bytes arrived. Inventory began
        // replacing its own file, removed it, and orders placed one of that name; inventory's finished upload took the
        // turn, ended billing's download of orders' file, and was refused.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(2);
        await orders.Client.RequestAsync(Requests.Open(Orders, Inventory, "what are the stock levels", "bait"), cancellationToken: Ct);

        await FileWire.PlaceAsync(inventory.Client, Inventory, "big.md", "first"u8.ToArray(), "bait", Ct);

        using var placing = inventory.Client.PlaceFile(cancellationToken: Ct);
        await placing.RequestStream.WriteAsync(
            new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Inventory, Name = "big.md", ConversationId = "bait" } },
            Ct);
        await placing.RequestStream.WriteAsync(new Wire.PlaceFileFrame { Chunk = ByteString.CopyFromUtf8("mine") }, Ct);
        await Task.Delay(300, Ct);

        var removed = await inventory.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Inventory, Files = { new Wire.FileRef { ConversationId = "bait", Name = "big.md" } } },
            cancellationToken: Ct);
        Assert.Single(removed.Removed);

        var big = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = Fetch(billing);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var reading = ReadSteadilyAsync(call, 200);

        await placing.RequestStream.CompleteAsync();
        var placed = await placing.ResponseAsync;

        var (status, detail, bytes) = await reading;

        Assert.True(placed.Outcome == Wire.PlaceOutcome.PlacedByAnother, placed.ToString());
        Assert.True(status == StatusCode.OK, $"status={status} detail={detail} bytes={bytes} of {big.Length}");
    }

    [Fact]
    public async Task Placing_the_same_bytes_again_changes_nothing_and_does_not_end_a_steady_download()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(2);

        var big = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = Fetch(billing);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        var reading = ReadSteadilyAsync(call, 200);
        var again = await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        var (status, detail, bytes) = await reading;

        Assert.True(again.Outcome == Wire.PlaceOutcome.AlreadyCurrent, again.ToString());
        Assert.True(status == StatusCode.OK, $"status={status} detail={detail} bytes={bytes} of {big.Length}");
    }

    [Fact]
    public async Task A_removal_given_up_while_it_waited_does_not_leave_the_file_unfetchable()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        // Wide enough that the first download, left unread while the removal is asked and given up, is not taken for a
        // stall on a slow machine (Windows CI read one second as one).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(3);

        var big = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        var first = Fetch(billing);
        Assert.True(await first.ResponseStream.MoveNext(Ct));

        // The placer's own removal, which waits for billing's read and is given up after 200 ms.
        using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            giveUp.CancelAfter(200);

            try
            {
                await orders.Client.RemoveOwnFilesAsync(
                    new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "bait", Name = "big.md" } } },
                    cancellationToken: giveUp.Token);
            }
            catch (RpcException)
            {
            }
        }

        var (s1, d1, _) = await ReadSteadilyAsync(first, 0);
        first.Dispose();
        Assert.True(s1 == StatusCode.OK, $"first status={s1} detail={d1}");

        // Nothing waits on the file now. Past the bound, a fresh download from the start.
        await Task.Delay(4000, Ct);

        using var second = Fetch(billing);
        var (s2, d2, b2) = await ReadSteadilyAsync(second, 0);

        Assert.True(s2 == StatusCode.OK, $"second status={s2} detail={d2} bytes={b2}");
    }
}
