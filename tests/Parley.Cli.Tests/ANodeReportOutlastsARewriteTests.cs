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

/// <summary>That a node's report held up past its deadline by the broker rewriting its store still lands (SPEC.md §11.6).</summary>
/// <remarks>
/// Found by review, lens 1 run 115: a rewrite holds every call behind the broker's one gate, seconds on a large store, and
/// a report that missed its five seconds was dropped. A launch dropped then left the agent open outside Parley; a turn's
/// end, a wake that never came. The broker is held here for longer than one deadline, as a rewrite would hold it.
/// </remarks>
[Integration]
public sealed class ANodeReportOutlastsARewriteTests : IAsyncDisposable
{
    private static readonly AgentName Billing = AgentName.Create("billing@" + NodeCatalogue.Machine);

    private readonly SqliteConnection _store = ParleyDatabase.OpenInMemory();
    private readonly LoopbackOperator _broker;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parley-outlasts-" + Path.GetRandomFileName());

    public ANodeReportOutlastsARewriteTests()
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

    [Theory]
    [InlineData("once")]
    [InlineData("restarted")]
    [InlineData("reannounced")]
    public async Task A_launch_reported_while_the_broker_is_held_past_the_deadline_is_filed_once_it_is_free(string how)
    {
        var restarted = how != "once";

        var cataloguePath = Path.Combine(_root, NodeCatalogue.DefaultFileName);
        await File.WriteAllTextAsync(cataloguePath, "{ \"agents\": {} }", Ct);

        using var channel = GrpcChannel.ForAddress(_broker.Address);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var node = NodeCommand.RunAsync(
            channel, _broker.Address, cataloguePath, TextWriter.Null, null,
            new ConcurrentDictionary<AgentIdentity, ResidentAgent>(), stopping.Token);

        try
        {
            for (var tries = 0; tries < 50 && !_broker.Runtime.Nodes.Contains(NodeCatalogue.Machine); tries++)
            {
                await Task.Delay(100, Ct);
            }

            // Registered with its identity, so the report goes through the read that waits on the gate.
            var identity = AgentIdentity.Mint();
            var workspace = Path.Combine(_root, "billing");
            _ = Directory.CreateDirectory(workspace);
            using (var first = AttachAsync(channel, identity, workspace, token: null))
            {
                Assert.True(await first.ResponseStream.MoveNext(Ct));
            }

            for (var tries = 0; tries < 50 && _broker.Runtime.Attached.Contains(Billing.Value); tries++)
            {
                await Task.Delay(100, Ct);
            }

            // Held for longer than a report's deadline, as a rewrite of a large store holds it: twice as long when the
            // process is restarted meanwhile, so the first report has been tried again by then.
            var holding = new TaskCompletionSource();
            var hold = _broker.Runtime.UseConnectionAsync(
                async (_, _, ct) =>
                {
                    holding.SetResult();
                    await Task.Delay(TimeSpan.FromSeconds(restarted ? 12 : 7), ct);
                    return 0;
                },
                Ct);
            await holding.Task;

            var reporter = new TurnReporter(new Wire.Nodes.NodesClient(channel), NodeCatalogue.Machine, TextWriter.Null);

            if (how == "reannounced")
            {
                // Lens 3 run 130: the case above hands the retry its own check. Here a rejoin's re-announce of a real
                // process asks the node's own (TurnReporter.Current), and the process ends while the report waits.
                await using var old = ResidentAgent.Start(
                    Billing, InvocationCommand.Parse(StandingAgent.Command), workspace, TimeProvider.System,
                    new Dictionary<string, string> { [LaunchToken.Variable] = "before-the-restart" });
                var announcing = reporter.ReannounceAsync([(identity, old, "billing")]);

                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                await old.DisposeAsync();
                await reporter.ReportRunningAsync(identity, "billing", "launched-during-the-hold", workspace, () => true);
                await announcing;
            }
            else if (restarted)
            {
                // Lens 2 run 119: the process a rejoin re-announced is replaced while the report waits, and the new one
                // is reported too. Tried again after that, the old token was filed last and the new process read as
                // open outside Parley.
                var replaced = false;
                var old = reporter.ReportRunningAsync(identity, "billing", "before-the-restart", workspace, () => !replaced);

                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                replaced = true;
                await reporter.ReportRunningAsync(identity, "billing", "launched-during-the-hold", workspace, () => true);
                await old;
            }
            else
            {
                await reporter.ReportRunningAsync(identity, "billing", "launched-during-the-hold", workspace, () => true);
            }

            _ = await hold;

            using var again = AttachAsync(channel, identity, workspace, "launched-during-the-hold");
            Assert.True(await again.ResponseStream.MoveNext(Ct));

            Assert.False(
                (await new BrokerClient(_broker.Operators).RegistryAsync(Ct)).Single(agent => agent.Name == Billing).OpenOutside,
                "the launch reported while the broker was held was dropped");
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

    private static AsyncServerStreamingCall<Wire.Event> AttachAsync(
        GrpcChannel channel, AgentIdentity identity, string workspace, string? token) =>
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
}
