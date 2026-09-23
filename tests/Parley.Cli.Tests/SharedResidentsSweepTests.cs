using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Parley.Store;
using Xunit;

namespace Parley.Cli.Tests;

/// <summary>
/// That one node loop ending leaves the agents every other node in the process is running alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the review gate's fourth finding, driven.</b> It was found by reading and fixed on a mechanism
/// that is unambiguous, and the ledger recorded a driving test as owed — because a fix for a
/// machine-wide harm that nothing exercises is a fix nobody has watched work.
/// </para>
/// <para>
/// <b>What it guards.</b> 018 gave every project's node loop one shared resident table, so that a checkout
/// answering in two projects is one process. The sweep that sends every agent away ran in each loop's
/// <c>finally</c> — on that shared table. So one project stopping swept the whole machine, including agents
/// still serving other projects, and <c>serve</c> stops and restarts a project whose store or endpoint
/// changed while <c>project remove</c> ends one outright. Either quietly took down everything Parley was
/// running, and SPEC.md §12 says what that costs: the process is what held everything each agent had
/// learned.
/// </para>
/// <para>
/// <b>Two brokers rather than one</b>, because the fixture the ledger said this suite did not have is the
/// whole point: a node loop per broker, sharing one table, is what a process hosting two projects actually
/// is. One loop is cancelled and the other is left alone, which is `project remove` from the surviving
/// project's side.
/// </para>
/// </remarks>
public sealed class SharedResidentsSweepTests : IAsyncDisposable
{
    private readonly SqliteConnection _alphaStore = ParleyDatabase.OpenInMemory();
    private readonly SqliteConnection _betaStore = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _alpha;
    private readonly LoopbackOperator _beta;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "parley-sweep-" + Path.GetRandomFileName());

    public SharedResidentsSweepTests()
    {
        _alpha = LoopbackOperator.Start(_alphaStore);
        _beta = LoopbackOperator.Start(_betaStore);

        _ = Directory.CreateDirectory(_root);
    }

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

    /// <summary>A command that really does stay alive, so that anything killing it is the thing under test.</summary>
    /// <remarks>
    /// <see cref="StandingAgent"/> carries why this is not the obvious string, and why a copy of it that
    /// drifts would not fail but would quietly stop measuring anything.
    /// </remarks>
    private static string Standing => StandingAgent.Command;

    [Fact]
    public async Task One_node_loop_ending_leaves_another_nodes_agents_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");

        _ = Directory.CreateDirectory(workspace);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);

        await File.WriteAllTextAsync(
            cataloguePath,
            $$"""
            {
              "agents": {
                "{{identity}}": {
                  "command": {{System.Text.Json.JsonSerializer.Serialize(Standing)}},
                  "workspace": {{System.Text.Json.JsonSerializer.Serialize(workspace)}}
                }
              }
            }
            """,
            ct);

        // The table the two loops share, holding one live agent — which is the state 018 made ordinary and
        // the state the sweep used to destroy.
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        var resident = ResidentAgent.Start(
            AgentName.Create("orders@" + NodeCatalogue.Machine),
            InvocationCommand.Parse(Standing),
            workspace,
            TimeProvider.System);

        residents[identity] = resident;

        // **Anti-vacuity.** A test whose agent was never running would report this property perfectly while
        // measuring nothing at all.
        Assert.True(resident.IsAlive, "the agent never started, so this probe measured nothing");

        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address);
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address);

        using var stoppingAlpha = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stoppingBeta = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var alphaNode = NodeCommand.RunAsync(
            alphaChannel, _alpha.Address, cataloguePath, TextWriter.Null, "alpha", residents,
            stoppingAlpha.Token);

        var betaNode = NodeCommand.RunAsync(
            betaChannel, _beta.Address, cataloguePath, TextWriter.Null, "beta", residents,
            stoppingBeta.Token);

        // **Alive with both nodes up**, so that anything which kills it below is the stop rather than the
        // fixture. Without this the test cannot tell "the sweep took it" from "the process was never going
        // to survive this long", which is exactly the confound that made the neighbouring probe unsound.
        Assert.True(
            resident.IsAlive,
            "the agent died while both nodes were still running, so the fixture is at fault. "
            + $"What it wrote to standard error: '{resident.Diagnostics()}'");

        // One project stops. This is `project remove`, or `serve` restarting a project whose store changed.
        await stoppingAlpha.CancelAsync();
        await Settled(alphaNode);

        // **The agent belongs to the machine, not to the node that stopped.** Both halves are asserted: the
        // table still holds it, and the process behind it is still there. Checking only the table would pass
        // against a sweep that killed every process and left the entries.
        Assert.True(
            residents.ContainsKey(identity),
            "the stopping node took the shared table's entry with it");

        Assert.True(
            resident.IsAlive,
            "the stopping node swept an agent belonging to a project that is still running");

        await stoppingBeta.CancelAsync();
        await Settled(betaNode);

        await resident.DisposeAsync();
    }

    /// <summary>Waits for a node loop to finish, however it chose to end.</summary>
    /// <remarks>
    /// <para>
    /// A cancelled loop may return a code, raise <see cref="OperationCanceledException"/>, or — because it
    /// is sitting in a streaming gRPC call when the token trips — surface an <see cref="RpcException"/>
    /// carrying <see cref="StatusCode.Cancelled"/>. Which of the three happens is not what this file is
    /// about; what matters is that the loop's <c>finally</c> has run by the time the assertions read the
    /// table.
    /// </para>
    /// <para>
    /// **Only cancellation is swallowed.** Any other status is a node that failed for a reason this test
    /// should report rather than absorb, and absorbing it would let the assertions below pass against a
    /// loop that never ran.
    /// </para>
    /// </remarks>
    private static async Task Settled(Task<int> node)
    {
        try
        {
            _ = await node.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException stopped) when (stopped.StatusCode == StatusCode.Cancelled)
        {
        }
    }
}
