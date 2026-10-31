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
/// That a node joining a broker tells it the launch token of every agent it still runs, so a broker that restarted knows
/// those processes are Parley's when they reattach (SPEC.md §11.7).
/// </summary>
/// <remarks>
/// Found by review, lens 1 run 104. A broker keeps launches in memory, so after a restart every process its node had
/// started reattached as open outside Parley: no wake reached it and a person's start was refused, until it was killed.
/// Through the node's own loop, so the call on joining is what is held, not only the report.
/// </remarks>
[Integration]
public sealed class ARejoiningNodeNamesItsRunningAgentsTests : IAsyncDisposable
{
    private const string Token = "before-the-restart";

    private static readonly AgentName Billing = AgentName.Create("billing@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-rejoin-" + Path.GetRandomFileName());

    public ARejoiningNodeNamesItsRunningAgentsTests()
    {
        // The broker after its restart: it has heard of no launch.
        _broker = LoopbackOperator.Start(_store);
        _ = Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_process_started_before_the_broker_restarted_is_Parleys_once_its_node_rejoins(bool nodeRejoins)
    {
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(cataloguePath, """{ "agents": {} }""", Ct);

        // The process the node started before the restart, with the token it was given then.
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        await using var resident = ResidentAgent.Start(
            Billing,
            InvocationCommand.Parse(StandingAgent.Command),
            workspace,
            TimeProvider.System,
            new Dictionary<string, string> { [LaunchToken.Variable] = Token });
        residents[AgentIdentity.Mint()] = resident;
        Assert.True(resident.IsAlive, "the agent never started, so this probe measured nothing");

        using var channel = GrpcChannel.ForAddress(_broker.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var node = nodeRejoins
            ? NodeCommand.RunAsync(channel, _broker.Address, cataloguePath, TextWriter.Null, null, residents, stopping.Token)
            : Task.FromResult(0);

        try
        {
            // The resident's adapter reattaching with the token in its environment, until the node has rejoined.
            var outside = true;

            for (var tries = 0; tries < 50 && outside; tries++)
            {
                outside = await ReattachReadsOutsideAsync(channel);

                if (outside)
                {
                    await Task.Delay(200, Ct);
                }
            }

            Assert.Equal(!nodeRejoins, outside);
        }
        finally
        {
            await stopping.CancelAsync();

            try
            {
                _ = await node;
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                       || ex is RpcException { StatusCode: StatusCode.Cancelled })
            {
            }
        }
    }

    private async Task<bool> ReattachReadsOutsideAsync(GrpcChannel channel)
    {
        using var attached = new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = Billing.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                LaunchToken = Token,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        return (await new BrokerClient(_broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing).OpenOutside;
    }
}
