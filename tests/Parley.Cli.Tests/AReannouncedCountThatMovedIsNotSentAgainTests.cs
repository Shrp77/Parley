using Grpc.Core;
using Grpc.Core.Interceptors;
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
/// That a rejoin's re-announce of background work is not sent again once the work it counted has ended (SPEC.md §11.6).
/// </summary>
/// <remarks>
/// A report held past its deadline is tried again for a minute. The re-announce counts the background tasks running at the
/// rejoin; if the work ends while that report is held, the pump says so, and a retry landing after it showed work that had
/// ended, with nothing coming to correct it. The retry asks whether the count is still the one it carries. Review, lens 3 run
/// 1 on 6fde4ef3: that check passed every test with it removed. Here the broker's deadline is simulated on the node's side,
/// and the pump is left out, so what the broker shows afterwards is only what the re-announce sent.
/// </remarks>
[Integration]
[Slow]
public sealed class AReannouncedCountThatMovedIsNotSentAgainTests : IAsyncDisposable
{
    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";
    private const string NoTasks = """{"type":"system","subtype":"background_tasks_changed","tasks":[]}""";
    private const string Started = """{"type":"result","subtype":"success","is_error":false,"result":"STARTED","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly AgentName Billing = AgentName.Create("billing@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-reannounce-moved-" + Path.GetRandomFileName());

    public AReannouncedCountThatMovedIsNotSentAgainTests()
    {
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

    [Fact]
    public async Task A_background_count_that_moved_while_its_report_was_held_is_not_sent_again()
    {
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "billing");
        _ = Directory.CreateDirectory(workspace);

        using var channel = GrpcChannel.ForAddress(_broker.Address);

        // Registered with its identity, as an agent that ran before.
        using (var first = Attach(channel, identity, workspace, token: null))
        {
            Assert.True(await first.ResponseStream.MoveNext(Ct));
        }

        for (var tries = 0; tries < 50 && _broker.Runtime.Attached.Contains(Billing.Value); tries++)
        {
            await Task.Delay(100, Ct);
        }

        // The node has joined again: what it said before is forgotten, and it re-announces.
        var nodes = new Wire.Nodes.NodesClient(channel);
        using var joined = nodes.Join(
            new Wire.JoinRequest
            {
                Machine = NodeCatalogue.Machine,
                StartableRoles = { "billing" },
                ProtocolVersion = ProtocolVersion.Current,
                ReportsTurns = true,
            },
            cancellationToken: Ct);

        Assert.True(await joined.ResponseStream.MoveNext(Ct), "the node was never told it joined");

        // One background task, which ends three seconds after the turn that started it.
        var command = StubRunner.Scripted(Path.Combine(_root, "runner"), [OneTask, Started, StubRunner.Pause(3), NoTasks, StubRunner.Pause(60)]);

        await using var resident = ResidentAgent.Start(
            Billing, InvocationCommand.Parse(command), workspace, TimeProvider.System,
            new Dictionary<string, string> { [LaunchToken.Variable] = "t-1" });

        // Its adapter is attached, as the process's own would be.
        using var attached = Attach(channel, identity, workspace, "t-1");
        Assert.True(await attached.ResponseStream.MoveNext(Ct), "billing was not attached");

        var turn = await resident.TakeTurnAsync("1", Patience, Ct);

        Assert.True(turn.Succeeded, turn.Describe());
        Assert.Equal(1, resident.BackgroundTasks);
        var read = resident.Background.Sequence;

        // The broker holds the background report past its deadline until the work has ended, as a rewrite would hold it.
        var held = new HoldsTheFirstBackgroundReport(() => resident.BackgroundTasks == 0);
        var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel.Intercept(held)), NodeCatalogue.Machine, TextWriter.Null);

        await reporter.ReannounceAsync([(identity, resident, "billing")]);

        Assert.Equal(0, resident.BackgroundTasks);
        Assert.True(held.Held, "the background report was never sent, so nothing was held");
        Assert.True(read > 0, "the runner's reading was never numbered");
        Assert.Equal(read, held.Sequence);

        var row = (await new BrokerClient(_broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing);

        Assert.True(
            row.Turns is not { BackgroundTasks: > 0 },
            $"the broker shows {row.Turns?.BackgroundTasks} background tasks after the work ended");
    }

    private static AsyncServerStreamingCall<Wire.Event> Attach(GrpcChannel channel, AgentIdentity identity, string workspace, string? token) =>
        new Wire.Agents.AgentsClient(channel).Attach(
            new Wire.AttachRequest
            {
                Agent = Billing.Value,
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                Workspace = workspace,
                Identity = identity.ToString(),
                LaunchToken = token ?? string.Empty,
                ProtocolVersion = ProtocolVersion.Current,
            },
            cancellationToken: Ct);

    /// <summary>Answers the first background report with a deadline passed, once a condition holds; passes everything else.</summary>
    private sealed class HoldsTheFirstBackgroundReport(Func<bool> until) : Interceptor
    {
        private int _held;

        private long _sequence;

        public bool Held => Volatile.Read(ref _held) > 0;

        /// <summary>When the node read what the held report says.</summary>
        public long Sequence => Interlocked.Read(ref _sequence);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            if (request is Wire.TurnReport { PhaseCase: Wire.TurnReport.PhaseOneofCase.Background } report
                && Interlocked.Increment(ref _held) == 1)
            {
                _ = Interlocked.Exchange(ref _sequence, report.Background.Sequence);
                return new AsyncUnaryCall<TResponse>(
                    TimedOutAsync<TResponse>(),
                    Task.FromResult(new Metadata()),
                    () => new Status(StatusCode.DeadlineExceeded, "held"),
                    () => [],
                    () => { });
            }

            return continuation(request, context);
        }

        private async Task<T> TimedOutAsync<T>()
        {
            var deadline = DateTimeOffset.UtcNow + Patience;

            while (!until() && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "held past its deadline"));
        }
    }
}
