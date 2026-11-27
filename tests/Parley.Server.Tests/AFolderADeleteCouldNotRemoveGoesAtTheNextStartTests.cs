using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That the files of a conversation a purge or destroy removed are off the disk by the next start, even when they were
/// held open when it ran.
/// </summary>
/// <remarks>
/// <b>Found by review, round 18.</b> Both commit before they delete the folders, and a folder held open (an editor with
/// the file locked) left the files on disk for good: nothing reaches a folder the store no longer records. The operator
/// had been told they went. Now no row names it, so the next sweep deletes it. Only Windows refuses to delete a
/// file held without sharing, so only there does the purge leave the folder for the start to finish; elsewhere the
/// purge deletes it at once and the test still holds.
/// </remarks>
[Integration]
public sealed class AFolderADeleteCouldNotRemoveGoesAtTheNextStartTests
{
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purged_file_held_open_at_the_purge_is_gone_after_a_restart()
    {
        var directory = Directory.CreateTempSubdirectory("parley-purge-left-").FullName;

        try
        {
            await PurgeHeldOpenThenRestartAsync(directory);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task PurgeHeldOpenThenRestartAsync(string directory)
    {
        var store = Path.Combine(directory, "alpha.db");
        string path;
        Wire.PurgeReply purged;

        await using (var first = await StartAsync(store))
        {
            using var channel = GrpcChannel.ForAddress(first.Addresses[0]);
            var agents = new Wire.Agents.AgentsClient(channel);

            using (var stream = agents.Attach(
                new Wire.AttachRequest { Agent = Inventory, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct))
            {
                Assert.True(await stream.ResponseStream.MoveNext(Ct));
                var placed = await FileWire.PlaceAsync(agents, Inventory, "stock.md", "secret stock levels"u8.ToArray(), "stock", Ct);
                Assert.Equal(Wire.PlaceOutcome.Placed, placed.Outcome);
            }

            path = Path.Combine(first.Runtime.Files.FolderPath("stock"), "stock.md");

            // The person has it open, in a program that holds it (SPEC.md section 10.2's describe_file_location).
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                purged = await new Wire.Operators.OperatorsClient(channel)
                    .PurgeAsync(new Wire.AgentRequest { Agent = Inventory }, cancellationToken: Ct);
            }
        }

        // The operator was told it went, and nothing names a file that stayed.
        Assert.Contains(purged.Destroyed, t => t.Table == "shared_files");

        await using (await StartAsync(store))
        {
            Assert.False(File.Exists(path), $"purged file still on disk after restart: {path}");
        }
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }
}
