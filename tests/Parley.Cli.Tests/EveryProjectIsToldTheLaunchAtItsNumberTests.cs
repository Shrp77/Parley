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
/// That every project which starts a checkout is told its launch at the number the node took before the process started,
/// at a start and at a restart, so background work the process reads before a project hears of it shows there
/// (SPEC.md §11.6, §11.7).
/// </summary>
/// <remarks>
/// <para>
/// Found by review: the start and the restart each pass that number to every project joined from the table, and telling
/// them at a number taken when each is told instead passed every test. A project told after the runner's first reading
/// holds a launch newer than it, takes the launch as a new process with nothing running, and reads the agent as idle while
/// its work runs.
/// </para>
/// <para>
/// <b>Three projects, and each project's tell held until the reading has reached it.</b> The tells go one project at a time,
/// so the first is numbered before the runner has read anything; only a later one can be numbered after its reading. Each
/// project's channel holds that project's tell (a launch report naming the checkout) until the project has answered a turn
/// report since the launch, which is the runner's reading, so every tell but the first is numbered after it. The runner
/// writes its background work before anybody asks it anything, as a resumed session does, so no turn is needed for it.
/// </para>
/// </remarks>
[Integration]
public sealed class EveryProjectIsToldTheLaunchAtItsNumberTests : IAsyncDisposable
{
    private const string OneTask = """{"type":"system","subtype":"background_tasks_changed","tasks":[{"id":"b1"}]}""";

    private const string Answered = """{"type":"result","subtype":"success","is_error":false,"result":"ok","num_turns":1,"total_cost_usd":1,"permission_denials":[]}""";

    private const string ReportLaunch = "/parley.v1.Nodes/ReportLaunch";

    private const string ReportTurn = "/parley.v1.Nodes/ReportTurn";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly string[] Roles = ["billing", "invoices", "inventory"];

    private static readonly string[] ProjectNames = ["alpha", "beta", "gamma"];

    private readonly SqliteConnection[] _stores = [ParleyDatabase.OpenInMemory(), ParleyDatabase.OpenInMemory(), ParleyDatabase.OpenInMemory()];
    private readonly LoopbackOperator[] _brokers;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-launch-number-" + Path.GetRandomFileName());

    public EveryProjectIsToldTheLaunchAtItsNumberTests()
    {
        _brokers = [.. _stores.Select(store => LoopbackOperator.Start(store))];
        _ = Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var broker in _brokers)
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var store in _stores)
        {
            store.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Background_work_read_before_a_project_is_told_of_the_launch_shows_there_at_a_start_and_a_restart()
    {
        var identity = AgentIdentity.Mint();
        var workspace = Path.Combine(_root, "checkout");
        _ = Directory.CreateDirectory(workspace);
        var names = Roles.Select(role => AgentName.Create(role + "@" + NodeCatalogue.Machine)).ToArray();

        var servers = string.Join(
            ",\n",
            Roles.Select((role, i) =>
                $$"""    "parley-{{ProjectNames[i]}}": { "command": "parley-mcp", "env": { "PARLEY_AGENT": "{{role}}", "PARLEY_PROJECT": "{{ProjectNames[i]}}" } }"""));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".mcp.json"),
            "{\n  \"mcpServers\": {\n" + servers + "\n  },\n  \"parleyAgentId\": \"" + identity + "\"\n}\n",
            Ct);

        // Every process this command starts says it has one task in the background as soon as it is up, and answers each
        // given turn with a result that leaves that task running.
        var command = StubRunner.Opening(Path.Combine(_root, "runner"), [OneTask], [Answered], [Answered]);

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(
            cataloguePath,
            "{ \"agents\": { \"" + identity + "\": { \"command\": "
            + System.Text.Json.JsonSerializer.Serialize(command) + ", \"workspace\": "
            + System.Text.Json.JsonSerializer.Serialize(workspace) + " } } }",
            Ct);

        for (var i = 0; i < _stores.Length; i++)
        {
            await new AgentRepository(_stores[i]).UpsertAsync(
                new AgentRecord(names[i], "test", AgentTier.Headless, workspace, null, null, false, AgentStatus.Active, Identity: identity), Ct);
        }

        var gate = new LaunchGate();
        var handlers = _brokers.Select(_ => new HoldingTells(gate)).ToArray();
        var channels = _brokers
            .Select((broker, i) => GrpcChannel.ForAddress(broker.Address, new GrpcChannelOptions { HttpHandler = handlers[i] }))
            .ToArray();
        var said = _brokers.Select(_ => new Said()).ToArray();
        var residents = new ConcurrentDictionary<AgentIdentity, ResidentAgent>();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var nodes = _brokers
            .Select((broker, i) => NodeCommand.RunAsync(
                channels[i], broker.Address, cataloguePath, said[i].Writer, ProjectNames[i], residents, stopping.Token))
            .ToArray();

        var attached = new List<AsyncServerStreamingCall<Wire.Event>>();

        try
        {
            await UntilAsync(
                () => _brokers.All(broker => broker.Runtime.Nodes.Contains(NodeCatalogue.Machine)),
                "the three nodes never joined");

            await UntilAsync(
                () => _brokers[0].Runtime.RequestAttention(names[0], "a test asks alpha to start it"),
                "alpha's node was never asked to start the checkout");

            // Written once every project has been told, so the readings below are the ones the launch leaves.
            await UntilAsync(() => said[0].Text.Contains(": started (", StringComparison.Ordinal), "alpha's node never started the checkout");

            var started = Assert.Single(residents.Values);
            attached.AddRange(await AttachEveryAsync(channels, names, identity, Assert.IsType<string>(started.IssuedToken)));

            await UntilEachReadsOneTaskAsync(names, "started");

            // A person's restart: the node stops the process once its turn is over and starts another, which reads its task
            // again before any project but alpha has heard of it.
            Assert.Equal(Parley.Server.StopOutcome.Sent, _brokers[0].Runtime.RequestStop(names[0], restart: true, "a test restarts it"));

            await UntilAsync(
                () => said[0].Text.Contains("restarted once its turn was over, as a person asked.", StringComparison.Ordinal),
                "alpha's node never restarted the checkout");

            var restarted = Assert.Single(residents.Values);
            Assert.NotSame(started, restarted);

            foreach (var call in attached)
            {
                call.Dispose();
            }

            attached.Clear();
            attached.AddRange(await AttachEveryAsync(channels, names, identity, Assert.IsType<string>(restarted.IssuedToken)));

            await UntilEachReadsOneTaskAsync(names, "restarted");

            // Each tell was held until its project had the reading, not let go when the hold ran out: a hold that ran out
            // would have let a tell be numbered before the reading, and the test would prove nothing about it.
            Assert.Equal(0, gate.RanOut);
            Assert.True(gate.Held >= 2 * _brokers.Length, $"only {gate.Held} tells were held");
        }
        finally
        {
            foreach (var call in attached)
            {
                call.Dispose();
            }

            await stopping.CancelAsync();

            foreach (var node in nodes)
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

            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private async Task UntilEachReadsOneTaskAsync(AgentName[] names, string when)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        var counts = new int?[_brokers.Length];

        while (true)
        {
            for (var i = 0; i < _brokers.Length; i++)
            {
                var row = (await new BrokerClient(_brokers[i].Operators).RegistryAsync(Ct)).Single(agent => agent.Name == names[i]);
                counts[i] = row.Turns?.BackgroundTasks;
            }

            if (counts.All(count => count == 1))
            {
                return;
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                $"once the checkout had {when}, alpha, beta and gamma read [{string.Join(", ", counts)}] tasks in the background, "
                + "not one each");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task UntilAsync(Func<bool> met, string otherwise)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!met())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, otherwise);
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<List<AsyncServerStreamingCall<Wire.Event>>> AttachEveryAsync(
        GrpcChannel[] channels, AgentName[] names, AgentIdentity identity, string token)
    {
        var calls = new List<AsyncServerStreamingCall<Wire.Event>>();

        for (var i = 0; i < channels.Length; i++)
        {
            var call = new Wire.Agents.AgentsClient(channels[i]).Attach(
                new Wire.AttachRequest
                {
                    Agent = names[i].Value,
                    Adapter = "test",
                    Tier = Wire.AgentTier.Pull,
                    Identity = identity.ToString(),
                    LaunchToken = token,
                    ProtocolVersion = ProtocolVersion.Current,
                },
                cancellationToken: Ct);
            calls.Add(call);

            Assert.True(await call.ResponseStream.MoveNext(Ct), $"{names[i].Value} was not attached");
        }

        return calls;
    }

    /// <summary>What the handlers share: when the last launch was sent, and how the holds went.</summary>
    private sealed class LaunchGate
    {
        private readonly List<HoldingTells> _handlers = [];
        private int _held;
        private int _ranOut;

        public int Held => Volatile.Read(ref _held);

        public int RanOut => Volatile.Read(ref _ranOut);

        public void Add(HoldingTells handler)
        {
            lock (_handlers)
            {
                _handlers.Add(handler);
            }
        }

        /// <summary>A launch is about to be sent: from here on a project has the process's reading only once it answers a turn
        /// report.</summary>
        public void Launching()
        {
            lock (_handlers)
            {
                foreach (var handler in _handlers)
                {
                    handler.Rearm();
                }
            }
        }

        public void CountHeld() => Interlocked.Increment(ref _held);

        public void CountRanOut() => Interlocked.Increment(ref _ranOut);
    }

    /// <summary>
    /// One project's channel: lets a launch through, and holds each tell of a launch (a launch report naming the checkout)
    /// until this project has answered a turn report since the launch.
    /// </summary>
    private sealed class HoldingTells : DelegatingHandler
    {
        private readonly LaunchGate _gate;
        private TaskCompletionSource _turnSinceLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HoldingTells(LaunchGate gate)
            : base(new SocketsHttpHandler())
        {
            _gate = gate;
            gate.Add(this);
        }

        public void Rearm() =>
            Volatile.Write(ref _turnSinceLaunch, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;

            if (string.Equals(path, ReportLaunch, StringComparison.Ordinal) && request.Content is { } content)
            {
                // Read whole and sent on as it was: one framed message, five bytes of header and then the report.
                var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var report = Wire.LaunchReport.Parser.ParseFrom(body.AsSpan(5));
                var copy = new ByteArrayContent(body);

                foreach (var header in content.Headers)
                {
                    _ = copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                request.Content = copy;

                if (report.Identity.Length == 0)
                {
                    // The launch itself, sent before the process starts.
                    _gate.Launching();
                }
                else
                {
                    _gate.CountHeld();

                    try
                    {
                        await Volatile.Read(ref _turnSinceLaunch).Task.WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        _gate.CountRanOut();
                    }
                }
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (string.Equals(path, ReportTurn, StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                _ = Volatile.Read(ref _turnSinceLaunch).TrySetResult();
            }

            return response;
        }
    }

    private sealed class Said
    {
        private readonly StringWriter _text = new();

        public Said() => Writer = TextWriter.Synchronized(_text);

        public TextWriter Writer { get; }

        public string Text
        {
            get
            {
                lock (Writer)
                {
                    return _text.ToString();
                }
            }
        }
    }
}
