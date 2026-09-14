using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent gets back when it sends something the protocol cannot accept.
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
/// capital letter, two items that ended up sharing an id, a gate left pointing at an item that
/// was edited away, an outcome word not quite remembered. What matters is that each has an
/// obvious correction, and that the refusal carries it.
/// </para>
/// </remarks>
public sealed class MalformedInputTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string DoneWhen = "the tax rate's owner is named and where it is stored";

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
        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        return (orders, billing);
    }

    private static Task<JsonNode> Request(
        ParleyTools tools, string to, RequestItem[] items, string? conversationId, CancellationToken ct,
        string? responderModel = null) =>
        tools.RequestAsync(to, "Deciding where the tax rate lives.", items, DoneWhen, conversationId,
            responderModel, ct);

    private static async Task<long> OpenAsync(ParleyTools orders, ParleyTools billing, CancellationToken ct)
    {
        var sent = await Request(orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], null, ct);
        await billing.InboxAsync(cancellationToken: ct);

        return sent["exchange_id"]!.GetValue<long>();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b/c")]
    [InlineData("..")]
    [InlineData("a:b")]
    public async Task A_conversation_id_carrying_path_syntax_is_refused(string conversationId)
    {
        // §10.2 exports a conversation by name, so this string reaches a filename. The schema
        // constraint meant to stop that once permitted '/' — its comment claimed to keep path
        // separators out while the GLOB admitted them, so '../../etc/passwd' was a legal
        // conversation id. A guarantee written down, believed, and not enforced.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], conversationId, ct),
            "conversation id");
    }

    [Fact]
    public async Task An_ordinary_conversation_id_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        var sent = await Request(orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], "tax-rate", ct);

        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());
        Assert.Equal("tax-rate", sent["conversation_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_item_id_too_long_for_a_result_to_answer_is_refused_with_the_rule()
    {
        // Storage refuses it too. Refused here first so the model is told what to do in a sentence rather than
        // meeting a constraint's words, and never sends work its receiver could not report on.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test", [new RequestItem(new string('r', 65), "work", "move the rate")], null, ct),
            "short id", "64");
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
            await Request(orders, peer, [new RequestItem("q1", "question", "gross or net?")], null, ct),
            "lower case", "parley_agents");
    }

    [Fact]
    public async Task Sending_a_request_to_yourself_is_refused_rather_than_throwing()
    {
        // The broker refuses this with an exception, because an exchange between an agent and
        // itself is a programming error at that layer. Reached from a tool it is a model
        // mistaking who owns something, which deserves a sentence rather than a stack trace.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "orders@test", [new RequestItem("q1", "question", "gross or net?")], null, ct),
            "yourself");
    }

    [Theory]
    [InlineData("parley:broker")]
    [InlineData("PARLEY:anything")]
    public async Task An_agent_claiming_the_reserved_responder_namespace_is_refused(string claimed)
    {
        // §11.3 makes this a MUST and the broker enforces it, because every row is written by the
        // broker, so no CHECK can tell it writing its own name from it recording what an adapter
        // handed it. What the broker cannot do is answer an agent: it throws, and this is an
        // agent-supplied tool parameter.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], null, ct, claimed),
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
            await Request(orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], null, ct, claimed),
            "responder identity");
    }

    [Fact]
    public async Task A_real_responder_identity_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var sent = await Request(
            orders, "billing@test", [new RequestItem("q1", "question", "gross or net?")], null, ct, "script:contracts-lookup");

        Assert.True(sent["ok"]!.GetValue<bool>(), sent.ToJsonString());

        // Found by sender rather than by position, so a broken assumption about what else is in
        // this inbox fails with what actually arrived rather than an index error.
        var delivered = (await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray();
        var request = Assert.Single(delivered, m => m!["from"]!.GetValue<string>() == "orders@test");

        Assert.Contains(
            "script:contracts-lookup", request!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_with_no_items_is_refused()
    {
        // It would open an exchange asking nothing, and the receiver would owe a Result on no item.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(await Request(orders, "billing@test", [], null, ct), "at least one");
    }

    [Fact]
    public async Task Items_sharing_an_id_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test",
                [new RequestItem("q1", "question", "gross or net?"), new RequestItem("q1", "work", "or something else?")],
                null, ct),
            "q1");
    }

    [Fact]
    public async Task An_item_that_is_neither_work_nor_a_question_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test", [new RequestItem("q1", "task", "move the column")], null, ct),
            "work", "question");
    }

    [Fact]
    public async Task A_gate_pointing_at_nothing_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, _) = await PairAsync(fixture, ct);

        AssertMalformed(
            await Request(orders, "billing@test",
                [new RequestItem("q1", "question", "gross or net?", GateOn: "q7", GateEquals: "done")], null, ct),
            "q7");
    }

    [Fact]
    public async Task An_outcome_that_is_not_one_is_refused_rather_than_read_as_blocked()
    {
        // Coercing it would hide a typo inside §15's count of blocked items, where it is
        // indistinguishable from an agent genuinely stuck — and the more agents typed it, the
        // worse the deployment would appear to be.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var exchangeId = await OpenAsync(orders, billing, ct);

        AssertMalformed(
            await billing.ResultAsync(exchangeId, [new ItemOutcome("q1", "resolvedish", "sort of")], cancellationToken: ct),
            "done", "answered", "acknowledged", "blocked");

        // Still resolvable afterwards, or the refusal is a dead end rather than a correction.
        var second = await billing.ResultAsync(exchangeId, [new ItemOutcome("q1", "answered", "Net.")], cancellationToken: ct);

        Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());
    }

    [Fact]
    public async Task Claiming_an_item_was_closed_is_refused_by_name()
    {
        // §5.3: the one outcome no agent may write. Only closing a conversation records it, and an
        // agent writing it over its own exchange would be forging the broker's hand.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var exchangeId = await OpenAsync(orders, billing, ct);

        AssertMalformed(
            await billing.ResultAsync(exchangeId, [new ItemOutcome("q1", "closed", "nobody came back")], cancellationToken: ct),
            "conversation is closed");
    }

    [Fact]
    public async Task A_blocked_item_must_name_its_blocker()
    {
        // A blocker with no kind is the "someone else should decide" that §8 exists to refuse: it
        // gives the requester nothing to pursue.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var exchangeId = await OpenAsync(orders, billing, ct);

        AssertMalformed(
            await billing.ResultAsync(exchangeId, [new ItemOutcome("q1", "blocked", "stuck")], cancellationToken: ct),
            "missing_information", "conflict", "permission");

        AssertMalformed(
            await billing.ResultAsync(exchangeId, [new ItemOutcome("q1", "answered", "Net.", Blocker: "conflict")], cancellationToken: ct),
            "no blocker");
    }

    [Fact]
    public async Task A_source_that_is_not_an_agent_is_refused()
    {
        // Sources name peers, so they land in a column holding the agent grammar.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);
        var (orders, billing) = await PairAsync(fixture, ct);

        var exchangeId = await OpenAsync(orders, billing, ct);

        AssertMalformed(
            await billing.ResultAsync(
                exchangeId, [new ItemOutcome("q1", "answered", "Net.", Sources: ["parley:broker"])], cancellationToken: ct),
            "sources");
    }
}
