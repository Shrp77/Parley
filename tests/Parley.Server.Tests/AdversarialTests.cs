using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The malicious cases, written and watched rather than reasoned about.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md records that the highest-value findings in this repository came from adversarially probing a guard
/// rather than reading it — a forged <c>parley:broker</c> responder, an agent writing over its own exchanges. None
/// was visible by inspection.
/// </para>
/// <para>
/// The transport adds a whole surface: every rule enforced inside one process is now enforced across a socket, by
/// a server that takes the caller's word for who it is. SPEC.md §16 says plainly that it must — the name in a
/// request is a routing address rather than an assertion — so what has to hold is that everything else still does.
/// </para>
/// </remarks>
[Integration]
public sealed class AdversarialTests
{
    [Fact]
    public async Task A_participant_cannot_send_the_result_of_an_exchange_it_is_not_party_to()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/c", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns dns"), cancellationToken: token)).Outcomes);

        // A third participant, attached and legitimate, reporting on somebody else's exchange. The wire cannot tell
        // who is calling, so the refusal has to come from the exchange rather than from the connection.
        var stolen = await billing.Client.ResultAsync(Requests.Result(billing.Name, sent.ExchangeId), cancellationToken: token);

        Assert.False(stolen.Ok);
        Assert.Equal(Wire.TransitionError.NotAParticipant, stolen.Refusal);
    }

    [Fact]
    public async Task A_requester_cannot_send_the_result_of_its_own_request()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns dns"), cancellationToken: token)).Outcomes);

        var forged = await docs.Client.ResultAsync(Requests.Result(docs.Name, sent.ExchangeId), cancellationToken: token);

        Assert.False(forged.Ok);
        Assert.Equal(Wire.TransitionError.OnlyReceiverSendsResult, forged.Refusal);
    }

    [Fact]
    public async Task A_participant_cannot_record_its_own_work_as_closed()
    {
        // closed says a person or a consensus ended the work, and only the broker writes it. The outcome is a field
        // the client fills in, which is exactly the shape that makes this worth probing rather than assuming.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var sent = Assert.Single((await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns dns"), cancellationToken: token)).Outcomes);

        var closed = await infra.Client.ResultAsync(Requests.Result(infra.Name, sent.ExchangeId, "closed"), cancellationToken: token);

        Assert.False(closed.Ok);
        Assert.Equal(Wire.TransitionError.ClosedIsTheBrokers, closed.Refusal);
    }

    [Fact]
    public async Task An_adapter_cannot_claim_the_brokers_own_attribution()
    {
        // §11.3 reserves this namespace for the broker's own attributions. The adapter refuses it at launch, which is
        // a check a hostile client simply skips; this asks whether the broker refuses it too.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var request = Requests.Open(docs.Name, infra.Name, "who owns dns");
        request.ResponderModel = "parley:broker";

        var forged = await Assert.ThrowsAsync<RpcException>(async () => await docs.Client.RequestAsync(request, cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, forged.StatusCode);
    }

    [Fact]
    public async Task A_role_request_cannot_be_aimed_at_a_path_or_a_wildcard()
    {
        // The role is a string on the wire and reaches a query. Anything that is not a role must match nothing
        // rather than everything — a wildcard that worked would be a primitive for asking every agent at once.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        foreach (var aimed in new[] { "%", "*", "_", "infra%", "'", "../infra" })
        {
            try
            {
                var outcomes = await docs.Client.RequestAsync(Requests.Open(docs.Name, aimed, "who owns dns"), cancellationToken: token);

                Assert.False(Assert.Single(outcomes.Outcomes).Ok, $"'{aimed}' reached somebody");
            }
            catch (RpcException refused)
            {
                // Refused as a malformed address is as good as refused as nobody: neither reaches anyone.
                Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
            }
        }
    }

    [Fact]
    public async Task A_notice_never_carries_a_participants_words()
    {
        // §13's envelope is applied by the drain. A notice that carried any part of the payload would be a second
        // path for the same text with no envelope on it.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        const string Distinctive = "zarquon-seven-four";

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, Distinctive), cancellationToken: token);

        var notice = await infra.WaitForNoticeAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(Distinctive, notice.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, notice.MessagesWaiting);
    }

    [Fact]
    public async Task Draining_hands_over_the_inbox_that_was_named()
    {
        // §16 is explicit that the caller says whose inbox to drain and the broker believes it. What is asserted is
        // the honest half: a caller asking for its own does not receive somebody else's.
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, infra.Name, "who owns dns"), cancellationToken: token);

        Assert.Empty((await docs.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: token)).Messages);
        Assert.Single((await infra.Client.DrainInboxAsync(new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token)).Messages);
    }

    [Fact]
    public async Task A_node_cannot_be_told_to_start_an_agent_on_another_machine()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/b", token);
        await infra.DisposeAsync();

        // A node on alpha that claims it can start infra. There is an infra on beta and none on alpha, and the start
        // command must not go to the wrong box.
        await using var alpha = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra@beta", "who owns dns"), cancellationToken: token);

        await Assert.ThrowsAsync<TimeoutException>(async () => await alpha.WaitForCommandAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Two_windows_on_one_repository_are_both_told_when_mail_arrives()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        // The same agent twice, which §14 treats as two sessions rather than a collision.
        await using var first = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await using var second = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        Assert.Equal(2, broker.Runtime.SessionsFor(Parley.Core.AgentName.Create("infra@alpha")));

        _ = await docs.Client.RequestAsync(Requests.Open(docs.Name, "infra@alpha", "who owns dns"), cancellationToken: token);

        // Both, and the older one is the point: a lookup holding one attachment per name left the first window
        // silent — still connected, and never written to again.
        Assert.Equal(1, (await first.WaitForNoticeAsync(TimeSpan.FromSeconds(5))).MessagesWaiting);
        Assert.Equal(1, (await second.WaitForNoticeAsync(TimeSpan.FromSeconds(5))).MessagesWaiting);
    }

    [Fact]
    public async Task Closing_one_window_leaves_the_other_attached()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var name = Parley.Core.AgentName.Create("infra@alpha");

        var first = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await using var second = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        await first.DisposeAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (broker.Runtime.SessionsFor(name) > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, token);
        }

        Assert.Equal(1, broker.Runtime.SessionsFor(name));
        Assert.Contains("infra@alpha", broker.Runtime.Attached);
    }

    // A version behind, a version ahead, a major ahead, and one that says nothing. A theory naming a specific foreign
    // version has to be revisited when the number moves; the guard below turns "no exception was thrown" into a
    // message that says the theory is out of date.
    [Theory]
    [InlineData("0.1")]
    [InlineData("0.3")]
    [InlineData("0.4")]
    [InlineData("0.5")]
    [InlineData("0.6")]
    [InlineData("1.0")]
    [InlineData("")]
    public async Task An_adapter_speaking_another_protocol_is_refused(string announced)
    {
        Assert.NotEqual(Parley.Core.ProtocolVersion.Current, announced);

        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var stream = broker.Connect().Attach(
            new Wire.AttachRequest { Agent = "docs@alpha", Adapter = "test", Tier = Wire.AgentTier.Pull, ProtocolVersion = announced },
            cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await stream.ResponseStream.MoveNext(token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains(Parley.Core.ProtocolVersion.Current, refused.Status.Detail, StringComparison.Ordinal);

        stream.Dispose();
    }

    [Fact]
    public async Task An_adapter_speaking_this_protocol_attaches()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        Assert.Contains("docs@alpha", broker.Runtime.Attached);
    }

    [Fact]
    public async Task One_directory_spelled_two_ways_is_not_two_repositories()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/repo-infra", token);
        await using var second = await AttachedClient.AttachAsync(broker, "infra@alpha", @"C:\repo-infra", token);

        Assert.Contains("infra@alpha", broker.Runtime.Attached);
    }

    [Fact]
    public async Task Two_directories_differing_only_in_case_do_not_share_a_name()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await AttachedClient.AttachAsync(broker, "docs@alpha", "/src/Archive", token);

        // On a case-sensitive filesystem these are two repositories, and admitting the second gives both one identity.
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await AttachedClient.AttachAsync(broker, "docs@alpha", "/src/archive", token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("belongs to one repository", refused.Status.Detail, StringComparison.Ordinal);
    }
}
