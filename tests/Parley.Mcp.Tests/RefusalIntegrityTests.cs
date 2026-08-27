using Dapper;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What a refused call leaves behind, and what a fully-featured accepted one does.
/// </summary>
/// <remarks>
/// Two properties that only make sense together. A guard layer is easy to get wrong in
/// opposite directions — writing something before deciding to refuse, or refusing a call that
/// was always valid — and a suite that only ever checks refusals will catch the first and none
/// of the second.
/// </remarks>
public sealed class RefusalIntegrityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy.";

    [Fact]
    public async Task A_refused_call_writes_nothing_at_all()
    {
        // Every refusal shape in one deployment, then a straight count. The failure this
        // guards against has happened here before: an admission that wrote the exchange and
        // then refused the message left an exchange open with no question in it, visible to
        // nobody and answerable by no one. Guards run before the session call for that reason,
        // and this is what says they still do.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        var one = new SubQuestion("q1", "Does an invoice carry the rate at time of sale?");

        await orders.AskAsync("Billing", "intent", [one], Consequences, cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent", [], Consequences, cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent",
            [one, new SubQuestion("q1", "and again?")], Consequences, cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent",
            [new SubQuestion("q1", "a", GateOn: "q9", GateEquals: "yes")], Consequences,
            cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent", [one], Consequences, "../bad", cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent", [one], Consequences,
            responderModel: "parley:broker", cancellationToken: ct);
        await orders.AskAsync("billing@test", "intent",
            [new SubQuestion("q1", new string('x', MessageLimits.MaxPayloadCharacters + 1000))],
            Consequences, cancellationToken: ct);
        await orders.NotifyAsync("orders@test", "talking to myself", cancellationToken: ct);

        var connection = fixture.Connection;

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM exchanges;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM messages;"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM conversations;"));

        // One exception, and it is deliberate: a refusal for length is counted. That is not
        // protocol state — no exchange, no message, no conversation, nothing another agent can
        // observe — and it is the only record anyone will ever have of whether the cap is set
        // anywhere near right. The row holds how long the payload was and never the payload,
        // because keeping the text refused for being too long would retain exactly what the cap
        // exists to keep out.
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM size_refusals;"));
        Assert.Equal(0, connection.ExecuteScalar<long>(
            "SELECT count(*) FROM size_refusals WHERE type <> 'question';"));
    }

    [Fact]
    public async Task Everything_the_tools_offer_at_once_is_still_admitted()
    {
        // The guards are layered, so the risk is not that one of them is wrong but that
        // together they refuse something the protocol allows. This uses every optional feature
        // in one exchange: a gate, a conversation, a slash in a model identifier, a human
        // responder, and a referral.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        var asked = await orders.AskAsync(
            to: "billing@test",
            intent: "Deciding where the tax rate lives.",
            subQuestions:
            [
                new SubQuestion("q1", "Is the rate stamped at time of sale?"),
                new SubQuestion("q2", "And on a refund?", GateOn: "q1", GateEquals: "yes")
            ],
            consequences: Consequences,
            conversationId: "tax-rate",
            responderModel: "some-org/some-model-9",
            cancellationToken: ct);

        Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        var answered = await billing.AnswerAsync(
            exchangeId,
            [
                new Resolution("q1", "answered", "Yes. invoice_lines.tax_rate is stamped at issue."),
                new Resolution("q2", "out_of_scope", "Refunds are not mine.", RedirectTo: ["inventory@test"])
            ],
            responderModel: "human:on-call",
            cancellationToken: ct);

        Assert.True(answered["ok"]!.GetValue<bool>(), answered.ToJsonString());
    }

    [Fact]
    public async Task Referring_a_question_back_to_yourself_is_refused()
    {
        // Both halves of a contradiction in one message: out_of_scope says the boundary is not
        // mine, and the referral says it is. The schema refuses it, which arrived as
        // 'redirect_to names the sender' — true, and not a sentence that helps a model see that
        // it has claimed two opposite things at once.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "out_of_scope", "Not mine.", RedirectTo: ["billing@test"])],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("malformed", refused["refused"]!.GetValue<string>());
    }

    [Fact]
    public async Task Referring_to_a_peer_an_operator_withdrew_is_refused()
    {
        // Stricter than the schema, which asks only that the name be registered. Sending an
        // asker to a retired peer sends it somewhere every operation is refused, and §5.3
        // already has the better word for a question this agent cannot place.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        {
            var connection = fixture.Connection;
            await new AgentRepository(connection)
                .RetireAsync(Parley.Core.AgentName.Create("inventory@test"), ct);
        }

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "out_of_scope", "Refunds are not mine.", RedirectTo: ["inventory@test"])],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Contains("parley_agents", refused["why"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Referring_to_an_agent_that_does_not_exist_is_refused_rather_than_throwing()
    {
        // The referral an agent is most likely to make. §8 tells it to name the owner rather
        // than guess, and §12.3 means an agent exists only once somebody has opened its
        // repository — so the honest answer to "who owns this" is often a name the broker has
        // never heard of. It used to arrive as a SqliteException from tr_referral_targets_exist.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "out_of_scope", "Not mine.", RedirectTo: ["inventory@test"])],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("malformed", refused["refused"]!.GetValue<string>());
        Assert.Contains("parley_agents", refused["why"]!.GetValue<string>(), StringComparison.Ordinal);

        // And still answerable, so the refusal is a correction rather than a dead end.
        var second = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "unknown", "I do not know who owns refunds.")],
            cancellationToken: ct);

        Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());
    }
}
