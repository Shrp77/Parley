using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a download its client stops reading is let go, so it cannot stall a destroy or other agents' placements.
/// </summary>
/// <remarks>
/// <b>Found by review, round 22.</b> Flow control parks a write to a client that stops reading, and the hold that
/// download kept made a destroy, purge or prune wait for good, and with it every placement that waits one out. A
/// download that makes no progress for <see cref="FileLocationStore.StallAfter"/> is now ended; a replacement no
/// longer waits out a hold over everything at all. One still taking frames is never ended.
/// </remarks>
[Integration]
public sealed class AStalledDownloadIsLetGoTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Text(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_fetch_never_read_does_not_stall_an_unrelated_destroy_or_other_agents_placements()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var inventory = await AttachedClient.AttachAsync(broker, Inventory, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(1);

        // A large file the slow agent will fetch; nothing bounds a file's size.
        var big = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        // An unrelated conversation with a file, which the person destroys; and another with a file inventory removes.
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);
        await FileWire.PlaceAsync(inventory.Client, Inventory, "stock.md", Text("12"), "stock", Ct);

        // Orders fetches and reads one frame, then never reads again: HTTP/2 flow control parks the broker's write.
        using var slow = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);
        Assert.True(await slow.ResponseStream.MoveNext(Ct));
        await Task.Delay(500, Ct);

        var destroying = broker.ConnectAsOperator()
            .DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;
        await Task.Delay(300, Ct);

        var replacing = inventory.Client.RemoveOwnFilesAsync(
            new Wire.RemoveFilesRequest { Agent = Inventory, Files = { new Wire.FileRef { ConversationId = "stock", Name = "stock.md" } } },
            cancellationToken: Ct).ResponseAsync;
        var firstPlacing = FileWire.PlaceAsync(billing.Client, Billing, "fresh.md", Text("new"), "elsewhere", Ct);

        await Task.WhenAll(
                Task.WhenAny(destroying, Task.Delay(5000, Ct)),
                Task.WhenAny(replacing, Task.Delay(5000, Ct)),
                Task.WhenAny(firstPlacing, Task.Delay(5000, Ct)));

        Assert.True(
            destroying.IsCompleted && replacing.IsCompleted && firstPlacing.IsCompleted,
            $"after 5s with orders' fetch unread: destroy of an unrelated conversation done={destroying.IsCompleted}, "
            + $"inventory's removal done={replacing.IsCompleted}, billing's first placement done={firstPlacing.IsCompleted}");

        // Done, not merely finished: a fault is completed too.
        Assert.Equal(Wire.DestroyVerdict.Gone, (await destroying).Verdict);
        Assert.Single((await replacing).Removed);
        Assert.Equal(Wire.FileRejection.None, (await firstPlacing).Rejection);

        // And the unread download was ended, not left running.
        var ended = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            while (await slow.ResponseStream.MoveNext(Ct))
            {
            }
        });
        Assert.Equal(Grpc.Core.StatusCode.DeadlineExceeded, ended.StatusCode);
    }

    [Fact]
    public async Task A_fetch_still_taking_frames_is_never_ended_and_a_destroy_waits_for_it_to_finish()
    {
        // The owner's decision after review round 29: a download that has stopped is let go, but one still moving is
        // never ended, whatever waits on it, because nothing still moving times out. Round 23 ended it once a destroy,
        // purge or prune had waited on it for the bound.
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);

        // A frame every 150 ms against a five-second bound: a loaded machine does not read the gap as a stall (three
        // seconds was, on a Windows machine running every suite at once).
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(5);

        // 128 frames, about twenty seconds: more than flow control lets the broker write ahead of the reader, so the
        // broker is still writing, and still holding, when the bound has passed.
        var big = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);
        await FileWire.PlaceAsync(billing.Client, Billing, "terms.md", Text("net 30"), "pricing", Ct);

        using var slow = orders.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Orders, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);

        var trickle = Task.Run(
            async () =>
            {
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                    System.Security.Cryptography.HashAlgorithmName.SHA256);

                while (await slow.ResponseStream.MoveNext(Ct))
                {
                    if (slow.ResponseStream.Current.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Chunk)
                    {
                        hash.AppendData(slow.ResponseStream.Current.Chunk.Span);
                    }

                    await Task.Delay(150, Ct);
                }

                return Convert.ToHexStringLower(hash.GetHashAndReset());
            },
            Ct);

        await Task.Delay(600, Ct);

        var destroying = broker.ConnectAsOperator()
            .DestroyAsync(new Wire.ConversationRequest { ConversationId = "pricing" }, cancellationToken: Ct).ResponseAsync;

        // Past the bound, the destroy is still waiting and the download still going.
        await Task.Delay(broker.Runtime.Files.StallAfter + TimeSpan.FromSeconds(1), Ct);
        Assert.False(destroying.IsCompleted, "the destroy did not wait for a download still taking frames");
        Assert.False(trickle.IsCompleted, "the download still taking frames was ended");

        var got = await trickle.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(big)), got);

        Assert.Equal(Wire.DestroyVerdict.Gone, (await destroying.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Verdict);
    }
}
