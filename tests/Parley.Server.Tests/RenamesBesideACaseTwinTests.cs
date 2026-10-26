using System.Diagnostics;
using Grpc.Net.Client;
using Parley.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// That renames beside a case twin, and quick runs of renames, keep each placer on its own file (SPEC.md §9.5).
/// </summary>
/// <remarks>
/// <b>Found by review, round 33.</b> Looks and rename reports are keyed without case, and a rename was taken as the
/// recorded file's move from whichever spelling reported it: a twin renamed while the agent's file was deleted carried
/// the agent's placer onto the person's file, which the agent could then remove; another file renamed onto the twin's
/// name displaced the agent's row. Two renames inside the quiet period lost the placer, and a twin stayed unlisted after
/// an agent removed its own file or renamed it away.
/// </remarks>
public sealed class RenamesBesideACaseTwinTests : IDisposable
{
    private const string Billing = "billing@alpha";

    private readonly string _directory = Directory.CreateTempSubdirectory("parley-renames-twin").FullName;

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
    public async Task Renaming_the_agents_file_away_leaves_the_twin_listed()
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
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            Assert.Contains(listed, f => f.Name == "b.md" && f.PlacedBy == Billing);
            Assert.True(listed.Any(f => f.Name == "A.md"), $"the twin is not listed: {names}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Renaming_the_agents_file_to_a_refused_name_leaves_the_twin_listed()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "a.exe"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            Assert.True(listed.Any(f => f.Name == "A.md"), $"the twin is not listed: {names}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task A_case_only_rename_keeps_the_placer_where_case_is_told_apart()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);
            Assert.SkipUnless(TellsCaseApart(aaa), "this file system does not tell case apart");

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "A.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            var a = Assert.Single(listed);
            Assert.True(a.Name == "A.md" && a.PlacedBy == Billing, names);
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Renaming_another_file_onto_the_twins_name_leaves_the_agents_file_recorded()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            // Asked before the move: where case is not told apart, A.md is a.md and the move itself throws (macOS).
            Assert.SkipUnless(TellsCaseApart(aaa), "this file system does not tell case apart");

            await File.WriteAllTextAsync(Path.Combine(aaa, "x.md"), "the person's own", Ct);
            await Task.Delay(2000, Ct);

            File.Move(Path.Combine(aaa, "x.md"), Path.Combine(aaa, "A.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            Assert.True(File.Exists(Path.Combine(aaa, "a.md")), "the agent's file is still on disk");
            Assert.True(listed.Any(f => f.Name == "a.md" && f.PlacedBy == Billing), $"listed: {names}");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Two_renames_in_a_row_keep_the_placer()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), MacOsChains);
        var (server, aaa) = await StartAsync(caseApart: false);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);

            File.Move(Path.Combine(aaa, "a.md"), Path.Combine(aaa, "b.md"));
            File.Move(Path.Combine(aaa, "b.md"), Path.Combine(aaa, "c.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));

            var c = Assert.Single(listed);
            Assert.True(c.Name == "c.md" && c.PlacedBy == Billing, names);
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task A_twin_renamed_while_the_agents_file_is_deleted_does_not_take_the_agents_placer()
    {
        var (server, aaa) = await StartAsync(caseApart: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);
            var client = new Wire.Agents.AgentsClient(channel);

            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");
            await Task.Delay(2000, Ct);

            // Inside one quiet period, so the two events meet in one look.
            File.Delete(Path.Combine(aaa, "a.md"));
            await Task.Delay(250, Ct);
            File.Move(Path.Combine(aaa, "A.md"), Path.Combine(aaa, "c.md"));
            await Task.Delay(3000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));
            var c = Assert.Single(listed);
            Assert.True(c.Name == "c.md" && c.PlacedBy != Billing, names);

            using var attached = client.Attach(
                new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct);
            Assert.True(await attached.ResponseStream.MoveNext(Ct));

            var removed = await client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "aaa", Name = "c.md" } } },
                cancellationToken: Ct);

            Assert.Empty(removed.Removed);
            Assert.True(File.Exists(Path.Combine(aaa, "c.md")), "the agent removed the person's file");
            await channel.ShutdownAsync();
        }
    }

    [Fact]
    public async Task An_agent_removing_its_own_file_leaves_the_twin_listed()
    {
        var (server, aaa) = await StartAsync(caseApart: true, twinWhileStopped: true);

        await using (server)
        {
            using var channel = GrpcChannel.ForAddress(server.Addresses[0]);
            var operators = new Wire.Operators.OperatorsClient(channel);
            var client = new Wire.Agents.AgentsClient(channel);
            using var attached = client.Attach(
                new Wire.AttachRequest { Agent = Billing, Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = ProtocolVersion.Current },
                cancellationToken: Ct);
            Assert.True(await attached.ResponseStream.MoveNext(Ct));
            Assert.SkipWhen(Directory.GetFiles(aaa).Length != 2, "this file system does not tell case apart");

            var removed = await client.RemoveOwnFilesAsync(
                new Wire.RemoveFilesRequest { Agent = Billing, Files = { new Wire.FileRef { ConversationId = "aaa", Name = "a.md" } } },
                cancellationToken: Ct);
            Assert.Single(removed.Removed);

            await Task.Delay(2000, Ct);

            var listed = (await operators.ListSharedFilesAsync(
                new Wire.ListFilesRequest { PageSize = FileLimits.MaximumPageSize }, cancellationToken: Ct)).Files;
            var names = string.Join(", ", listed.Select(f => $"{f.Name} by {f.PlacedBy}"));
            Assert.True(listed.Any(f => f.Name == "A.md" && f.PlacedBy != Billing), $"the twin is not listed as the person's: [{names}]");
            await channel.ShutdownAsync();
        }
    }

    private async Task<(ParleyServer Server, string Folder)> StartAsync(bool caseApart, bool twinWhileStopped = false)
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

        // Put there while nothing watched, so the scan at start finds the twin and no event at its path ever comes.
        if (twinWhileStopped)
        {
            await File.WriteAllTextAsync(Path.Combine(aaa, "A.md"), "the person's own", Ct);
        }

        var second = await ParleyServer.StartAsync(Open(store), "http://127.0.0.1:0", Ct);
        await second.Runtime.Watcher.Scanned;

        return (second, aaa);
    }

    /// <summary>Why a chain of renames is not held on macOS.</summary>
    /// <remarks>
    /// Seen in CI, 2026-10-26, and not reproducible here: macOS's watcher, on FSEvents, does not report a rename of a
    /// rename inside the quiet period as a chain, so the placer is lost there as it was everywhere before. Skipped
    /// rather than loosened, so it shows as a known gap instead of passing.
    /// </remarks>
    internal const string MacOsChains =
        "macOS's file watcher does not report a rename of a rename as a chain; the placer is kept for one rename there";

    // The agent's a.md is in the folder; A.md answers for it only where case is not told apart.
    private static bool TellsCaseApart(string folder) => !File.Exists(Path.Combine(folder, "A.md"));

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
