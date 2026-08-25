using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent gets back when it asks for something the protocol cannot accept.
/// </summary>
/// <remarks>
/// <para>
/// Every case here reached a schema constraint and came back as a <c>SqliteException</c> naming
/// a <c>GLOB</c>. The schema is right to hold these — §9.3 puts invariants in storage because
/// application code routes around them — but a constraint is not a way to talk to a model, and
/// an agent handed one will most often respond by sending the same thing again.
/// </para>
/// <para>
/// These are also not attacks. They are the mistakes of an agent working: a peer named with a
/// capital letter, two sub-questions that ended up sharing an id, a gate left pointing at a
/// question that was edited away, an outcome word not quite remembered. What matters is that
/// each has an obvious correction, and that the refusal carries it.
/// </para>
/// </remarks>
public sealed class MalformedInputTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy.";

    private static void AssertMalformed(JsonNode result, params string[] mustMention)
    {
        Assert.False(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("malformed", result["refused"]!.GetValue<string>());

        var why = result["why"]!.GetValue<string>();

        foreach (var fragment in mustMention)
        {
            Assert.Contains(fragment, why, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<(ParleyTools Orders, ParleyTools Billing)> PairAsync(
        ServerFixture fixture, CancellationToken ct)
    {
        var (_, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        return (orders, billing);
    }

    private static Task<JsonNode> Ask(
        ParleyTools tools, string to, SubQuestion[] subQuestions, string? conversationId,
        CancellationToken ct) =>
        tools.AskAsync(to, "Deciding where the tax rate lives.", subQuestions, Consequences,
            conversationId, cancellationToken: ct);

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b/c")]
    [InlineData("..")]
    [InlineData("a:b")]
    public async Task A_conversation_id_carrying_path_syntax_is_refused(string conversationId)
    {
        // §10.2 exports a discussion by name, so this string reaches a filename. The schema
        // constraint meant to stop that permitted '/' — its comment claimed to keep path
        // separators out while the GLOB admitted them, so '../../etc/passwd' was a legal
        // conversation id. A guarantee written down, believed, and not enforced.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Ask(orders, "billing", [new SubQuestion("q1", "gross or net?")], conversationId, ct),
            "conversation id");
    }

    [Fact]
    public async Task An_ordinary_conversation_id_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        var asked = await Ask(orders, "billing", [new SubQuestion("q1", "gross or net?")], "tax-rate", ct);

        Assert.True(asked["ok"]!.GetValue<bool>(), asked.ToJsonString());
    }

    [Theory]
    [InlineData("Billing")]
    [InlineData("")]
    [InlineData("billing; DROP TABLE agents")]
    public async Task A_peer_name_the_grammar_does_not_admit_is_refused_with_the_rule(string peer)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Ask(orders, peer, [new SubQuestion("q1", "gross or net?")], null, ct),
            "lowercase", "parley_agents");
    }

    [Fact]
    public async Task Addressing_yourself_is_refused_rather_than_throwing()
    {
        // The broker refuses this with an exception, because an exchange between an agent and
        // itself is a programming error at that layer. Reached from a tool it is a model
        // mistaking who owns something, which deserves a sentence rather than a stack trace.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Ask(orders, "orders", [new SubQuestion("q1", "gross or net?")], null, ct),
            "yourself");
    }

    [Theory]
    [InlineData("parley:broker")]
    [InlineData("PARLEY:anything")]
    public async Task An_agent_claiming_the_reserved_responder_namespace_is_refused(string claimed)
    {
        // §11.3 makes this a MUST and the broker enforces it, because it is the one place the
        // distinction still exists — every row is written by the broker, so no CHECK can tell it
        // writing its own name from it recording what an adapter handed it. What the broker
        // cannot do is answer an agent: it throws, and this is an agent-supplied tool parameter.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await orders.NotifyAsync(
                "billing", "the invoice schema changed", responderModel: claimed, cancellationToken: ct),
            "reserved");
    }

    [Theory]
    [InlineData("model with spaces")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task A_responder_identity_the_grammar_does_not_admit_is_refused(string claimed)
    {
        // The identity is rendered into the §13 envelope header, which is the broker-authored
        // region the payload delimiter does not defend — so the character set and the length
        // bound are both trust-boundary controls rather than tidiness.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await orders.NotifyAsync(
                "billing", "the invoice schema changed", responderModel: claimed, cancellationToken: ct),
            "responder identity");
    }

    [Fact]
    public async Task A_real_responder_identity_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var sent = await orders.NotifyAsync(
            "billing", "the invoice schema changed", responderModel: "script:contracts-lookup",
            cancellationToken: ct);

        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());

        // Found by sender rather than by position. Nothing else should be in this inbox, but an
        // assertion that reads index zero fails with an index error when that assumption breaks,
        // which says nothing about what actually arrived.
        var delivered = (await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray();
        var note = Assert.Single(delivered, m => m!["from"]!.GetValue<string>() == "orders");

        Assert.Contains(
            "script:contracts-lookup", note!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_with_no_sub_questions_is_refused()
    {
        // It would hold the pair slot and burn a working window while asking nothing, and the
        // peer receiving it would have nothing it could answer.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(await Ask(orders, "billing", [], null, ct), "at least one");
    }

    [Fact]
    public async Task Sub_questions_sharing_an_id_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Ask(orders, "billing",
                [new SubQuestion("q1", "gross or net?"), new SubQuestion("q1", "or something else?")],
                null, ct),
            "q1");
    }

    [Fact]
    public async Task A_gate_pointing_at_nothing_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Ask(orders, "billing",
                [new SubQuestion("q1", "gross or net?", GateOn: "q7", GateEquals: "yes")], null, ct),
            "q7");
    }

    [Fact]
    public async Task An_outcome_that_is_not_one_is_refused_rather_than_read_as_unknown()
    {
        // It used to be coerced, quietly. §15 counts 'unknown' to tell a healthy deployment from
        // one in trouble, so a typo became indistinguishable from a peer that genuinely could not
        // answer — and the more agents typed it, the worse the deployment would appear to be.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var asked = await Ask(orders, "billing", [new SubQuestion("q1", "gross or net?")], null, ct);
        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                exchangeId, [new Resolution("q1", "resolvedish", "sort of")], cancellationToken: ct),
            "answered", "unknown", "out_of_scope");

        // Still answerable afterwards, or the refusal is a dead end rather than a correction.
        var second = await billing.AnswerAsync(
            exchangeId, [new Resolution("q1", "answered", "Net.")], cancellationToken: ct);

        Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());
    }

    [Fact]
    public async Task Claiming_a_question_was_ghosted_is_refused_by_name()
    {
        // §5.3: the one resolution no agent may write. Coercing it to 'unknown' would answer an
        // attempt at forgery with a shrug, and leave §15 counting it as an ordinary failure.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var asked = await Ask(orders, "billing", [new SubQuestion("q1", "gross or net?")], null, ct);
        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                asked["exchange_id"]!.GetValue<long>(),
                [new Resolution("q1", "ghosted", "nobody came back")], cancellationToken: ct),
            "broker");
    }

    [Fact]
    public async Task A_referral_to_something_that_is_not_an_agent_is_refused()
    {
        // §8 pushes an agent towards referring rather than guessing, so this field is reached
        // often — and it names a peer, so it lands in a column holding the agent grammar.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var asked = await Ask(orders, "billing", [new SubQuestion("q1", "gross or net?")], null, ct);
        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                asked["exchange_id"]!.GetValue<long>(),
                [new Resolution("q1", "out_of_scope", "not mine", RedirectTo: ["parley:broker"])],
                cancellationToken: ct),
            "parley_agents");
    }
}
