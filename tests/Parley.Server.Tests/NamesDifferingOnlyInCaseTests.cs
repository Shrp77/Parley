using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Two files in one conversation folder whose names differ only in case, which a folder that tells case apart can hold.
/// </summary>
/// <remarks>
/// <b>Found by review, round 31.</b> The scan at start keyed the folder's files by name without case, and two such
/// names threw: every folder after it went unscanned, and the broker's stop rethrew it, leaving its store locked.
/// </remarks>
public sealed class NamesDifferingOnlyInCaseTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-case-twins").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_case_twin_is_passed_over_the_scan_finishes_and_the_broker_stops_and_lets_go_of_its_store()
    {
        var store = Path.Combine(_directory, "alpha.db");

        await using (var first = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct))
        {
            using var channel = GrpcChannel.ForAddress(first.Addresses[0]);
            var client = new Wire.Agents.AgentsClient(channel);
            using var attached = client.Attach(
                new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct);
            Assert.True(await attached.ResponseStream.MoveNext(Ct));

            await FileWire.PlaceAsync(client, Billing, "a.md", "a"u8.ToArray(), "aaa", Ct);
            await FileWire.PlaceAsync(client, Billing, "z.md", "z"u8.ToArray(), "zzz", Ct);
            await channel.ShutdownAsync();
        }

        var aaa = Path.Combine(store + StoreFiles.Files, "aaa");
        await TellCaseApartAsync(aaa);

        await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
        Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
        await File.WriteAllTextAsync(Path.Combine(store + StoreFiles.Files, "zzz", "offline.md"), "put here while stopped", Ct);

        var second = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        IReadOnlyList<Wire.SharedFileView> listed;

        using (var channel = GrpcChannel.ForAddress(second.Addresses[0]))
        {
            listed = await FileWire.WaitForAsync(
                new Wire.Operators.OperatorsClient(channel), l => l.Any(f => f.Name == "offline.md"), TimeSpan.FromSeconds(10), Ct);
            await channel.ShutdownAsync();
        }

        Assert.True(second.Runtime.Watcher.Scanned.IsCompletedSuccessfully, $"the scan ended {second.Runtime.Watcher.Scanned.Status}");

        // The recorded file is the one in the location, and keeps its placer.
        Assert.Equal(Billing, Assert.Single(listed, f => string.Equals(f.Name, "a.md", StringComparison.OrdinalIgnoreCase)).PlacedBy);

        await second.DisposeAsync();
        await (await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct)).DisposeAsync();
    }

    private static BrokerRuntime Open(string store) =>
        BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

    /// <summary>
    /// Makes a folder tell case apart, as every folder on Linux does. Windows does it per folder, only while it is empty.
    /// </summary>
    private async Task TellCaseApartAsync(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var aside = Path.Combine(_directory, "aside");
        Directory.Move(folder, aside);
        Directory.CreateDirectory(folder);

        using var fsutil = Process.Start(new ProcessStartInfo("fsutil", $"file setCaseSensitiveInfo \"{folder}\" enable")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await fsutil.WaitForExitAsync(Ct);

        foreach (var file in Directory.GetFiles(aside))
        {
            File.Move(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        Assert.SkipWhen(fsutil.ExitCode != 0, "this machine cannot make a folder tell case apart (fsutil setCaseSensitiveInfo)");
    }
}
