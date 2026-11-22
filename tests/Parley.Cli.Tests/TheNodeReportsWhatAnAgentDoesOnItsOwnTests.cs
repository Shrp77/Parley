using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.Sqlite;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Headless;
using Parley.Headless.Tests;
using Parley.Store;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Cli.Tests;

/// <summary>
/// That a node tells every project that starts a checkout what its process does on its own: the background work it ends
/// a turn with, and the turn it begins itself when that work finishes (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// Found by review: the resident agent's half and the broker's half were each held, and nothing held the node's wiring
/// between them. With no project told, every test passed, and an agent with background work read as idle. Through two
/// real node loops sharing one table, as <c>serve</c> runs them, a scripted runner writing what Claude Code writes, and
/// each project's registry as an operator reads it.
/// </remarks>
[Integration]
public sealed class TheNodeReportsWhatAnAgentDoesOnItsOwnTests : IAsyncDisposable
{
    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";

    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";

    private const string Finished = """{"type":"system","subtype":"task_notification","task_id":"b1","status":"completed"}""";

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly AgentName InAlpha = AgentName.Create("billing@" + NodeCatalogue.Machine);
    private static readonly AgentName InBeta = AgentName.Create("invoices@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _alphaStore = ParleyDatabase.OpenInMemory();
    private readonly SqliteConnection _betaStore = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _alpha;
    private readonly LoopbackOperator _beta;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-own-work-" + Path.GetRandomFileName());

    public TheNodeReportsWhatAnAgentDoesOnItsOwnTests()
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

    [Fact]
    public async Task Background_work_and_the_agents_own_turn_reach_every_project_that_starts_the_checkout()
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

        // One given turn: it starts a background task and ends while the task runs; the task finishes, and the agent
        // begins a turn of its own, with nobody's prompt, and ends it. Each pause is a state the registry is read in.
        var command = StubRunner.Scripted(
            Path.Combine(_root, "runner"),
            [OneTask, Started, StubRunner.Pause(2), Finished, NoTasks, Init, StubRunner.Pause(2), Done]);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            "{ \"agents\": { \"" + identity + "\": { \"command\": "
            + System.Text.Json.JsonSerializer.Serialize(command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(workspace) + " } } }",
            Ct);

        // A report is taken only about an agent the broker knows, and the first comes before anything could attach.
        await new AgentRepository(_alphaStore).UpsertAsync(
            new AgentRecord(InAlpha, "test", AgentTier.Headless, workspace, null, null, false, AgentStatus.Active, Identity: identity), Ct);
        await new AgentRepository(_betaStore).UpsertAsync(
            new AgentRecord(InBeta, "test", AgentTier.Headless, workspace, null, null, false, AgentStatus.Active, Identity: identity), Ct);

        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();

        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address);
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var alphaNode = NodeCommand.RunAsync(alphaChannel, _alpha.Address, cataloguePath, TextWriter.Null, "alpha", residents, stopping.Token);
        var betaNode = NodeCommand.RunAsync(betaChannel, _beta.Address, cataloguePath, TextWriter.Null, "beta", residents, stopping.Token);

        try
        {
            await UntilAsync(
                () => Task.FromResult(_alpha.Runtime.Nodes.Contains(NodeCatalogue.Machine) && _beta.Runtime.Nodes.Contains(NodeCatalogue.Machine)),
                "both nodes never joined");

            // Asked once: a second asking would be a second given turn, and the turn after the first must be the agent's own.
            await UntilAsync(
                () => Task.FromResult(_alpha.Runtime.RequestAttention(InAlpha, "a test asks alpha to start it")),
                "alpha's node was never asked to start the checkout");

            ResidentAgent? started = null;

            await UntilAsync(
                () => Task.FromResult((started = residents.Values.FirstOrDefault(resident => resident.IsAlive && resident.IssuedToken is not null)) is not null),
                "the node never started the checkout");

            // Each project's adapter in that one process, with the token it inherited: a count is shown only for a process
            // Parley started.
            var token = Assert.IsType<string>(started!.IssuedToken);
            using var inAlpha = await AttachAsync(alphaChannel, InAlpha, identity, token);
            using var inBeta = await AttachAsync(betaChannel, InBeta, identity, token);

            // (a) The given turn has ended and the task it started runs on: waiting on its background work, not idle.
            var alphaBefore = await UntilItReadsAsync(
                _alpha, InAlpha, agent => agent.Turns is { RunningSince: null, BackgroundTasks: 1 },
                "alpha never read the agent as waiting on one background task, with no turn running");
            var betaBefore = await UntilItReadsAsync(
                _beta, InBeta, agent => agent.Turns is { RunningSince: null, BackgroundTasks: 1 },
                "beta, which did not start the process, never read it as waiting on one background task");

            Assert.Equal(AgentPresence.Running, alphaBefore.Presence);
            Assert.Equal(AgentPresence.Running, betaBefore.Presence);
            Assert.True(alphaBefore.Turns!.Reported);
            Assert.False(alphaBefore.OpenOutside);
            Assert.False(betaBefore.OpenOutside);

            // The given turn was alpha's, so alpha has heard one end. Beta gave no turn: it has heard none yet.
            Assert.NotNull(alphaBefore.Turns.LastEnded);
            Assert.Null(betaBefore.Turns!.LastEnded);

            // (b) The task finished and the agent began a turn nobody gave it: working, with nothing in the background.
            _ = await UntilItReadsAsync(
                _alpha, InAlpha, agent => agent.Turns is { RunningSince: not null, BackgroundTasks: 0 },
                "alpha never read the agent's own turn as running");
            _ = await UntilItReadsAsync(
                _beta, InBeta, agent => agent.Turns is { RunningSince: not null, BackgroundTasks: 0 },
                "beta never read the agent's own turn as running");

            // And its end: no turn running, nothing in the background, and an end later than the given turn's.
            var alphaAfter = await UntilItReadsAsync(
                _alpha, InAlpha,
                agent => agent.Turns is { RunningSince: null, BackgroundTasks: 0 } turns && turns.LastEnded > alphaBefore.Turns.LastEnded,
                "alpha never read the agent's own turn as ended");
            var betaAfter = await UntilItReadsAsync(
                _beta, InBeta, agent => agent.Turns is { RunningSince: null, BackgroundTasks: 0, LastEnded: not null },
                "beta never read the agent's own turn as ended");

            Assert.True(alphaAfter.Turns!.LastClean);
            Assert.True(betaAfter.Turns!.LastClean);
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

    private static async Task UntilAsync(Func<Task<bool>> met, string otherwise)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!await met())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, otherwise);
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<AgentRecord> UntilItReadsAsync(
        LoopbackOperator broker, AgentName name, Func<AgentRecord, bool> met, string otherwise)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (true)
        {
            var row = (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == name);

            if (met(row))
            {
                return row;
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                $"{otherwise}: it read running since {row.Turns?.RunningSince:O}, last ended {row.Turns?.LastEnded:O}, "
                + $"{row.Turns?.BackgroundTasks} in the background, {row.Presence}");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<AsyncServerStreamingCall<Wire.Event>> AttachAsync(
        GrpcChannel channel, AgentName name, AgentIdentity identity, string token)
    {
        var attached = new Wire.Agents.AgentsClient(channel).Attach(
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

        Assert.True(await attached.ResponseStream.MoveNext(Ct), $"{name.Value} was not attached");

        return attached;
    }
}
