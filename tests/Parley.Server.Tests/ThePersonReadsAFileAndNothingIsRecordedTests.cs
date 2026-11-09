using Grpc.Core;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the person reads a placed file through the broker wherever the surface runs, and that reading records nothing
/// (SPEC.md §9.5, §10.2 <c>read_shared_file</c>).
/// </summary>
[Integration]
public sealed class ThePersonReadsAFileAndNothingIsRecordedTests
{
    private const string Billing = "billing@alpha";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Wire.FetchFileFrame>> ReadAsync(Wire.Operators.OperatorsClient operators, string conversation, string name, string expect = "")
    {
        using var call = operators.ReadSharedFile(
            new Wire.ReadSharedFileRequest { ConversationId = conversation, Name = name, ExpectSha256 = expect },
            cancellationToken: Ct);

        var frames = new List<Wire.FetchFileFrame>();

        await foreach (var frame in call.ResponseStream.ReadAllAsync(Ct))
        {
            frames.Add(frame);
        }

        return frames;
    }

    [Fact]
    public async Task The_person_reads_an_agents_image_whole_and_no_fetch_is_recorded()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "mockup.png", Png, "pricing", Ct);

        var frames = await ReadAsync(operators, "pricing", "mockup.png", ContentHash.Of(Png).Value);

        var header = Assert.Single(frames, f => f.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Header).Header;
        Assert.Equal(Billing, header.PlacedBy);
        Assert.Equal(Png, frames.Where(f => f.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Chunk).SelectMany(f => f.Chunk.ToByteArray()).ToArray());
        Assert.DoesNotContain(frames, f => f.FrameCase == Wire.FetchFileFrame.FrameOneofCase.Refusal);

        // A fetch is what an agent reports to be told of changes; the person's read left none for anybody to report.
        Assert.False(broker.Runtime.Fetches.TryConsume(AgentName.Create(Billing), "pricing", "MOCKUP.PNG", ContentHash.Of(Png)));
    }

    [Fact]
    public async Task A_read_is_refused_for_a_missing_file_a_changed_hash_and_no_conversation()
    {
        await using var broker = await LoopbackBroker.StartAsync(Ct);
        await using var billing = await AttachedClient.AttachAsync(broker, Billing, cancellationToken: Ct);
        var operators = broker.ConnectAsOperator();

        await FileWire.PlaceAsync(billing.Client, Billing, "mockup.png", Png, "pricing", Ct);

        var missing = Assert.Single(await ReadAsync(operators, "pricing", "gone.png"));
        var changed = Assert.Single(await ReadAsync(operators, "pricing", "mockup.png", new string('c', 64)));
        var nowhere = await Assert.ThrowsAsync<RpcException>(() => ReadAsync(operators, string.Empty, "mockup.png"));

        Assert.Equal(Wire.FileRejection.NoSuchFile, missing.Refusal.Rejection);
        Assert.Equal(Wire.FileRejection.Changed, changed.Refusal.Rejection);
        Assert.Equal(StatusCode.InvalidArgument, nowhere.StatusCode);
    }
}
