using System.Collections.Concurrent;
using Grpc.Net.Client;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node asked to start an agent whose workspace is gone starts nothing, and has the broker retire it only where a
/// deletion can be told from a disk that is absent or replaced: on the home disk, beside a folder in use, through no link
/// (SPEC.md §14).
/// </summary>
/// <remarks>
/// A deleted repository is worth a judgement; a drive briefly unmounted, or another disk answering at the same path, is
/// not, and one unavailable volume must not retire every agent on it at once.
/// </remarks>
[Integration]
public sealed class ANodeReportsAWorkspaceGoneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-gone-" + Path.GetRandomFileName());

    public ANodeReportsAWorkspaceGoneTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentName Billing => AgentName.Create("billing@" + NodeCatalogue.Machine);

    private string Evidence => Path.Combine(_root, "launched.txt");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A catalogue with one checkout at <paramref name="workspace"/>, read while it existed.</summary>
    private async Task<(NodeCatalogue Catalogue, AgentIdentity Identity)> CatalogueAsync(string workspace)
    {
        var identity = AgentIdentity.Mint();
        _ = Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } }
              },
              "{{LaunchConfiguration.IdentityMember}}": "{{identity}}"
            }
            """,
            Ct);

        var path = Path.Combine(_root, NodeCatalogue.DefaultFileName);

        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": {{System.Text.Json.JsonSerializer.Serialize(StandingAgent.Recording(Evidence, 1))}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }
              }
            }
            """,
            Ct);

        return (NodeCatalogue.ReadFrom(path), identity);
    }

    private static async Task<string> StartAsync(NodeCatalogue catalogue, AgentIdentity identity, TurnReporter? reporter)
    {
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var said = new StringWriter();

        await NodeCommand.StartAsync(
            new Wire.StartAgent { Agent = Billing.Value, Because = "asked", Identity = identity.ToString() },
            catalogue, "alpha", residents, said, Ct, reporter);

        Assert.Empty(residents);

        return said.ToString();
    }

    [Fact]
    public async Task A_workspace_gone_under_a_folder_still_there_retires_the_agent_and_starts_nothing()
    {
        Assert.SkipUnless(NodeCommand.OnTheHomeVolume(_root), "This machine's temporary folder is not on the home disk.");

        var workspace = Path.Combine(_root, "checkout");
        var (catalogue, identity) = await CatalogueAsync(workspace);
        Directory.Delete(workspace, recursive: true);

        using var connection = ParleyDatabase.OpenInMemory();
        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Billing, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active), Ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var channel = GrpcChannel.ForAddress(broker.Address);
        var nodes = new Wire.Nodes.NodesClient(channel);
        using var join = nodes.Join(
            new Wire.JoinRequest { Machine = NodeCatalogue.Machine, StartableRoles = { "billing" } }, cancellationToken: Ct);
        Assert.True(await join.ResponseStream.MoveNext(Ct), "the node was not acknowledged");

        var said = await StartAsync(catalogue, identity, new TurnReporter(nodes, NodeCatalogue.Machine, TextWriter.Null));

        Assert.Contains("is gone, so the broker retired it", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");
        Assert.Equal(
            AgentStatus.Retired,
            (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing).Status);
    }

    [Fact]
    public async Task A_workspace_reached_through_a_link_whose_far_side_is_gone_concludes_nothing()
    {
        // Lens 2 run 89: the folder above is a link to a second disk, and the disk is gone. The link itself is still there,
        // so the parent check alone would retire every agent on that disk at once.
        var volume = Path.Combine(_root, "other-volume");
        _ = Directory.CreateDirectory(volume);
        var repos = Path.Combine(_root, "repos");

        try
        {
            Directory.CreateSymbolicLink(repos, volume);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let a test create a directory link.");
        }

        var (catalogue, identity) = await CatalogueAsync(Path.Combine(repos, "checkout"));
        Directory.Delete(volume, recursive: true);
        Assert.True(Directory.Exists(repos) || new DirectoryInfo(repos).LinkTarget is not null, "the link should outlive its target");

        var said = await StartAsync(catalogue, identity, reporter: null);

        // Which check says so differs by platform: Windows still sees the dangling link as a folder that cannot be listed,
        // so it holds nothing; Unix reads it as missing, so the folder above is missing (lens 3 runs 97 and 98). Either way
        // nothing is concluded. The link check itself is held by the test after this one.
        Assert.Contains("nothing is concluded", said, StringComparison.Ordinal);
        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");

        RemoveLink(repos);
    }

    [Fact]
    public async Task A_workspace_missing_beyond_a_link_to_a_folder_in_use_concludes_nothing()
    {
        // The folder above is a link to a folder that is there and holds other things: every other check passes, so only
        // the link check stands between it and a retire, on every platform.
        var volume = Path.Combine(_root, "other-volume");
        _ = Directory.CreateDirectory(Path.Combine(volume, "billing"));
        var repos = Path.Combine(_root, "repos");

        try
        {
            Directory.CreateSymbolicLink(repos, volume);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let a test create a directory link.");
        }

        var (catalogue, identity) = await CatalogueAsync(Path.Combine(repos, "checkout"));
        Directory.Delete(Path.Combine(volume, "checkout"), recursive: true);

        var said = await StartAsync(catalogue, identity, reporter: null);

        Assert.Contains("runs through a link", said, StringComparison.Ordinal);
        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);

        RemoveLink(repos);
    }

    /// <summary>Removes a directory link as itself, never what it leads to.</summary>
    /// <remarks>
    /// On Unix a link whose far side is gone reads as missing to <see cref="Directory.Delete(string)"/>, which then throws
    /// (lens 3 run 98); a file removal unlinks it there. Windows removes a directory link as a directory.
    /// </remarks>
    private static void RemoveLink(string link)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(link);
        }
        else
        {
            File.Delete(link);
        }
    }

    [Fact]
    public async Task A_workspace_that_is_itself_a_link_to_something_gone_is_never_reported_gone()
    {
        // The workspace is a link to a drive that is not mounted, beside other things in a folder that is there: Unix reads
        // it as missing and the parent as in use, so only the link check stands between it and a retire. Windows reads a
        // dangling directory link as present, so the workspace is not missing there and the node tries to start it.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows reads a dangling directory link as a folder that is there.");

        var volume = Path.Combine(_root, "usb");
        var workspace = Path.Combine(_root, "orders");
        _ = Directory.CreateDirectory(Path.Combine(volume, "orders"));

        try
        {
            Directory.CreateSymbolicLink(workspace, Path.Combine(volume, "orders"));
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let a test create a directory link.");
        }

        var (catalogue, identity) = await CatalogueAsync(workspace);
        Directory.Delete(volume, recursive: true);

        var said = await StartAsync(catalogue, identity, reporter: null);

        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");

        RemoveLink(workspace);
    }

    [Fact]
    public async Task A_workspace_missing_from_a_folder_that_holds_nothing_else_concludes_nothing()
    {
        // Lens 1 run 87: an unmounted Linux volume leaves its mount folder behind, empty, and a mount point is no link.
        var mount = Path.Combine(_root, "mnt-vol");
        var (catalogue, identity) = await CatalogueAsync(Path.Combine(mount, "checkout"));
        Directory.Delete(Path.Combine(mount, "checkout"), recursive: true);
        Assert.Empty(Directory.EnumerateFileSystemEntries(mount));

        var said = await StartAsync(catalogue, identity, reporter: null);

        Assert.Contains("holds nothing else", said, StringComparison.Ordinal);
        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");
    }

    [Fact]
    public async Task A_workspace_missing_from_a_drives_root_concludes_nothing_though_the_drive_holds_other_things()
    {
        // Lens 2 run 90: another disk took the removable drive's letter, so the root is there and holds things, and nothing
        // on the path is a link. A drive letter onto a folder plays the disk; the owner's mapped letters are never used.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");

        var disk = Path.Combine(_root, "disk");
        _ = Directory.CreateDirectory(Path.Combine(disk, "photos"));
        var letter = Enumerable.Range('K', 'Y' - 'K' + 1).Select(c => (char)c)
            .Where(c => c is not 'M' and not 'R' and not 'V')
            .Select(c => $"{c}:")
            .First(l => QueryDosDevice(l, new char[65536], 65536) == 0
                && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 2);
        var definition = $@"\??\{disk}";
        Assert.True(DefineDosDevice(RawTargetPath, letter, definition));

        try
        {
            var (catalogue, identity) = await CatalogueAsync($@"{letter}\orders");
            Directory.Delete(Path.Combine(disk, "orders"), recursive: true);

            var said = await StartAsync(catalogue, identity, reporter: null);

            Assert.Contains("nothing is concluded", said, StringComparison.Ordinal);
            Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
        }
        finally
        {
            DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, letter, definition);
        }
    }

    [Fact]
    public async Task A_workspace_missing_from_a_folder_of_the_same_name_on_another_disk_concludes_nothing()
    {
        // Lens 1 run 89: another disk took the letter and has a Projects folder of its own, in use. Only the home-disk rule
        // tells it from the disk the repository is still on.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters");

        var diskA = Path.Combine(_root, "diskA");
        var diskB = Path.Combine(_root, "diskB");
        _ = Directory.CreateDirectory(Path.Combine(diskA, "Projects", "inventory"));
        _ = Directory.CreateDirectory(Path.Combine(diskB, "Projects", "notes"));
        var letter = Enumerable.Range('K', 'Y' - 'K' + 1).Select(c => (char)c)
            .Where(c => c is not 'M' and not 'R' and not 'V')
            .Select(c => $"{c}:")
            .First(l => QueryDosDevice(l, new char[65536], 65536) == 0
                && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 2);
        var first = $@"\??\{diskA}";
        var second = $@"\??\{diskB}";
        Assert.True(DefineDosDevice(RawTargetPath, letter, first));
        var defined = first;

        try
        {
            var (catalogue, identity) = await CatalogueAsync($@"{letter}\Projects\orders");
            Assert.True(DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, letter, first));
            defined = null;
            Assert.True(DefineDosDevice(RawTargetPath, letter, second));
            defined = second;

            var said = await StartAsync(catalogue, identity, reporter: null);

            Assert.Contains("home folder", said, StringComparison.Ordinal);
            Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(diskA, "Projects", "orders")), "the repository is still on its own disk");
        }
        finally
        {
            if (defined is not null)
            {
                DefineDosDevice(RawTargetPath | RemoveDefinition | ExactMatchOnRemove, letter, defined);
            }
        }
    }

    private const uint RawTargetPath = 0x1;
    private const uint RemoveDefinition = 0x2;
    private const uint ExactMatchOnRemove = 0x4;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string targetPath);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, uint length);

    [Fact]
    public async Task An_empty_workspace_in_the_catalogue_concludes_nothing_and_does_not_stop_the_node()
    {
        // Lens 1 run 88: a hand-edited "workspace": "" reached Path.GetFullPath("") and threw out of the node's loop, which
        // stopped every agent on the machine.
        var (catalogue, identity) = await CatalogueAsync(Path.Combine(_root, "checkout"));
        var path = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        var text = await File.ReadAllTextAsync(path, Ct);
        var workspaceJson = System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "checkout"));
        await File.WriteAllTextAsync(path, text.Replace(workspaceJson, "\"\"", StringComparison.Ordinal), Ct);
        catalogue = NodeCatalogue.ReadFrom(path);

        var said = await StartAsync(catalogue, identity, reporter: null);

        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_and_the_folder_above_both_missing_conclude_nothing()
    {
        var parent = Path.Combine(_root, "volume");
        var (catalogue, identity) = await CatalogueAsync(Path.Combine(parent, "checkout"));
        Directory.Delete(parent, recursive: true);

        var said = await StartAsync(catalogue, identity, reporter: null);

        Assert.Contains("both missing, so nothing is concluded", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");
    }
}
