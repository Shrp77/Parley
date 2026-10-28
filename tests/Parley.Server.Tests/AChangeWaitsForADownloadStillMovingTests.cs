using Google.Protobuf;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a removal of a file waits for a download still taking frames and never ends it, and that a change refused, given
/// up, or of the same bytes leaves downloads as they were (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>The owner's decision after review round 29:</b> a download is ended only when it has stopped. Until then a change
/// that waited on a download for the stall bound ended it. <b>Found by review, rounds 24 and 25,</b> under that rule: a
/// removal or placement another agent could not make, a change given up while it waited, and placing the same bytes
/// again each took the file's turn and ended downloads. They are kept, because a turn still holds new downloads back
/// while it waits.
/// </remarks>
public sealed class AChangeWaitsForADownloadStillMovingTests
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
    public async Task A_removal_waits_for_a_download_still_taking_frames_past_the_bound_and_then_lands()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        // A frame every 150 ms against a five-second bound, for 128 frames: about twenty seconds, four times the bound,
        // and more than flow control lets the broker write ahead of the reader. Two seconds was read as a stall on a
        // loaded macOS runner, and three on a loaded Windows machine; idle, the broker's write trails the reader by a
        // frame.
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);

        var big = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = Fetch(billing);
        Assert.True(await call.ResponseStream.MoveNext(Ct));
        var reading = ReadSteadilyAsync(call, 150);

        var replacing = orders.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Orders, Files = { new Wire.FileRef { ConversationId = "bait", Name = "big.md" } } },
            cancellationToken: Ct).ResponseAsync;

        await Task.Delay(broker.Runtime.Files.StallAfter * 2, Ct);
        Assert.False(replacing.IsCompleted, "the removal did not wait for a download still taking frames");
        Assert.False(reading.IsCompleted, "a download still taking frames was ended");

        var (status, detail, bytes) = await reading.WaitAsync(TimeSpan.FromSeconds(90), Ct);
        Assert.True(status == StatusCode.OK, $"status={status} detail={detail} bytes={bytes} of {big.Length}");
        Assert.Equal(big.Length, bytes);

        var removed = await replacing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(removed.Removed);
    }

    [Fact]
    public async Task A_removal_by_an_agent_that_did_not_place_the_file_does_not_end_a_steady_download()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        // A reader taking a frame every 200 ms reads a 12 MiB file in about ten seconds; five seconds is a bound that
        // reader never reaches, with room for a slow runner (Linux CI read two as a stall).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);

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

        // A reader taking a frame every 200 ms reads a 12 MiB file in about ten seconds; five seconds is a bound that
        // reader never reaches, with room for a slow runner (Linux CI read two as a stall).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);
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

        // A reader taking a frame every 200 ms reads a 12 MiB file in about ten seconds; five seconds is a bound that
        // reader never reaches, with room for a slow runner (Linux CI read two as a stall).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);
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

        // A reader taking a frame every 200 ms reads a 12 MiB file in about ten seconds; five seconds is a bound that
        // reader never reaches, with room for a slow runner (Linux CI read two as a stall).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);

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

        // Nothing waits on the file now: a fresh download from the start.
        using var second = Fetch(billing);
        var (s2, d2, b2) = await ReadSteadilyAsync(second, 0);

        Assert.True(s2 == StatusCode.OK, $"second status={s2} detail={d2} bytes={b2}");
    }
}
