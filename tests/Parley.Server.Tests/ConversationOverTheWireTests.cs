using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// A whole exchange carried over a real socket, per SPEC.md §4 and §4.0.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the architecture exists for. Everything else in the repository can pass
/// while the broker is a file two processes both open — which is the deployment §4 calls corrupt
/// rather than slow, and which no test that constructs a broker directly can tell apart from a
/// working one.
/// </para>
/// <para>
/// So: two clients, two channels, one listener, and a question that goes from one to the other
/// and comes back answered. Nothing here touches a <c>Broker</c> or a <c>SqliteConnection</c>.
/// </para>
/// </remarks>
public sealed class ConversationOverTheWireTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private const string Consequences =
        "If the answer is yes I will record it in BOUNDARIES.md and stop asking; if no I will " +
        "raise it with the operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    private static string Answer(string text) =>
        $$"""{"answers":[{"id":"q1","text":"{{text}}","confidence":"high"}]}""";

    [Fact]
    public async Task A_question_reaches_a_peer_and_its_answer_comes_back()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        await using var infra = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the retry policy")
            },
            cancellationToken: token);

        Assert.True(asked.Ok);
        Assert.Equal(infra.Name, asked.Answerer);

        // The broker spoke first. This is the whole claim of §4.0: nothing polled, nothing timed
        // out, and the notice arrived because a socket was already open.
        var notice = await infra.WaitForNoticeAsync(Patience);
        Assert.Equal(1, notice.MessagesWaiting);

        // And it carried no part of what was said. §13: a peer's words reach a model wrapped as
        // data, and the envelope is applied by the drain -- so a notice that carried the text
        // would be a second path for it with no envelope on it.
        Assert.DoesNotContain("retry", notice.ToString(), StringComparison.OrdinalIgnoreCase);

        var inbox = await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);

        var delivered = Assert.Single(inbox.Messages);
        Assert.Equal(docs.Name, delivered.Message.From);
        Assert.Contains("retry policy", delivered.Message.PayloadJson, StringComparison.Ordinal);
        Assert.NotEmpty(delivered.Envelope);

        var answered = await infra.Client.AnswerAsync(
            new Wire.AnswerRequest
            {
                From = infra.Name,
                ExchangeId = asked.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = Answer("infra owns it, in terraform/retry.tf"),

                // Said rather than left unset, and the omission was worth catching: the broker
                // refuses an unspecified resolution instead of reading it as "answered". That is
                // the most consequential of the four and the one an unset field is least likely
                // to have meant, so a default there would put a claim in an answerer's mouth.
                Resolution = Wire.Resolution.Answered
            },
            cancellationToken: token);

        Assert.True(answered.Ok);

        var back = await docs.WaitForNoticeAsync(Patience);
        Assert.Equal(1, back.MessagesWaiting);

        var reply = await docs.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: token);

        var answer = Assert.Single(reply.Messages);
        Assert.Equal(Wire.MessageType.Answer, answer.Message.Type);
        Assert.Contains("terraform/retry.tf", answer.Message.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_is_reachable_because_its_stream_is_open_and_not_otherwise()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/repo-docs", token);

        Assert.Contains("docs@alpha", broker.Runtime.Attached);

        // §4.0: the stream ending is the agent going away, however it ends. Nothing is renewed
        // and nothing lapses, so there is no lease to wait out -- but the two sides are
        // different processes, so the broker learns of it when its handler unwinds rather than
        // when the client returns from disposing. Bounded, because that gap is a moment and a
        // gap that grew would be the defect.
        await docs.DisposeAsync();

        await broker.WaitUntilDetachedAsync("docs@alpha", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_question_to_an_agent_that_is_not_attached_is_still_opened()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        // Attached, then gone -- which leaves it registered. §4.1.1: a name outlives a
        // connection, and §5.5 asks a machine that is switched off precisely so its silence
        // arrives as an orphan the asker is told about rather than an answer nobody missed.
        var infra = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/repo-infra", token);
        await infra.DisposeAsync();

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = "infra@beta",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns the domain controller")
            },
            cancellationToken: token);

        Assert.True(asked.Ok);
        Assert.Equal("infra@beta", asked.Answerer);
    }

    [Fact]
    public async Task An_operation_before_an_attach_is_refused()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        var stranger = broker.Connect();

        // Not an authorisation check, and it must not be read as one: §16 says the wire cannot
        // tell who is calling. It says only that no stream is open under that name, and there is
        // therefore no broker to run the operation against -- one invented for the call would
        // have a ghosting clock starting now.
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            async () => await stranger.ListAgentsAsync(
                new Wire.ListAgentsRequest { AskingAs = "billing@alpha" },
                cancellationToken: token));

        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refused.StatusCode);
    }

    [Fact]
    public async Task A_second_repository_cannot_take_a_name_that_is_held()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-one", token);

        // §4.1.1. Two checkouts on one machine agree about the role, the machine, and therefore
        // the whole name; without this they share a registry row and an identity, and the symptom
        // is a peer receiving replies about a repository it never asked about.
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(
            async () => await AttachedClient.AttachAsync(
                broker, "docs@alpha", "C:/repo-two", token));

        Assert.Equal(Grpc.Core.StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("C:/repo-one", refused.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_question_opens_one_exchange_per_machine_and_wakes_each()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "C:/repo-docs", token);

        await using var alpha = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        await using var beta = await AttachedClient.AttachAsync(
            broker, "infra@beta", "C:/repo-infra", token);

        var asked = await docs.Client.AskRoleAsync(
            new Wire.AskRoleRequest
            {
                From = docs.Name,
                Role = "infra",
                ResponderModel = "some-model-9",
                PayloadJson = Question("what does this machine believe about DNS")
            },
            cancellationToken: token);

        Assert.Equal(2, asked.Outcomes.Count);
        Assert.All(asked.Outcomes, o => Assert.True(o.Ok));

        // Both woken, and independently. §5.5 makes a role question N ordinary exchanges, so
        // each member hears about its own and nothing binds them but the group.
        Assert.Equal(1, (await alpha.WaitForNoticeAsync(Patience)).MessagesWaiting);
        Assert.Equal(1, (await beta.WaitForNoticeAsync(Patience)).MessagesWaiting);

        // The same repository on two machines, which is exactly the case §4.1.1 exists for -- and
        // is not a name collision, because the machine half tells them apart.
        Assert.Equal(
            ["infra@alpha", "infra@beta"],
            asked.Outcomes.Select(o => o.Answerer).Order(StringComparer.Ordinal));
    }
}
