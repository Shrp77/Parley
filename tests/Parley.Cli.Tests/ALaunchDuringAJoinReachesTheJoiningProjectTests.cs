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
/// That a launch one project makes of a checkout two projects enrol reaches the other even while the other's node is
/// joining (SPEC.md §11.7).
/// </summary>
/// <remarks>
/// Found by review, lens 1 run 109. A joining loop took its seat on the board only after re-announcing what the table
/// held, and a refresh told the board before putting the new process in the table, so a launch in either gap reached the
/// joining project by neither route and it read the process as open outside Parley until its node next joined. A broker
/// slow to answer one report holds each gap open.
/// </remarks>
[Integration]
public sealed class ALaunchDuringAJoinReachesTheJoiningProjectTests : IAsyncDisposable
{
    private static readonly AgentName InAlpha = AgentName.Create("billing@" + NodeCatalogue.Machine);
    private static readonly AgentName InBeta = AgentName.Create("invoices@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _alphaStore = ParleyDatabase.OpenInMemory();
    private readonly SqliteConnection _betaStore = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _alpha;
    private readonly LoopbackOperator _beta;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-launch-during-join-" + Path.GetRandomFileName());

    public ALaunchDuringAJoinReachesTheJoiningProjectTests()
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
    public async Task A_start_while_the_other_project_re_announces_reaches_it()
    {
        var (catalogue, target, other) = await LayOutAsync();
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        residents[other] = StandIn(AgentName.Create("orders@" + NodeCatalogue.Machine), "other-token");

        // Beta's broker is slow to answer its first report: the re-announce of the checkout beta already runs.
        var betaHandler = new HoldingReport(holdAt: 1);
        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address);
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address, new GrpcChannelOptions { HttpHandler = betaHandler });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var alphaNode = NodeCommand.RunAsync(alphaChannel, _alpha.Address, catalogue, TextWriter.Null, "alpha", residents, stopping.Token);
        await WaitJoinedAsync(_alpha);
        var betaNode = NodeCommand.RunAsync(betaChannel, _beta.Address, catalogue, TextWriter.Null, "beta", residents, stopping.Token);

        try
        {
            await betaHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

            await NodeCommand.StartAsync(
                new Wire.StartAgent { Agent = InAlpha.Value, Identity = target.ToString(), Because = "a test" },
                NodeCatalogue.ReadFrom(catalogue), "alpha", residents, TextWriter.Null, Ct,
                new TurnReporter(new Wire.Nodes.NodesClient(alphaChannel), NodeCatalogue.Machine, TextWriter.Null));

            var token = Assert.IsType<string>(residents[target].IssuedToken);

            betaHandler.Release.TrySetResult();
            await Task.Delay(500, Ct);

            Assert.False(await AttachReadsOutsideAsync(betaChannel, _beta, InBeta, target, token),
                "beta read alpha's process as open outside Parley");
        }
        finally
        {
            betaHandler.Release.TrySetResult();
            await StopAsync(stopping, alphaNode);
            await StopAsync(stopping, betaNode);

            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_refresh_while_the_other_project_joins_reaches_it()
    {
        var (catalogue, target, _) = await LayOutAsync();
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        var old = StandIn(InAlpha, "before-the-refresh");
        residents[target] = old;

        // Alpha's join re-announces the old process (1), the refresh reports its launch (2) and then tells alpha's own
        // seat (3), which its broker is slow to answer while beta joins.
        var alphaHandler = new HoldingReport(holdAt: 3);
        using var alphaChannel = GrpcChannel.ForAddress(_alpha.Address, new GrpcChannelOptions { HttpHandler = alphaHandler });
        using var betaChannel = GrpcChannel.ForAddress(_beta.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var betaStopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var alphaNode = NodeCommand.RunAsync(alphaChannel, _alpha.Address, catalogue, TextWriter.Null, "alpha", residents, stopping.Token);
        await WaitJoinedAsync(_alpha);

        for (var tries = 0; tries < 40 && alphaHandler.Count < 1; tries++)
        {
            await Task.Delay(50, Ct);
        }

        Task<int>? betaNode = null;

        try
        {
            Assert.Equal(1, alphaHandler.Count);

            await NodeCommand.RefreshAsync(
                new Wire.RefreshAgent { Agent = InAlpha.Value, Identity = target.ToString(), Because = "a test" },
                NodeCatalogue.ReadFrom(catalogue), "alpha", residents, TextWriter.Null, Ct,
                new TurnReporter(new Wire.Nodes.NodesClient(alphaChannel), NodeCatalogue.Machine, TextWriter.Null));

            await alphaHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

            // Beta joins now, after alpha has read the board.
            betaNode = NodeCommand.RunAsync(betaChannel, _beta.Address, catalogue, TextWriter.Null, "beta", residents, betaStopping.Token);
            await WaitJoinedAsync(_beta);
            await Task.Delay(500, Ct);

            alphaHandler.Release.TrySetResult();

            ResidentAgent? started = null;

            for (var tries = 0; tries < 50 && started is null; tries++)
            {
                await Task.Delay(100, Ct);
                started = residents.TryGetValue(target, out var now) && !ReferenceEquals(now, old) && now.IsAlive ? now : null;
            }

            Assert.NotNull(started);
            var token = Assert.IsType<string>(started.IssuedToken);
            await Task.Delay(500, Ct);

            Assert.False(await AttachReadsOutsideAsync(betaChannel, _beta, InBeta, target, token),
                "beta read alpha's refreshed process as open outside Parley");
        }
        finally
        {
            alphaHandler.Release.TrySetResult();
            await StopAsync(stopping, alphaNode);

            if (betaNode is not null)
            {
                await StopAsync(betaStopping, betaNode);
            }

            await old.DisposeAsync();

            foreach (var resident in residents.Values)
            {
                await resident.DisposeAsync();
            }
        }
    }

    /// <summary>The checkout both projects enrol, and one only beta does.</summary>
    private async Task<(string Catalogue, AgentIdentity Target, AgentIdentity Other)> LayOutAsync()
    {
        var target = AgentIdentity.Mint();
        var other = AgentIdentity.Mint();
        var entries = new List<string>();

        async Task CheckoutAsync(AgentIdentity identity, string servers)
        {
            var workspace = Path.Combine(_root, identity.ToString());
            _ = Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".mcp.json"),
                "{ \"mcpServers\": {" + servers + "}, \"parleyAgentId\": \"" + identity + "\" }",
                Ct);
            entries.Add("\"" + identity + "\": { \"command\": "
                + System.Text.Json.JsonSerializer.Serialize(StandingAgent.Command) + ", \"workspace\": "
                + System.Text.Json.JsonSerializer.Serialize(workspace) + " }");
        }

        await CheckoutAsync(target,
            "\"parley-alpha\": { \"command\": \"parley-mcp\", \"env\": { \"PARLEY_AGENT\": \"billing\", \"PARLEY_PROJECT\": \"alpha\" } },"
            + "\"parley-beta\": { \"command\": \"parley-mcp\", \"env\": { \"PARLEY_AGENT\": \"invoices\", \"PARLEY_PROJECT\": \"beta\" } }");
        await CheckoutAsync(other,
            "\"parley-beta\": { \"command\": \"parley-mcp\", \"env\": { \"PARLEY_AGENT\": \"orders\", \"PARLEY_PROJECT\": \"beta\" } }");

        var catalogue = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(catalogue, "{ \"agents\": { " + string.Join(", ", entries) + " } }", Ct);

        return (catalogue, target, other);
    }

    private ResidentAgent StandIn(AgentName name, string token) =>
        ResidentAgent.Start(
            name, InvocationCommand.Parse(StandingAgent.Command), _root, TimeProvider.System,
            new Dictionary<string, string> { [LaunchToken.Variable] = token });

    private static async Task WaitJoinedAsync(LoopbackOperator broker)
    {
        for (var tries = 0; tries < 100 && !broker.Runtime.Nodes.Contains(NodeCatalogue.Machine); tries++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Contains(NodeCatalogue.Machine, broker.Runtime.Nodes);
    }

    private static async Task StopAsync(CancellationTokenSource stopping, Task<int> node)
    {
        await stopping.CancelAsync();

        try
        {
            _ = await node;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled })
        {
        }
    }

    private static async Task<bool> AttachReadsOutsideAsync(
        GrpcChannel channel, LoopbackOperator broker, AgentName name, AgentIdentity identity, string token)
    {
        using var detach = CancellationTokenSource.CreateLinkedTokenSource(Ct);
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
            cancellationToken: detach.Token);
        Assert.True(await attached.ResponseStream.MoveNext(Ct));

        var outside = (await new BrokerClient(broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == name).OpenOutside;
        await detach.CancelAsync();

        return outside;
    }

    /// <summary>Holds one launch report until released, as a broker slow to answer it would.</summary>
    private sealed class HoldingReport(int holdAt) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/ReportLaunch", StringComparison.Ordinal)
                && Interlocked.Increment(ref _count) == holdAt)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
