using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a change keeping a file's size and time is recorded, so its old hash is not listed or refused for good
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 35.</b> A look compared size and time and hashed only when they differed, so a same-size
/// change whose time read as before (a coarse file-system clock, a tool that keeps times) kept the old hash: listed,
/// and every fetch refused as changed, for good. A look a change asked for now always hashes, and a fetch that finds the
/// bytes differ asks for one, which covers a change made while the broker was stopped.
/// </remarks>
public sealed class ASameSizeChangeIsHashedAgainTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-same-size").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception left) when (left is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_change_seen_while_running_is_hashed_whatever_its_size_and_time_say()
    {
        var store = Path.Combine(_directory, "alpha.db");

        await using var server = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(server.Addresses[0]);

        var onDisk = await PlaceAndRewriteAsync(channel, store);
        await Task.Delay(2000, Ct);

        await AssertServedAsync(channel, onDisk);
        await channel.ShutdownAsync();
    }

    [Fact]
    public async Task A_change_made_while_stopped_is_recorded_once_a_fetch_finds_it()
    {
        var store = Path.Combine(_directory, "alpha.db");
        string onDisk;

        await using (var first = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct))
        {
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(first.Addresses[0]);
            await PlaceAsync(channel);
            await channel.ShutdownAsync();
        }

        onDisk = Rewrite(store);

        await using var again = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        await again.Runtime.Watcher.Scanned;
        using var channel2 = Grpc.Net.Client.GrpcChannel.ForAddress(again.Addresses[0]);

        // The scan goes by size and time, and both match: the first fetch finds the bytes differ, and is refused.
        Assert.NotEqual("fetched", await FetchAsync(channel2));

        await Task.Delay(2000, Ct);

        await AssertServedAsync(channel2, onDisk);
        await channel2.ShutdownAsync();
    }

    private static async Task PlaceAsync(Grpc.Net.Client.GrpcChannel channel)
    {
        var client = new Wire.Agents.AgentsClient(channel);
        using var attached = Attach(client);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));
        await FileWire.PlaceAsync(client, Billing, "a.md", "aaaa"u8.ToArray(), "aaa", Ct);
    }

    private static async Task<string> PlaceAndRewriteAsync(Grpc.Net.Client.GrpcChannel channel, string store)
    {
        await PlaceAsync(channel);
        await Task.Delay(1500, Ct);
        return Rewrite(store);
    }

    // A same-size rewrite whose time reads as before.
    private static string Rewrite(string store)
    {
        var path = Path.Combine(store + Parley.Core.StoreFiles.Files, "aaa", "a.md");
        var time = File.GetLastWriteTimeUtc(path);

        File.WriteAllText(path, "bbbb");
        File.SetLastWriteTimeUtc(path, time);

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static async Task<string> FetchAsync(Grpc.Net.Client.GrpcChannel channel)
    {
        var client = new Wire.Agents.AgentsClient(channel);
        using var attached = Attach(client);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        try
        {
            _ = await FileWire.FetchAndReportAsync(client, Billing, "a.md", "aaa", Ct);
            return "fetched";
        }
        catch (InvalidOperationException refused)
        {
            return refused.Message;
        }
    }

    private static async Task AssertServedAsync(Grpc.Net.Client.GrpcChannel channel, string onDisk)
    {
        var operators = new Wire.Operators.OperatorsClient(channel);
        var listed = (await operators.ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = Parley.Core.FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;

        Assert.Equal(onDisk, Assert.Single(listed).Sha256);
        Assert.Equal("fetched", await FetchAsync(channel));
    }

    private static AsyncServerStreamingCall<Wire.Event> Attach(Wire.Agents.AgentsClient client) =>
        client.Attach(
            new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = Parley.Core.ProtocolVersion.Current },
            cancellationToken: Ct);

    private static BrokerRuntime Open(string store) =>
        BrokerRuntime.Open(Parley.Core.ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);
}
