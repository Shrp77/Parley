using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// Machines joining, and a question reaching an agent that was not running.
/// </summary>
/// <remarks>
/// SPEC.md §5.5 says a question may be addressed to a registered agent whether or not it is
/// running, and that one which is not gets started. Until a node process existed that was a
/// promise with nothing behind it — the question was admitted, sat there, and was ghosted.
/// </remarks>
public sealed class NodeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private const string Consequences =
        "If the answer is yes I will record it in BOUNDARIES.md and stop asking; if no I will " +
        "raise it with the operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    [Fact]
    public async Task A_question_to_an_idle_agent_asks_its_machine_to_start_it()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        // Registered on alpha, then gone -- which is every agent whose editor is closed.
        var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await infra.DisposeAsync();

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = "infra@alpha",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the retry policy")
            },
            cancellationToken: token);

        Assert.True(asked.Ok);

        // The machine was told, over the connection it already held. Nothing dialled anything:
        // §4 keeps the broker from ever using an address in the registry as a route.
        var command = await node.WaitForCommandAsync(Patience);

        Assert.Equal("infra@alpha", command.Start.Agent);
        Assert.Contains("docs@alpha", command.Start.Because, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_agent_is_not_started_again()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        await using var infra = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the retry policy")
            },
            cancellationToken: token);

        // It was nudged, because its stream is open.
        Assert.Equal(1, (await infra.WaitForNoticeAsync(Patience)).MessagesWaiting);

        // And not launched a second time. A start command for an agent already running produces
        // a second session for one that needed none, and the duplicate is invisible from the
        // asker's side -- the answer arrives either way.
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await node.WaitForCommandAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_role_the_node_did_not_offer_is_not_started()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await infra.DisposeAsync();

        // The node lists what it can launch, and this one cannot launch infra. §4 keeps how to
        // start a repository on the machine it lives on, so the broker knows only whether to
        // send a command -- never what the command would run.
        await using var node = await JoinedNodeClient.JoinAsync(broker, "alpha", ["billing"], token);

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = "infra@alpha",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the retry policy")
            },
            cancellationToken: token);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await node.WaitForCommandAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_second_node_for_one_machine_is_refused()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        // Two supervisors on one box would each receive the same start command and race to
        // launch the same agent, and nothing downstream would show it.
        var refused = await Assert.ThrowsAsync<RpcException>(
            async () => await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token));

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

        // Bounded wait rather than an immediate assertion. Disposing the client ends the stream;
        // the broker forgets the machine when its own handler unwinds, which is a separate event
        // on a separate thread. Asserting straight afterwards raced the two and lost about one run
        // in three on Linux while passing every time on Windows.
        //
        // The property is kept, not weakened: SPEC.md §4.0 makes a stream ending the machine going
        // away, and "within a moment" is what that means when the two sides are different
        // processes. Asserting it happens synchronously is a promise the transport never made.
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
            broker, "alpha", ["infra"], token,
            new Wire.NodeFacts { Os = "Windows", OsVersion = "11", Domain = "corp.example" });

        var described = await node.Nodes.DescribeAsync(
            new Wire.DescribeNodeRequest { Machine = "alpha" }, cancellationToken: token);

        Assert.True(described.Connected);
        Assert.Equal("Windows", described.Facts.Os);
        Assert.Equal("corp.example", described.Facts.Domain);
        Assert.Contains("infra@alpha", described.Agents);
    }
}
