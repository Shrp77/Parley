using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That an event at a case twin of a recorded file leaves the recorded file, its row and its placer alone, in a folder
/// that tells case apart (SPEC.md §9.5: the twin is treated as a name that breaks a rule).
/// </summary>
/// <remarks>
/// <b>Found by review, round 32.</b> Round 31 made the scan at start keep the recorded file of two case twins, but an
/// event found its row without case: deleting the person's twin removed the agent's row with its file still on disk,
/// and renaming the twin moved the agent's placer onto the person's file.
/// </remarks>
public sealed class ACaseTwinsEventsLeaveTheRecordedFileTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-case-twin-events").FullName;

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
    public async Task Deleting_the_persons_twin_leaves_the_agents_file_recorded()
    {
        var (server, aaa) = await StartWithTwinFolderAsync();

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Delete(Path.Combine(aaa, "A.md"));
            await Task.Delay(2000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;

            Assert.True(File.Exists(Path.Combine(aaa, "a.md")), "the agent's file is still on disk");
            var a = Assert.Single(listed, f => string.Equals(f.Name, "a.md", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Billing, a.PlacedBy);
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Renaming_the_persons_twin_does_not_move_the_agents_file()
    {
        var (server, aaa) = await StartWithTwinFolderAsync();

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "b.md"));
            await Task.Delay(2000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;

            var a = Assert.Single(listed, f => string.Equals(f.Name, "a.md", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Billing, a.PlacedBy);
            var b = listed.SingleOrDefault(f => f.Name == "b.md");
            Assert.True(b is null || b.PlacedBy != Billing, $"b.md, the person's, is listed as placed by {b?.PlacedBy}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Deleting_the_agents_file_leaves_the_persons_twin_listed_as_the_persons()
    {
        var (server, aaa) = await StartWithTwinFolderAsync();

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Delete(Path.Combine(aaa, "a.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;

            var a = Assert.Single(listed, f => string.Equals(f.Name, "a.md", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("A.md", a.Name);
            Assert.NotEqual(Billing, a.PlacedBy);
            await channel.ShutdownAsync();
        }
    }

    private async Task<(ParleyServer Server, string Folder)> StartWithTwinFolderAsync()
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
            await channel.ShutdownAsync();
        }

        var aaa = Path.Combine(store + StoreFiles.Files, "aaa");
        await TellCaseApartAsync(aaa);

        var second = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        await second.Runtime.Watcher.Scanned;

        return (second, aaa);
    }

    private static BrokerRuntime Open(string store) =>
        BrokerRuntime.Open(ProjectName.Create("test"), store, Parley.Broker.BrokerOptions.Default, TimeProvider.System);

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
