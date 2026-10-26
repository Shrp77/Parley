using System.Diagnostics;
using System.Security.Cryptography;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a rename leaves the row with the bytes on disk and the placer on its own file (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 34.</b> A rename moved the row with the file's time now and its old hash, so a same-size
/// edit saved in the quiet period was never hashed again and every fetch was refused as changed, for good. And a case
/// twin's rename, keyed without case, overwrote the agent's own rename report, so the agent's row was removed while its
/// file stayed on disk as the person's.
/// </remarks>
public sealed class ARenameNeverLosesTheBytesOrThePlacerTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-rename-bytes").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_rename_then_a_same_size_edit_records_the_edited_bytes()
    {
        var (server, aaa) = await StartAsync(caseApart: false);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);
            var client = new Wire.Agents.AgentsClient(channel);

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
            await Task.Delay(100, Ct);
            await File.WriteAllTextAsync(Path.Combine(aaa, "b.md"), "b", Ct);
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy} {f.Sha256}"));
            var onDisk = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(aaa, "b.md"), Ct)));

            using var attached = client.Attach(
                new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct);
            Assert.True(await attached.ResponseStream.MoveNext(Ct));
            var fetched = "fetched";

            try
            {
                _ = await FileWire.FetchAndReportAsync(client, Billing, "b.md", "aaa", Ct);
            }
            catch (InvalidOperationException refused)
            {
                fetched = refused.Message;
            }

            var b = Assert.Single(listed);
            Assert.True(b.Name == "b.md" && b.Sha256 == onDisk, $"on disk {onDisk}; listed {names}; fetch: {fetched}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task A_rename_then_a_same_size_edit_survives_a_restart_scan()
    {
        var (server, aaa) = await StartAsync(caseApart: false);
        var store = Path.Combine(_directory, "alpha.db");

        await using (server)
        {
            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
            await Task.Delay(100, Ct);
            await File.WriteAllTextAsync(Path.Combine(aaa, "b.md"), "b", Ct);
            await Task.Delay(3000, Ct);
        }

        await using var again = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        await again.Runtime.Watcher.Scanned;
        using var channel2 = GrpcChannel.ForAddress(again.Addresses[0]);
        var operators2 = new Wire.Operators.OperatorsClient(channel2);
        var listed = (await operators2.ListSharedFilesAsync(
            new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
        var onDisk = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(aaa, "b.md"), Ct)));
        var b = Assert.Single(listed);
        Assert.True(b.Sha256 == onDisk, $"on disk {onDisk}; listed {b.Sha256} after restart");
        await channel2.ShutdownAsync();
    }

    [Fact]
    public async Task A_twins_rename_does_not_overwrite_the_agents_rename_report()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
            await Task.Delay(100, Ct);
            File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "x.md"));
            await Task.Delay(100, Ct);

            // The person opens and saves the agent's renamed file unchanged: an event at b.md puts its look last.
            await File.WriteAllTextAsync(Path.Combine(aaa, "b.md"), "a", Ct);
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by '{f.PlacedBy}'"));

            Assert.True(listed.Any(f => f.Name == "b.md" && f.PlacedBy == Billing), $"the agent's row is lost: {names}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task A_twins_rename_after_the_agents_rename_keeps_the_placer()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
            await Task.Delay(100, Ct);
            File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "x.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by '{f.PlacedBy}'"));

            Assert.True(listed.Any(f => f.Name == "b.md" && f.PlacedBy == Billing), $"the agent's row is lost: {names}");
            Assert.True(listed.Any(f => f.Name == "x.md" && f.PlacedBy != Billing), $"x: {names}");
            await channel.ShutdownAsync();
        }
    }

    private async Task<(ParleyServer Server, string Folder)> StartAsync(bool caseApart)
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

        if (caseApart)
        {
            await TellCaseApartAsync(aaa);
        }

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

        Assert.SkipWhen(fsutil.ExitCode != 0, "this machine cannot make a folder tell case apart");
    }
}
