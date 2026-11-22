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
/// That background work still running when the node rejoins its broker is still shown as running (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// A rejoin (the catalogue changed, the stream dropped, the broker restarted) clears what the node said, and the node
/// re-announces the process but not its background tasks, so the agent read as idle until the work ended.
/// </remarks>
[Integration]
public sealed class BackgroundWorkOutlastsARejoinTests : IAsyncDisposable
{
    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";
    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";
    private const string Finished = """{"type":"system","subtype":"task_notification","task_id":"b1","status":"completed"}""";
    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";
    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";
    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","num_turns":1,"total_cost_usd":2,"permission_denials":[],"origin":{"kind":"task-notification","producer":"session-task"}}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly AgentName Billing = AgentName.Create("billing@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _alpha;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-bg-rejoin-" + Path.GetRandomFileName());

    public BackgroundWorkOutlastsARejoinTests()
    {
        _alpha = LoopbackOperator.Start(_store);
        _ = Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _alpha.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Background_work_running_when_the_node_rejoins_is_still_shown()
    {
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            $$"""
            {
              "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "billing", "PARLEY_PROJECT": "alpha" } } },
              "parleyAgentId": "{{identity}}"
            }
            """,
            Ct);

        // The background task runs for twenty seconds: long enough to rejoin and read the registry while it does.
        var command = StubRunner.Scripted(
            Path.Combine(_root, "runner"),
            [OneTask, Started, StubRunner.Pause(20), Finished, NoTasks, Init, StubRunner.Pause(1), Done]);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(cataloguePath, Catalogue(identity, command, workspace, null), Ct);

        await new AgentRepository(_store).UpsertAsync(
            new AgentRecord(Billing, "test", AgentTier.Headless, workspace, null, null, false, AgentStatus.Active, Identity: identity), Ct);

        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var inner = new StringWriter();
        var said = TextWriter.Synchronized(inner);
        string Said() { lock (said) { return inner.ToString(); } }

        using var channel = GrpcChannel.ForAddress(_alpha.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var node = NodeCommand.RunAsync(channel, _alpha.Address, cataloguePath, said, "alpha", residents, stopping.Token);

        try
        {
            await UntilAsync(() => Task.FromResult(_alpha.Runtime.Nodes.Contains(NodeCatalogue.Machine)), "the node never joined");
            await UntilAsync(() => Task.FromResult(_alpha.Runtime.RequestAttention(Billing, "a test asks it to start")), "never asked to start");

            ResidentAgent? started = null;
            await UntilAsync(
                () => Task.FromResult((started = residents.Values.FirstOrDefault(r => r.IsAlive && r.IssuedToken is not null)) is not null),
                "the node never started the checkout");

            using var attached = await AttachAsync(channel, identity, started!.IssuedToken!);

            _ = await UntilItReadsAsync(agent => agent.Turns is { RunningSince: null, BackgroundTasks: 1 }, "never read as waiting on one background task");

            // A second checkout is enrolled on this machine: the catalogue changes and the node rejoins.
            var second = AgentIdentity.Mint();
            _ = Directory.CreateDirectory(workspace + "-other");
            await File.WriteAllTextAsync(
                Path.Combine(workspace + "-other", ".mcp.json"),
                $$"""
                {
                  "mcpServers": { "parley-alpha": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "inventory", "PARLEY_PROJECT": "alpha" } } },
                  "parleyAgentId": "{{second}}"
                }
                """,
                Ct);
            await File.WriteAllTextAsync(cataloguePath, Catalogue(identity, command, workspace, second), Ct);
            await UntilAsync(() => Task.FromResult(Said().Contains("Rejoining with", StringComparison.Ordinal)), "the node never rejoined: " + Said());
            await UntilAsync(() => Task.FromResult(_alpha.Runtime.Nodes.Contains(NodeCatalogue.Machine)), "the node never joined again");

            Assert.True(started.IsAlive && started.BackgroundTasks == 1, "the background task should still be running");

            // Still waiting on its background work, as the process is: not idle.
            _ = await UntilItReadsAsync(
                agent => agent.Turns is { RunningSince: null, BackgroundTasks: 1 },
                "after the rejoin, the agent read as idle while its background task still ran",
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, started.BackgroundTasks);
        }
        finally
        {
            await stopping.CancelAsync();

            try
            {
                _ = await node;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled })
            {
            }

            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    private static string Catalogue(AgentIdentity identity, string command, string workspace, AgentIdentity? other)
    {
        var entry = "{ \"command\": " + System.Text.Json.JsonSerializer.Serialize(command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(workspace) + " }";
        var second = other is { } o
            ? ", \"" + o + "\": { \"command\": \"never\", \"workspace\": " + System.Text.Json.JsonSerializer.Serialize(workspace + "-other") + " }"
            : string.Empty;

        return "{ \"agents\": { \"" + identity + "\": " + entry + second + " } }";
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

    private async Task<AgentRecord> UntilItReadsAsync(Func<AgentRecord, bool> met, string otherwise, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Patience);

        while (true)
        {
            var row = (await new BrokerClient(_alpha.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing);

            if (met(row))
            {
                return row;
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                $"{otherwise}: it read running since {row.Turns?.RunningSince:O}, {row.Turns?.BackgroundTasks} in the background, {row.Presence}");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<AsyncServerStreamingCall<Wire.Event>> AttachAsync(GrpcChannel channel, AgentIdentity identity, string token)
    {
        var attached = new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = Billing.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Identity = identity.ToString(),
                LaunchToken = token,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);

        Assert.True(await attached.ResponseStream.MoveNext(Ct), "billing was not attached");

        return attached;
    }
}

