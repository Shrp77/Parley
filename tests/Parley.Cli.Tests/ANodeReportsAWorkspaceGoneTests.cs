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
