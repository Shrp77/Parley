using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a file held by another program when the broker starts is passed over, and the scan finishes the rest.
/// </summary>
/// <remarks>
/// <b>Found by review, round 30.</b> A held file made the scan throw at the first folder it met and start again from the
/// top, so every folder after it went unscanned for as long as the file stayed held.
/// </remarks>
public sealed class AHeldFileDoesNotStopTheScanTests
{
    private const string Billing = "billing@alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_held_in_one_folder_does_not_stop_the_scan_finding_a_new_file_in_another()
    {
        var directory = Path.Combine(Path.GetTempPath(), "parley-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "alpha.db");

        try
        {
            var runtime1 = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);
            await using (var first = await ParleyServer.StartAsync(runtime1, "http://127.0.0.1:0", Ct))
            {
                using var channel = GrpcChannel.ForAddress(first.Addresses[0]);
                var client = new Wire.Agents.AgentsClient(channel);
                using var stream = client.Attach(
                    new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                    cancellationToken: Ct);
                Assert.True(await stream.ResponseStream.MoveNext(Ct));

                await FileWire.PlaceAsync(client, Billing, "a.md", "a"u8.ToArray(), "aaa", Ct);
                await FileWire.PlaceAsync(client, Billing, "z.md", "z"u8.ToArray(), "zzz", Ct);
                await channel.ShutdownAsync();
            }

            var aaa = Path.Combine(store + StoreFiles.Files, "aaa");
            var zzz = Path.Combine(store + StoreFiles.Files, "zzz");
            Assert.True(Directory.Exists(aaa), "folder aaa");
            Assert.True(Directory.Exists(zzz), "folder zzz");

            var held = Path.Combine(aaa, "held.md");
            await File.WriteAllTextAsync(held, "held by another program", Ct);
            await File.WriteAllTextAsync(Path.Combine(zzz, "offline.md"), "put here while stopped", Ct);

            using var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

            var runtime2 = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);
            await using var second = await ParleyServer.StartAsync(runtime2, "http://127.0.0.1:0", Ct);
            using var channel2 = GrpcChannel.ForAddress(second.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel2);

            await Task.Delay(TimeSpan.FromSeconds(6), Ct);

            var listed = (await operators.ListSharedFilesAsync(new Wire.ListFilesRequest(), cancellationToken: Ct)).Files;
            var scanned = second.Runtime.Watcher.Scanned.IsCompleted;

            hold.Dispose();
            await channel2.ShutdownAsync();

            Assert.True(listed.Any(f => f.Name == "offline.md"), $"offline.md in zzz not listed; scan finished={scanned}; listed={string.Join(",", listed.Select(f => f.Name))}");
            Assert.Contains(listed, f => f.Name == "offline.md");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Fact]
    public async Task A_placed_file_renamed_while_stopped_and_held_at_start_keeps_its_placer()
    {
        // While a file in a folder cannot be read it may be a recorded file renamed, so the folder's missing rows are not
        // judged gone in that pass; judged then, the rename would be lost and the placer with it.
        var directory = Path.Combine(Path.GetTempPath(), "parley-held-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "alpha.db");

        try
        {
            var runtime1 = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);
            await using (var first = await ParleyServer.StartAsync(runtime1, "http://127.0.0.1:0", Ct))
            {
                using var channel = GrpcChannel.ForAddress(first.Addresses[0]);
                var client = new Wire.Agents.AgentsClient(channel);
                using var stream = client.Attach(
                    new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                    cancellationToken: Ct);
                Assert.True(await stream.ResponseStream.MoveNext(Ct));

                await FileWire.PlaceAsync(client, Billing, "terms.md", "net 30"u8.ToArray(), "aaa", Ct);
                await channel.ShutdownAsync();
            }

            var aaa = Path.Combine(store + StoreFiles.Files, "aaa");
            var renamed = Path.Combine(aaa, "renamed.md");
            File.Move(Path.Combine(aaa, "terms.md"), renamed);

            // Held before the broker starts, so its scan at start meets it held.
            var hold = new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.None);

            var runtime2 = BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);
            await using var second = await ParleyServer.StartAsync(runtime2, "http://127.0.0.1:0", Ct);
            using var channel2 = GrpcChannel.ForAddress(second.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel2);

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await hold.DisposeAsync();

            var listed = await FileWire.WaitForAsync(operators, l => l.Any(f => f.Name == "renamed.md"), TimeSpan.FromSeconds(10), Ct);
            await channel2.ShutdownAsync();

            Assert.Equal(Billing, Assert.Single(listed, f => f.Name == "renamed.md").PlacedBy);
            Assert.DoesNotContain(listed, f => f.Name == "terms.md");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
