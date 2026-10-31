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
/// That a node joining a broker tells it the launch token of every agent it still runs for that project, so a broker
/// that restarted knows those processes are Parley's when they reattach (SPEC.md §11.7).
/// </summary>
/// <remarks>
/// <para>
/// Found by review, lens 1 run 104. A broker keeps launches in memory, so after a restart every process its node had
/// started reattached as open outside Parley: no wake reached it and a person's start was refused, until it was killed.
/// Through the node's own loop, so the call on joining is what is held, not only the report.
/// </para>
/// <para>
/// <b>Only this project's agents</b> (lens 2 run 107). The table of running agents is the machine's, shared by every
/// project one process hosts, and another project's agent may carry the same name from another checkout; reported
/// here, its token took the place of this project's, which then read as open outside.
/// </para>
/// </remarks>
[Integration]
public sealed class ARejoiningNodeNamesItsRunningAgentsTests : IAsyncDisposable
{
    private const string Project = "beta";
    private const string Ours = "before-the-restart";
    private const string Theirs = "from-another-project";

    private static readonly AgentName Billing = AgentName.Create("billing@" + NodeCatalogue.Machine);

    /// <summary>What this project calls the other checkout, which the project that started it calls billing.</summary>
    private static readonly AgentName Orders = AgentName.Create("orders@" + NodeCatalogue.Machine);

    /// <summary>What a person renamed it to since its node launched it (lens 1 run 105).</summary>
    private static readonly AgentName Sales = AgentName.Create("sales@" + NodeCatalogue.Machine);

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
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    public async Task A_process_started_before_the_broker_restarted_is_Parleys_once_its_node_rejoins(
        bool nodeRejoins, bool renamed, bool otherAlsoHere)
    {
        // Two identities, ordered as the shared table lists them: the other project's goes last, so that reporting it
        // here would leave its token in place of this project's whatever order a table keeps.
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var order = new ConcurrentDictionary<AgentIdentity, int> { [AgentIdentity.Mint()] = 0, [AgentIdentity.Mint()] = 0 };
        var identity = order.Keys.First();
        var elsewhere = order.Keys.Last();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-{{Project}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "{{Project}}" } }
              },
              "parleyAgentId": "{{identity}}"
            }
            """,
            Ct);

        // The other checkout. When it is enrolled here too, it is billing in the project that started it and orders in
        // this one (lens 2 run 108): reported under the name it was started with, it took this project's billing's place.
        var elsewhereWorkspace = Path.Combine(_root, "other-checkout");
        _ = Directory.CreateDirectory(elsewhereWorkspace);

        await File.WriteAllTextAsync(
            Path.Combine(elsewhereWorkspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": {
                "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } },
                "parley-{{Project}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "orders", "PARLEY_PROJECT": "{{Project}}" } }
              },
              "parleyAgentId": "{{elsewhere}}"
            }
            """,
            Ct);

        var command = System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command);
        var alsoHere = otherAlsoHere
            ? ", \"" + elsewhere + "\": { \"command\": " + command + ", \"workspace\": "
              + System.Text.Json.JsonSerializer.Serialize(elsewhereWorkspace) + " }"
            : string.Empty;

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": {{command}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }{{alsoHere}}
              }
            }
            """,
            Ct);

        // The machine's table: this project's process, and another project's under the same name from another checkout,
        // each with the token it was started with.
        await using var resident = Start(workspace, Ours);
        await using var other = Start(elsewhereWorkspace, Theirs);
        residents[identity] = resident;
        residents[elsewhere] = other;
        Assert.Equal(elsewhere, residents.Keys.Last());
        Assert.True(resident.IsAlive && other.IsAlive, "the agents never started, so this probe measured nothing");

        using var channel = GrpcChannel.ForAddress(_broker.Address);

        // Registered with its identity; when renamed since its launch, the broker knows it as sales and the node still as
        // billing.
        var name = renamed ? Sales : Billing;
        (await AttachAsync(channel, name, identity.ToString(), null)).Dispose();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var node = nodeRejoins
            ? NodeCommand.RunAsync(channel, _broker.Address, cataloguePath, TextWriter.Null, Project, residents, stopping.Token)
            : Task.FromResult(0);

        try
        {
            // This project's adapter reattaching with its token, until the node has rejoined.
            var outside = true;

            for (var tries = 0; tries < 50 && outside; tries++)
            {
                outside = await ReattachReadsOutsideAsync(channel, name, Ours, identity.ToString());

                if (outside)
                {
                    await Task.Delay(200, Ct);
                }
            }

            Assert.Equal(!nodeRejoins, outside);

            // And the other project's token is not this project's.
            Assert.True(
                await ReattachReadsOutsideAsync(channel, name, Theirs, identity.ToString()),
                "another project's process passed for this one's");

            // Enrolled here too, the other checkout is known here under this project's name for it.
            if (nodeRejoins && otherAlsoHere)
            {
                Assert.False(
                    await ReattachReadsOutsideAsync(channel, Orders, Theirs, elsewhere.ToString()),
                    "the checkout this project calls orders was not known as Parley's under that name");
            }
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

    private static ResidentAgent Start(string workspace, string token) =>
        ResidentAgent.Start(
            Billing,
            InvocationCommand.Parse(StandingAgent.Command),
            workspace,
            TimeProvider.System,
            new Dictionary<string, string> { [LaunchToken.Variable] = token });

    private async Task<bool> ReattachReadsOutsideAsync(GrpcChannel channel, AgentName name, string token, string identity)
    {
        using var attached = await AttachAsync(channel, name, identity, token);

        return (await new BrokerClient(_broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == name).OpenOutside;
    }

    private static async Task<AsyncServerStreamingCall<Wire.Event>> AttachAsync(
        GrpcChannel channel, AgentName name, string identity, string? token)
    {
        var attached = new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = name.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Identity = identity,
                LaunchToken = token ?? string.Empty,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        return attached;
    }
}
