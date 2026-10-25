using System.Security.Cryptography;
using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the download stall bound ends only a download that has stopped, and tells its client so.
/// </summary>
/// <remarks>
/// Kept from review round 23, where both held: a slow but steady reader gets every byte, and one that stops is told the
/// download was ended (DeadlineExceeded), not that the broker is down, after its hold was let go.
/// </remarks>
public sealed class ASteadyDownloadIsNotEndedTests
{
    private const string Orders = "orders@alpha";
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slow_but_steady_reader_of_a_large_file_gets_all_of_it()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        // Fifteen frames' worth of margin, so a busy machine slowing the reader does not look like a stall.
        broker.Runtime.Files.StallAfter = TimeSpan.FromSeconds(3);

        var big = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = billing.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Billing, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var frames = 0;

        // Each frame taken 200 ms apart: always well inside the bound, though the whole download takes seconds.
        while (await call.ResponseStream.MoveNext(Ct))
        {
            frames++;
            var frame = call.ResponseStream.Current;
            Assert.NotEqual(Wire.FetchFileFrame.FrameOneofCase.Refusal, frame.FrameCase);

            if (frame.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Chunk)
            {
                hash.AppendData(frame.Chunk.Span);
            }

            await Task.Delay(200, Ct);
        }

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(big)), Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.True(frames > 40, $"frames={frames}");
    }

    [Fact]
    public async Task A_stalled_reader_that_resumes_is_told_the_download_was_ended_not_that_the_broker_is_down()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var orders = await AttachedClient.AttachAsync(broker, Orders, cancellationToken: Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);

        broker.Runtime.Files.StallAfter = TimeSpan.FromMilliseconds(500);

        var big = new byte[32 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await FileWire.PlaceAsync(orders.Client, Orders, "big.md", big, "bait", Ct);

        using var call = billing.Client.FetchFile(
            new Wire.FetchFileRequest { Agent = Billing, Name = "big.md", ConversationId = "bait" }, cancellationToken: Ct);
        Assert.True(await call.ResponseStream.MoveNext(Ct));

        // Stop reading well past the bound: the broker ends the download and lets the file go.
        await Task.Delay(2500, Ct);
        Assert.False(broker.Runtime.Files.IsHeld("bait", Parley.Core.SharedFileName.Create("big.md")));

        var chunks = 0L;
        var status = StatusCode.OK;
        string? detail = null;

        try
        {
            while (await call.ResponseStream.MoveNext(Ct))
            {
                chunks += call.ResponseStream.Current.Chunk.Length;
            }
        }
        catch (RpcException failure)
        {
            status = failure.StatusCode;
            detail = failure.Status.Detail;
        }

        Assert.True(chunks < big.Length, $"bytes after resuming={chunks}");
        Assert.True(status == StatusCode.DeadlineExceeded, $"status={status} detail={detail} bytes={chunks}");
    }
}
