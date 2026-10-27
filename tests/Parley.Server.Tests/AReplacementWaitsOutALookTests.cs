using System.Security.Cryptography;
using Google.Protobuf;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an agent replacing its own file just after placing it is not refused for the watcher's look at the first one
/// (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 36.</b> A look a change asked for always hashes, and on Windows a move over a file open for
/// that read is refused, so a replacement landing during the look of the first placement failed with an internal error.
/// The second placement is held back to land at a spread of moments around the look.
/// </remarks>
public sealed class AReplacementWaitsOutALookTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-replace-look").FullName;

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
    public async Task A_second_placement_during_the_first_ones_look_keeps_its_hash()
    {
        // Large enough that one hash outlasts a single attempt at the move.
        const int sizeMb = 8;
        var store = Path.Combine(_directory, "alpha.db");

        await using var server = await ParleyServer.StartAsync(
            BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System),
            "http://127.0.0.1:0", Ct);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(server.Addresses[0]);

        var client = new Wire.Agents.AgentsClient(channel);
        var operators = new Wire.Operators.OperatorsClient(channel);
        using var attached = client.Attach(
            new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
            cancellationToken: Ct);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        var path = Path.Combine(store + StoreFiles.Files, "aaa", "a.md");
        var failures = new List<string>();
        var rounds = 0;

        foreach (var lead in new[] { 480, 500, 510, 520, 530, 540, 550, 560, 580, 600, 620, 650 })
        {
            rounds++;
            var v1 = Content(sizeMb, (byte)(rounds * 2));
            var v2 = Content(sizeMb, (byte)(rounds * 2 + 1));
            var h2 = Convert.ToHexStringLower(SHA256.HashData(v2));

            // v1 placed; its move raises an event, and the look 500 ms later hashes v1.
            _ = await FileWire.PlaceAsync(client, Billing, "a.md", v1, "aaa", Ct);
            var placedAt = DateTime.UtcNow;

            // v2 uploaded now, and completed (committed) at about the moment v1's look is hashing.
            using var call = client.PlaceFile(cancellationToken: Ct);
            await call.RequestStream.WriteAsync(
                new Wire.PlaceFileFrame { Header = new Wire.PlaceFileHeader { Agent = Billing, Name = "a.md", ConversationId = "aaa" } }, Ct);
            for (var offset = 0; offset < v2.Length; offset += FileLimits.ChunkBytes)
            {
                await call.RequestStream.WriteAsync(
                    new Wire.PlaceFileFrame { Chunk = ByteString.CopyFrom(v2, offset, Math.Min(FileLimits.ChunkBytes, v2.Length - offset)) }, Ct);
            }

            var wait = placedAt.AddMilliseconds(lead) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, Ct);
            }

            await call.RequestStream.CompleteAsync();
            Wire.PlaceFileReply second;

            try
            {
                second = await call.ResponseAsync;
            }
            catch (Grpc.Core.RpcException thrown)
            {
                failures.Add($"lead {lead}: second placement threw {thrown.StatusCode} '{thrown.Status.Detail}' at {(DateTime.UtcNow - placedAt).TotalMilliseconds:F0} ms");
                await Task.Delay(1500, Ct);
                continue;
            }

            // Watched for two seconds: every listing must say v2's hash once v2 was placed.
            var seen = new List<string>();
            var until = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < until)
            {
                var listed = await operators.ListSharedFilesAsync(new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct);
                var hash = Assert.Single(listed.Files).Sha256;
                if (seen.Count == 0 || seen[^1] != hash)
                {
                    seen.Add(hash);
                }

                await Task.Delay(20, Ct);
            }

            var onDisk = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct)));

            if (seen.Any(h => h != h2) || onDisk != h2)
            {
                failures.Add($"lead {lead}: placed {second.Outcome} {second.Rejection} {(second.Sha256.Length > 8 ? second.Sha256[..8] : second.Sha256)}, listed {string.Join(" -> ", seen.Select(h => h[..8]))}, disk {onDisk[..8]}, want {h2[..8]}");
            }

            await Task.Delay(1500, Ct);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static byte[] Content(int megabytes, byte seed)
    {
        var bytes = new byte[megabytes * 1024 * 1024];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
