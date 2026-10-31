using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a checkout enrolled in two projects, one process for both, is known as Parley's in each as soon as either starts
/// it (SPEC.md §11.7, §12).
/// </summary>
/// <remarks>
/// Found by review, lens 1 run 107, and older than the rule that a token lasts across reconnects. Only the project whose
/// node launched the process was told its token, so the other read it as open outside Parley: its wakes asked nobody and
/// a person's start was refused, until that project's node happened to join again. Through two real node loops sharing
/// one table, as <c>serve</c> runs them.
/// </remarks>
[Integration]
public sealed class ACheckoutInTwoProjectsIsParleysInBothTests : IAsyncDisposable
{
    private static readonly AgentName InAlpha = AgentName.Create("billing@" + NodeCatalogue.Machine);
    private static readonly AgentName InBeta = AgentName.Create("invoices@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _alphaStore = ParleyDatabase.OpenInMemory();
    private readonly SqliteConnection _betaStore = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _alpha;
    private readonly LoopbackOperator _beta;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-two-projects-" + Path.GetRandomFileName());

    public ACheckoutInTwoProjectsIsParleysInBothTests()
    {
        _alpha = LoopbackOperator.Start(_alphaStore);
        _beta = LoopbackOperator.Start(_betaStore);
        _ = Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync().ConfigureAwait(false);
        await _beta.DisposeAsync().ConfigureAwait(false);
        _alphaStore.Dispose();
        _betaStore.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_process_one_project_started_is_Parleys_in_the_other_too(bool refreshed)
    {
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "invoices", "PARLEY_PROJECT": "beta" } }
              },
              "parleyAgentId": "{{identity}}"
            }
            """,
            Ct);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            "{ \"agents\": { \"" + identity + "\": { \"command\": "
            + System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(workspace) + " } } }",
            Ct);

        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address);
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var alphaNode = NodeCommand.RunAsync(alphaChannel, _alpha.Address, cataloguePath, TextWriter.Null, "alpha", residents, stopping.Token);
        var betaNode = NodeCommand.RunAsync(betaChannel, _beta.Address, cataloguePath, TextWriter.Null, "beta", residents, stopping.Token);

        try
        {
            // Both nodes joined before alpha starts the checkout, so nothing that joins later explains the result.
            for (var tries = 0; tries < 50 && !(_alpha.Runtime.Nodes.Contains(NodeCatalogue.Machine) && _beta.Runtime.Nodes.Contains(NodeCatalogue.Machine)); tries++)
            {
                await Task.Delay(100, Ct);
            }

            ResidentAgent? started = null;

            if (refreshed)
            {
                // Idle, as a refresh waits for, with an old token: alpha's refresh replaces it with a new one.
                var old = ResidentAgent.Start(
                    InAlpha, InvocationCommand.Parse(StandingAgent.Command), workspace, TimeProvider.System,
                    new Dictionary<string, string> { [LaunchToken.Variable] = "before-the-refresh" });
                residents[identity] = old;

                await NodeCommand.RefreshAsync(
                    new Wire.RefreshAgent { Agent = InAlpha.Value, Identity = identity.ToString(), Because = "a test" },
                    NodeCatalogue.ReadFrom(cataloguePath),
                    "alpha",
                    residents,
                    TextWriter.Null,
                    Ct,
                    new TurnReporter(new Wire.Nodes.NodesClient(alphaChannel), NodeCatalogue.Machine, TextWriter.Null));

                for (var tries = 0; tries < 50 && started is null; tries++)
                {
                    await Task.Delay(200, Ct);
                    started = residents.TryGetValue(identity, out var now) && !ReferenceEquals(now, old) && now.IsAlive ? now : null;
                }

                await old.DisposeAsync();
            }
            else
            {
                for (var tries = 0; tries < 50 && started is null; tries++)
                {
                    _ = _alpha.Runtime.RequestAttention(InAlpha, "a test asks alpha to start it");
                    await Task.Delay(200, Ct);
                    started = residents.Values.FirstOrDefault(resident => resident.IsAlive);
                }
            }

            Assert.NotNull(started);
            var token = Assert.IsType<string>(started.IssuedToken);

            // Beta's adapter in that same process, with the token the process inherited.
            Assert.False(await AttachReadsOutsideAsync(betaChannel, _beta, InBeta, identity, token),
                "beta read alpha's process as open outside Parley");
        }
        finally
        {
            await stopping.CancelAsync();

            foreach (var node in new[] { alphaNode, betaNode })
            {
                try
                {
                    _ = await node;
                }
                catch (Exception ex) when (ex is OperationCanceledException
                                           || ex is RpcException { StatusCode: StatusCode.Cancelled })
                {
                }
            }

            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_checkout_claiming_another_agents_name_does_not_take_its_token()
    {
        // Lens 2 run 111: a second checkout whose launch key claims beta's invoices, started by alpha, was reported to
        // beta under that name and took the token of the checkout beta's invoices is, which then read as open outside
        // Parley and was woken by nobody.
        var claimed = Path.Combine(_root, "invoices");
        var claimant = Path.Combine(_root, "claimant");
        _ = Directory.CreateDirectory(claimed);
        _ = Directory.CreateDirectory(claimant);
        var identity = AgentIdentity.Mint();

        await File.WriteAllTextAsync(
            Path.Combine(claimant, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } },
                "parley-beta": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "invoices", "PARLEY_PROJECT": "beta" } }
              },
              "parleyAgentId": "{{identity}}"
            }
            """,
            Ct);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            "{ \"agents\": { \"" + identity + "\": { \"command\": "
            + System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(claimant) + " } } }",
            Ct);

        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address);
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var alphaNode = NodeCommand.RunAsync(alphaChannel, _alpha.Address, cataloguePath, TextWriter.Null, "alpha", residents, stopping.Token);
        var betaNode = NodeCommand.RunAsync(betaChannel, _beta.Address, cataloguePath, TextWriter.Null, "beta", residents, stopping.Token);

        try
        {
            for (var tries = 0; tries < 50 && !(_alpha.Runtime.Nodes.Contains(NodeCatalogue.Machine) && _beta.Runtime.Nodes.Contains(NodeCatalogue.Machine)); tries++)
            {
                await Task.Delay(100, Ct);
            }

            // Beta's invoices, a checkout of its own, attached as the process beta's node started.
            Assert.True(_beta.Runtime.LaunchReported(NodeCatalogue.Machine, InBeta, "beta-started-it"));
            using var invoices = new Wire.Agents.AgentsClient(betaChannel).Attach(
                new Wire.AttachRequest
                {
                    Agent = InBeta.Value,
                    Adapter = "test",
                    Tier = Wire.AgentTier.Pull,
                    Workspace = claimed,
                    Identity = AgentIdentity.Mint().ToString(),
                    LaunchToken = "beta-started-it",
                    ProtocolVersion = ProtocolVersion.Current,
                },
                cancellationToken: Ct);
            Assert.True(await invoices.ResponseStream.MoveNext(Ct));
            Assert.False(await OpenOutsideAsync(_beta, InBeta));

            ResidentAgent? started = null;

            for (var tries = 0; tries < 50 && started is null; tries++)
            {
                _ = _alpha.Runtime.RequestAttention(InAlpha, "a test asks alpha to start it");
                await Task.Delay(200, Ct);
                started = residents.Values.FirstOrDefault(resident => resident.IsAlive);
            }

            Assert.NotNull(started);

            // The report reaches beta just after the start; long enough for it to have landed.
            for (var tries = 0; tries < 10; tries++)
            {
                await Task.Delay(200, Ct);
                Assert.False(await OpenOutsideAsync(_beta, InBeta), "the claimant took beta's invoices' token");
            }
        }
        finally
        {
            await stopping.CancelAsync();

            foreach (var node in new[] { alphaNode, betaNode })
            {
                try
                {
                    _ = await node;
                }
                catch (Exception ex) when (ex is OperationCanceledException
                                           || ex is RpcException { StatusCode: StatusCode.Cancelled })
                {
                }
            }

            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    private static async Task<bool> OpenOutsideAsync(LoopbackOperator broker, AgentName name) =>
        (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == name).OpenOutside;

    private static async Task<bool> AttachReadsOutsideAsync(
        GrpcChannel channel, LoopbackOperator broker, AgentName name, AgentIdentity identity, string token)
    {
        using var attached = new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = name.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Identity = identity.ToString(),
                LaunchToken = token,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        return (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == name).OpenOutside;
    }
}
