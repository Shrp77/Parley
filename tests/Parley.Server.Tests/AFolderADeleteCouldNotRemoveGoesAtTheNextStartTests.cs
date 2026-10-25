using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>Probe: a purged conversation's folder that could not be deleted at the purge.</summary>
/// <summary>
/// That the files of a conversation a purge or destroy removed are off the disk by the next start, even when they were
/// held open when it ran.
/// </summary>
/// <remarks>
/// <b>Found by review, round 18.</b> Both commit before they delete the folders, and a folder held open (an editor with
/// the file locked) or a broker stopped between the two left the files on disk for good: nothing reaches a folder the
/// store no longer records. The operator had been told they went.
/// </remarks>
public sealed class AFolderADeleteCouldNotRemoveGoesAtTheNextStartTests
{
    private const string Inventory = "inventory@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_purged_file_held_open_at_the_purge_is_gone_after_a_restart()
    {
        var directory = Directory.CreateTempSubdirectory("parley-purge-left-").FullName;
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

            // The person has it open in place, in an editor that holds it (SPEC.md section 9.5's open in place).
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
            // A restart does not reclaim it either.
            Assert.False(File.Exists(path), $"purged file still on disk after restart: {path}");
        }
    }

    private static async Task<ParleyServer> StartAsync(string store)
    {
        var runtime = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

        return await ParleyServer.StartAsync(runtime, "http://127.0.0.1:0", Ct);
    }
}
