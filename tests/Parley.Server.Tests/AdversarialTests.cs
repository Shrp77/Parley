using Grpc.Core;
using Xunit;
using Wire = Parley.Transport;

namespace Parley.Server.Tests;

/// <summary>
/// The malicious cases, written and watched rather than reasoned about.
/// </summary>
/// <remarks>
/// <para>
/// CLAUDE.md records that the highest-value findings in this repository came from adversarially
/// probing a guard rather than reading it — a self-referral asserting both halves of a
/// contradiction, a forged <c>parley:broker</c> responder, an agent writing <c>ghosted</c> over
/// its own answered exchanges. None was visible by inspection.
/// </para>
/// <para>
/// The transport adds a whole surface those probes have never covered: every rule that used to
/// be enforced inside one process is now enforced across a socket, by a server that takes the
/// caller's word for who it is. SPEC.md §16 says plainly that it must — the name in a request is
/// a routing address rather than an assertion — so what has to hold is that everything
/// <em>else</em> still does.
/// </para>
/// </remarks>
public sealed class AdversarialTests
{
    private const string Consequences =
        "If the answer is yes I will record it in BOUNDARIES.md and stop asking; if no I will " +
        "raise it with the operator rather than deciding alone.";

    private static string Question(string text) =>
        $$"""{"intent":"{{text}}","sub_questions":[{"id":"q1","text":"{{text}}"}],"consequences":"{{Consequences}}"}""";

    [Fact]
    public async Task An_agent_cannot_answer_an_exchange_it_is_not_party_to()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);
        await using var billing = await AttachedClient.AttachAsync(broker, "billing@alpha", "C:/c", token);

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        // A third agent, attached and legitimate, answering somebody else's exchange. The wire
        // cannot tell who is calling, so this arrives looking exactly like the real answerer —
        // and the refusal has to come from the exchange rather than from the connection.
        var stolen = await billing.Client.AnswerAsync(
            new Wire.AnswerRequest
            {
                From = billing.Name,
                ExchangeId = asked.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = """{"answers":[{"id":"q1","text":"mine","confidence":"high"}]}""",
                Resolution = Wire.Resolution.Answered
            },
            cancellationToken: token);

        Assert.False(stolen.Ok);
        Assert.Equal(Wire.TransitionError.NotAParticipant, stolen.Refusal);
    }

    [Fact]
    public async Task An_asker_cannot_answer_its_own_question()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        var forged = await docs.Client.AnswerAsync(
            new Wire.AnswerRequest
            {
                From = docs.Name,
                ExchangeId = asked.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = """{"answers":[{"id":"q1","text":"me","confidence":"high"}]}""",
                Resolution = Wire.Resolution.Answered
            },
            cancellationToken: token);

        Assert.False(forged.Ok);
        Assert.Equal(Wire.TransitionError.OnlyAnswererMayAnswer, forged.Refusal);
    }

    [Fact]
    public async Task An_agent_cannot_record_its_own_exchange_as_ghosted()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        var asked = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        // Ghosted records that nobody answered, and only the broker writes it. The wire now
        // carries the resolution as a field the client fills in, which is exactly the shape that
        // makes this worth probing rather than assuming.
        var ghosted = await infra.Client.AnswerAsync(
            new Wire.AnswerRequest
            {
                From = infra.Name,
                ExchangeId = asked.ExchangeId,
                ResponderModel = "some-model-9",
                PayloadJson = """{"answers":[{"id":"q1","text":"nothing","confidence":"low"}]}""",
                Resolution = Wire.Resolution.Ghosted
            },
            cancellationToken: token);

        Assert.False(ghosted.Ok);
        Assert.Equal(Wire.TransitionError.GhostIsNotAnAnswer, ghosted.Refusal);
    }

    [Fact]
    public async Task An_adapter_cannot_claim_the_brokers_own_attribution()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        // §11.3 reserves this namespace for the broker's own attributions — a ghost notice, a
        // forced conclusion. An adapter claiming it could forge the one class of message an
        // agent is entitled to believe came from the protocol rather than from a peer.
        //
        // The adapter refuses it at launch, which is a check a hostile client simply skips. This
        // asks whether the broker refuses it too.
        var forged = await Assert.ThrowsAsync<RpcException>(
            async () => await docs.Client.AskAsync(
                new Wire.AskRequest
                {
                    From = docs.Name,
                    To = infra.Name,
                    ResponderModel = "parley:broker",
                    PayloadJson = Question("who owns dns")
                },
                cancellationToken: token));

        Assert.Equal(StatusCode.InvalidArgument, forged.StatusCode);
    }

    [Fact]
    public async Task A_role_question_cannot_be_aimed_at_a_path_or_a_wildcard()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        // The role is a string on the wire and reaches a query. Anything that is not a role must
        // match nothing rather than matching everything — a wildcard that worked would be a
        // primitive for asking every agent in a deployment one question.
        foreach (var aimed in new[] { "%", "*", "_", "infra%", "'", "../infra" })
        {
            var outcomes = await docs.Client.AskRoleAsync(
                new Wire.AskRoleRequest
                {
                    From = docs.Name,
                    Role = aimed,
                    ResponderModel = "some-model-9",
                    PayloadJson = Question("who owns dns")
                },
                cancellationToken: token);

            var only = Assert.Single(outcomes.Outcomes);

            Assert.False(only.Ok, $"'{aimed}' reached somebody");
        }
    }

    [Fact]
    public async Task A_notice_never_carries_a_peers_words()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        // §13's envelope is applied by the drain. A notice that carried any part of the payload
        // would be a second path for the same text with no envelope on it — and the more
        // attractive path, because it arrives sooner. Probed with a distinctive string rather
        // than by reading the message definition, because what matters is what crosses the wire.
        const string Distinctive = "zarquon-seven-four";

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question(Distinctive)
            },
            cancellationToken: token);

        var notice = await infra.WaitForNoticeAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(Distinctive, notice.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, notice.MessagesWaiting);
    }

    [Fact]
    public async Task Draining_somebody_elses_inbox_does_not_hand_over_their_messages()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);
        await using var infra = await AttachedClient.AttachAsync(broker, "infra@alpha", "C:/b", token);

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = infra.Name,
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        // §16 is explicit that this is not prevented and cannot be: the caller says whose inbox
        // to drain and the broker believes it. What is asserted is the honest half — that the
        // inbox handed over is the one that was named, so a caller cannot ask for its own and
        // receive somebody else's. Written down because "it drains what you asked for" is the
        // property, and the alternative would be a routing bug rather than a security control.
        var mine = await docs.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = docs.Name }, cancellationToken: token);

        Assert.Empty(mine.Messages);

        var theirs = await infra.Client.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = infra.Name }, cancellationToken: token);

        Assert.Single(theirs.Messages);
    }

    [Fact]
    public async Task A_node_cannot_be_told_to_start_an_agent_on_another_machine()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        var infra = await AttachedClient.AttachAsync(broker, "infra@beta", "C:/b", token);
        await infra.DisposeAsync();

        // A node on alpha that claims it can start `infra`. There is an infra on beta and none
        // on alpha, and the start command must not go to the wrong box: the node would launch a
        // process that attaches under a name belonging to a machine it is not on.
        await using var alpha = await JoinedNodeClient.JoinAsync(broker, "alpha", ["infra"], token);

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = "infra@beta",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await alpha.WaitForCommandAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Two_windows_on_one_repository_are_both_told_when_mail_arrives()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        // The same agent twice, which SPEC.md §14 treats as two sessions rather than a
        // collision: `connected` counts them, and a person with two editors open on one
        // repository is the ordinary case it exists for.
        await using var first = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/b", token);

        await using var second = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/b", token);

        Assert.Equal(2, broker.Runtime.SessionsFor(Parley.Core.AgentName.Create("infra@alpha")));

        _ = await docs.Client.AskAsync(
            new Wire.AskRequest
            {
                From = docs.Name,
                To = "infra@alpha",
                ResponderModel = "some-model-9",
                PayloadJson = Question("who owns dns")
            },
            cancellationToken: token);

        // Both, and the older one is the point. The runtime held one attachment per name until
        // this was written, so the second attach replaced the first in the lookup and the first
        // window went silent -- still connected, still counted, and never written to again.
        // Nothing failed and nothing logged; the person in that window simply stopped being told
        // they had mail.
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

        // Waited for, because disposing a client returns before the server's handler unwinds --
        // the two are different processes and the removal is the server's to make.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (broker.Runtime.SessionsFor(name) > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, token);
        }

        // One left, and the name still present. Removing the whole entry when either ended would
        // strand the survivor: it would hold an open stream the broker no longer knew about, so
        // every operation it attempted would be refused as not attached.
        Assert.Equal(1, broker.Runtime.SessionsFor(name));
        Assert.Contains("infra@alpha", broker.Runtime.Attached);
    }

    // A version behind, a version ahead, a major ahead, and one that says nothing. "0.3" was in
    // this list until it became the version this build speaks — which is the right way for it to
    // fail: a theory naming a specific foreign version has to be revisited when the number moves,
    // and the alternative is a case that quietly starts asserting the opposite of its name.
    [Theory]
    [InlineData("0.1")]
    [InlineData("0.2")]
    [InlineData("0.4")]
    [InlineData("1.0")]
    [InlineData("")]
    public async Task An_adapter_speaking_another_protocol_is_refused(string announced)
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        var client = broker.Connect();

        // The field was on the wire before anything read it, which is the failure worth pinning:
        // an adapter sent its version on every attach and the broker ignored all of them. A
        // contract that carries a compatibility field and does not enforce it is worse than one
        // that carries none, because a reader concludes they are protected.
        //
        // The empty case is a build from before the field carried anything -- which is 0.1, and
        // 0.1 opened the database directly rather than speaking to a broker at all.
        var stream = client.Attach(
            new Wire.AttachRequest
            {
                Agent = "docs@alpha",
                Adapter = "test",
                Tier = Wire.AgentTier.Pull,
                ProtocolVersion = announced
            },
            cancellationToken: token);

        var refused = await Assert.ThrowsAsync<RpcException>(
            async () => await stream.ResponseStream.MoveNext(token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains(
            Parley.Core.ProtocolVersion.Current, refused.Status.Detail, StringComparison.Ordinal);

        stream.Dispose();
    }

    [Fact]
    public async Task An_adapter_speaking_this_protocol_attaches()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        // The other half, and the one that stops the check above being satisfied by a broker
        // that refuses everybody.
        await using var docs = await AttachedClient.AttachAsync(broker, "docs@alpha", "C:/a", token);

        Assert.Contains("docs@alpha", broker.Runtime.Attached);
    }
    [Fact]
    public async Task One_directory_spelled_two_ways_is_not_two_repositories()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        // The same directory, as two clients would report it. Compared as raw text this was
        // refused with "an agent name belongs to one repository", which sends somebody to retire
        // a registration that is their own -- and the advice in the message is wrong for them.
        await using var first = await AttachedClient.AttachAsync(
            broker, "infra@alpha", "C:/repo-infra", token);

        await using var second = await AttachedClient.AttachAsync(
            broker, "infra@alpha", @"C:\repo-infra", token);

        Assert.Contains("infra@alpha", broker.Runtime.Attached);
    }

    [Fact]
    public async Task Two_directories_differing_only_in_case_do_not_share_a_name()
    {
        var token = TestContext.Current.CancellationToken;

        await using var broker = await LoopbackBroker.StartAsync(token);

        await using var first = await AttachedClient.AttachAsync(
            broker, "docs@alpha", "/src/Archive", token);

        // On a case-sensitive filesystem these are two repositories, and admitting the second
        // gives both one identity -- the §4.1.1 collision, reached without anybody claiming a
        // name that looked taken. It has no symptom where it happens: what somebody eventually
        // sees is a peer receiving replies about a repository it never asked about.
        var refused = await Assert.ThrowsAsync<RpcException>(
            async () => await AttachedClient.AttachAsync(
                broker, "docs@alpha", "/src/archive", token));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Contains("belongs to one repository", refused.Status.Detail, StringComparison.Ordinal);
    }
}
