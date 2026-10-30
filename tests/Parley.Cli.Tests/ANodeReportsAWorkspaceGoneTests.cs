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
/// That a node asked to start an agent whose workspace is gone starts nothing, and has the broker retire it only when the
/// folder above is still there (SPEC.md §14).
/// </summary>
/// <remarks>
/// The folder above is the discriminator: a deleted repository is worth a judgement, a drive briefly unmounted is not,
/// and one unavailable volume must not retire every agent on it at once.
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

        // Which check says so differs by platform: Windows still sees the dangling link as a folder, so the link check does;
        // Unix reads it as missing, so the parent check does (lens 3 run 97). Either way nothing is concluded.
        Assert.Contains("nothing is concluded", said, StringComparison.Ordinal);
        Assert.DoesNotContain("is gone", said, StringComparison.Ordinal);
        Assert.False(File.Exists(Evidence), "something was launched for a workspace that is gone");

        // The link alone, never recursively: its far side is gone, but a link is removed as itself.
        Directory.Delete(repos);
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

        Directory.Delete(workspace);
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
