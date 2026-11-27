using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Machines joining, and a Request reaching an agent that was not running.
/// </summary>
/// <remarks>
/// SPEC.md §5.5 says a Request may be addressed to a registered agent whether or not it is running, and that one
/// which is not gets started. Without a node that is a promise with nothing behind it: the Request is admitted and
/// sits there, flagged, until somebody happens to open the repository.
/// </remarks>
[Integration]
public sealed class NodeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_request_to_an_idle_agent_asks_its_machine_to_start_it()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        // Registered on alpha, then gone — which is every agent whose editor is closed.
        var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await infra.DisposeAsync();

        // Gone as far as the broker knows, not only as far as this client knows: until its handler unwinds infra still
        // looks attached, so the Request is a nudge to a closing stream and no start is sent. Lost once on macOS.
        await broker.WaitUntilDetachedAsync("infra@alpha", Patience);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        Assert.True(Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra@alpha", "who owns the retry policy"), cancellationToken: token)).Outcomes).Ok);

        // The machine was told, over the connection it already held. Nothing dialled anything.
        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal("infra@alpha", command.Start.Agent);
        Assert.Contains("docs@alpha", command.Start.Because, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_agent_is_given_a_turn_rather_than_started_again()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        // Infra is running because its node started it (SPEC.md §11.7), so its node is the one to give it a turn.
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token, startedBy: node);

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns the retry policy"), cancellationToken: token);

        // It was nudged, because its stream is open.
        Assert.Equal(1, (await infra.WaitForNoticeAsync(Patience)).MessagesWaiting);

        // And not launched a second time — but the machine is still told, because what the Request needs is a turn.
        // The node gives the turn only to a resident of its own, so an agent attached through somebody's editor
        // window is left alone rather than duplicated.
        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, command.CommandCase);
        Assert.Equal("infra@alpha", command.Turn.Agent);
        Assert.Contains("docs@alpha", command.Turn.Because, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_further_request_after_the_agent_has_collected_asks_for_another_turn()
    {
        // The defect this guards was not that the first Request failed. It was that the second did, silently, for
        // as long as the session lived. Requests arriving before the agent has read the first share its turn;
        // once it has read them, the next one asks again.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        // Started by its node (SPEC.md §11.7): an agent opened outside Parley would be asked for nothing at all.
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token, startedBy: node);

        foreach (var subject in new[] { "the retry policy", "the realm", "the secrets" })
        {
            _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, $"who owns {subject}"), cancellationToken: token);

            var command = await node.WaitForCommandAsync(Patience);

            Assert.Equal(Wire.NodeCommand.CommandOneofCase.Turn, command.CommandCase);
            Assert.Equal("infra@alpha", command.Turn.Agent);

            _ = await infra.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);
        }
    }

    [Fact]
    public async Task A_role_the_node_did_not_offer_is_not_started()
    {
        // The node lists what it can launch, and this one cannot launch infra. §4 keeps how to start a repository
        // on the machine it lives on, so the broker knows only whether to send a command — never what it would run.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await infra.DisposeAsync();

        // Otherwise infra may still look attached, and no command is sent for that reason rather than this one.
        await broker.WaitUntilDetachedAsync("infra@alpha", Patience);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra@alpha", "who owns the retry policy"), cancellationToken: token);

        await Assert.ThrowsAsync<TimeoutException>(async () => await node.WaitForCommandAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_second_node_for_one_machine_is_refused()
    {
        // Two supervisors on one box would each receive the same start command and race to launch the same agent.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token));

        Assert.Equal(StatusCode.AlreadyExists, refused.StatusCode);
    }

    [Fact]
    public async Task A_machine_is_reachable_while_its_node_is_and_not_after()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);
        Assert.Contains("alpha", broker.Runtime.Nodes);

        await node.DisposeAsync();

        // Bounded wait rather than an immediate assertion: the broker forgets the machine when its own handler
        // unwinds, which is a separate event on a separate thread.
        await broker.WaitUntilNodeGoneAsync("alpha", TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("alpha", broker.Runtime.Nodes);
    }

    [Fact]
    public async Task A_node_reports_what_it_observed_and_who_lives_there()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await infra.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(
            broker, "alpha", ["infra"], token, new Wire.NodeFacts { Os = "Windows", OsVersion = "11", Domain = "corp.example" });

        var described = await node.Nodes.DescribeAsync(new Wire.DescribeNodeRequest { Machine = "alpha" }, cancellationToken: token);

        Assert.True(described.Connected);
        Assert.Equal("Windows", described.Facts.Os);
        Assert.Equal("corp.example", described.Facts.Domain);
        Assert.Contains("infra@alpha", described.Agents);
    }
}
