using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That a run of renames inside the quiet period keeps the agent's placer on its file, in folders that do and do not
/// tell case apart (SPEC.md §9.5: a rename keeps the placer).
/// </summary>
/// <remarks>
/// <b>Found by review, round 34.</b> Looks and rename reports were keyed without case, so a chain through the other
/// spelling of a name (a.md to A.md to b.md) overwrote the continued report and the row was removed; and a rename landing
/// just as the first look ran still can (recorded, low).
/// </remarks>
public sealed class RenameChainsKeepThePlacerTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-r34-chain").FullName;

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

    [Theory]
    [InlineData(true, "a.md|A.md|b.md")]
    [InlineData(false, "a.md|A.md|b.md")]
    [InlineData(false, "a.md|b.md|a.md")]
    [InlineData(true, "a.md|b.md|a.md")]
    [InlineData(false, "a.md|b.md|c.md|d.md")]
    [InlineData(true, "a.md|b.md|A.md")]
    [InlineData(true, "a.md|b.md|A.md|c.md")]
    [InlineData(false, "a.md|b.md|c.md|b.md")]
    public async Task A_chain_keeps_the_placer(bool caseApart, string chain)
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), RenamesBesideACaseTwinTests.MacOsChains);
        var steps = chain.Split('|');
        var (server, aaa) = await StartAsync(caseApart);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            for (var i = 1; i < steps.Length; i++)
            {
                File.Move(Path.Combine(aaa, steps[i - 1]), Path.Combine(aaa, steps[i]));
            }

            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            var last = Assert.Single(listed);
            Assert.True(last.Name == steps[^1] && last.PlacedBy == Billing, $"listed: [{names}]");
            await channel.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("delete")]
    [InlineData("agent-onto-twin")]
    public async Task A_chain_beside_a_twin(string mode)
    {
        var (server, aaa) = await StartAsync(true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            await Task.Delay(2000, Ct);

            switch (mode)
            {
                case "keep":
                    File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "b.md"));
                    File.Move(Path.Combine(aaa, "b.md"), Path.Combine(aaa, "c.md"));
                    break;
                case "delete":
                    File.Delete(Path.Combine(aaa, "a.md"));
                    File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "b.md"));
                    File.Move(Path.Combine(aaa, "b.md"), Path.Combine(aaa, "c.md"));
                    break;
                default:
                    File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
                    File.Move(Path.Combine(aaa, "b.md"), Path.Combine(aaa, "A.md"), overwrite: true);
                    break;
            }

            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            switch (mode)
            {
                case "keep":
                    Assert.True(listed.Count == 2 && listed.Any(f => f.Name == "a.md" && f.PlacedBy == Billing)
                        && listed.Any(f => f.Name == "c.md" && f.PlacedBy != Billing), $"listed: [{names}]");
                    break;
                case "delete":
                    Assert.True(listed.Count == 1 && listed.Any(f => f.Name == "c.md" && f.PlacedBy != Billing), $"listed: [{names}]");
                    break;
                default:
                    Assert.True(listed.Count == 1 && listed.Any(f => f.Name == "A.md" && f.PlacedBy == Billing), $"listed: [{names}]");
                    break;
            }

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
            await File.WriteAllTextAsync(Path.Combine(aaa, "Z.md"), "probe", Ct);
            var apart = !File.Exists(Path.Combine(aaa, "z.md"));
            File.Delete(Path.Combine(aaa, "Z.md"));
            Assert.SkipWhen(!apart, "this file system does not tell case apart");
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
