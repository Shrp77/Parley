using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The two rules that decide whether an exchange was worth opening, and whether it was finished.
/// </summary>
/// <remarks>
/// SPEC.md §8 rule 1 — a Request states why it is wanted and what counts as done — and §5.3 — one
/// outcome per item asked. Both are quality rules rather than safety ones, and both reached agents
/// as SQL or not at all, which is the same thing from where the agent is standing.
/// </remarks>
public sealed class RequestFramingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string DoneWhen = "the tax rate's owner is named and where it is stored";

    private static void AssertMalformed(JsonNode result, params string[] mustMention)
    {
        Assert.False(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("malformed", result["refused"]!.GetValue<string>());

        foreach (var fragment in mustMention)
        {
            Assert.Contains(fragment, result["why"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    [InlineData("done")]
    [InlineData("when it works.")]
    public async Task A_request_that_does_not_say_what_done_looks_like_is_refused(string doneWhen)
    {
        // Rule 1 is enforced by a length floor rather than by requiring the field, because "n/a"
        // satisfies a presence check and says nothing about when the receiver stops. Enforced in
        // the schema it arrived as a CHECK naming json_extract — the worst place to answer with
        // SQL, because the agent has not made a syntax mistake. It has failed to say what finished is.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        AssertMalformed(
            await orders.RequestAsync(
                "billing@test", "Deciding where the tax rate lives.",
                [new RequestItem("q1", "question", "Is the rate stamped at time of sale?")],
                doneWhen, cancellationToken: ct),
            "what done looks like");
    }

    [Fact]
    public async Task A_request_with_no_stated_intent_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        AssertMalformed(
            await orders.RequestAsync(
                "billing@test", "  ",
                [new RequestItem("q1", "question", "Is the rate stamped at time of sale?")],
                DoneWhen, cancellationToken: ct),
            "why you want this");
    }

    [Fact]
    public async Task A_result_that_leaves_an_item_without_an_outcome_is_refused()
    {
        // The likelier half of §5.3, and the one that costs most. Resolving the first of two
        // closes the exchange exactly as firmly as resolving both — so the second item is gone,
        // with nothing recording that it was skipped rather than done.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [
                new RequestItem("q1", "question", "Is the rate stamped at time of sale?"),
                new RequestItem("q2", "work", "Stamp it on refunds too.")
            ],
            DoneWhen, cancellationToken: ct);

        var exchangeId = sent["exchange_id"]!.GetValue<long>();
        await billing.InboxAsync(cancellationToken: ct);

        var partial = await billing.ResultAsync(
            exchangeId, [new ItemOutcome("q1", "answered", "Yes, at issue.")], cancellationToken: ct);

        Assert.False(partial["ok"]!.GetValue<bool>(), partial.ToJsonString());
        Assert.Equal("outcomesdonotmatchitems", partial["refused"]!.GetValue<string>());
        Assert.Equal(["q1", "q2"], partial["expected_items"]!.AsArray().Select(i => i!.GetValue<string>()));

        // Both, and it lands — the refusal is a correction rather than a dead end.
        var complete = await billing.ResultAsync(
            exchangeId,
            [
                new ItemOutcome("q1", "answered", "Yes, at issue."),
                new ItemOutcome("q2", "done", "Refunds carry invoice_lines.tax_rate as of this commit.")
            ],
            cancellationToken: ct);

        Assert.True(complete["ok"]!.GetValue<bool>(), complete.ToJsonString());
    }

    [Fact]
    public async Task Giving_one_item_two_outcomes_is_refused()
    {
        // The mirror of two items sharing an id, which is refused when the Request is sent.
        // Stored, it is a self-contradiction — "answered" and "blocked" against the same id — and
        // the roll-up would quietly take one, so the exchange stops describing what was said.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new RequestItem("q1", "question", "Is the rate stamped at time of sale?")],
            DoneWhen, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.ResultAsync(
                sent["exchange_id"]!.GetValue<long>(),
                [
                    new ItemOutcome("q1", "answered", "Yes, at issue."),
                    new ItemOutcome("q1", "blocked", "Actually I am not sure.", Blocker: "missing_information")
                ],
                cancellationToken: ct),
            "q1", "more than one");
    }

    [Fact]
    public async Task A_bystander_learns_nothing_about_an_exchange_it_is_not_in()
    {
        // The §13 boundary. Checking that a Result covers the right items means reading which ones
        // were asked, and a refusal that listed them for anybody would carry another pair's
        // identifiers to whoever had guessed the exchange id. Identifiers are chosen by agents,
        // so one may well name what a team is working on.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        var (_, inventory) = await fixture.LaunchAsync("inventory@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Deciding where the rate lives.",
            [new RequestItem("migrate-payments-elsewhere", "question", "Is the rate stamped at sale?")],
            DoneWhen, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await inventory.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(),
            [new ItemOutcome("anything", "answered", "Nothing to do with me.")],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("notaparticipant", refused["refused"]!.GetValue<string>());
        Assert.DoesNotContain("migrate-payments-elsewhere", refused.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_for_an_item_nobody_asked_is_refused()
    {
        // Stored, it would close the exchange complete while the requester's real item went
        // unresolved, so §15 would count work done that had not been.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var sent = await orders.RequestAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new RequestItem("q1", "question", "Is the rate stamped at time of sale?")],
            DoneWhen, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await billing.ResultAsync(
            sent["exchange_id"]!.GetValue<long>(),
            [new ItemOutcome("q99", "answered", "An answer to nothing.")],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("outcomesdonotmatchitems", refused["refused"]!.GetValue<string>());
        Assert.Equal(["q1"], refused["expected_items"]!.AsArray().Select(i => i!.GetValue<string>()));
    }
}
