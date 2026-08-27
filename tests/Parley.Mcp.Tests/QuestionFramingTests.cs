using System.Text.Json.Nodes;
using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// The two rules that decide whether an exchange was worth opening.
/// </summary>
/// <remarks>
/// SPEC.md §8 rule 1 — a question states what the asker will do with each possible answer — and
/// §5.3 — one resolution per sub-question asked. Both are quality rules rather than safety ones,
/// and both were reaching agents as SQL or not at all, which is the same thing from where the
/// agent is standing.
/// </remarks>
public sealed class QuestionFramingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string Consequences =
        "If it does I read it from you; if not I snapshot my own copy.";

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
    [InlineData("none")]
    [InlineData("I will use the answer.")]
    public async Task A_question_that_does_not_say_what_the_answer_is_for_is_refused(string consequences)
    {
        // Rule 1 is the one the rest of the protocol leans on, and it is enforced by a length
        // floor rather than by requiring the field, because "n/a" satisfies a presence check and
        // states nothing. Enforced in the schema it arrived as a CHECK naming json_extract — the
        // single worst place to answer with SQL, because the agent has not made a mistake of
        // syntax. It has failed to say what its question is for.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        AssertMalformed(
            await orders.AskAsync(
                "billing@test", "Deciding where the tax rate lives.",
                [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
                consequences, cancellationToken: ct),
            "what you will do");
    }

    [Fact]
    public async Task A_question_with_no_stated_intent_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        await fixture.LaunchAsync("billing@test", ct);

        AssertMalformed(
            await orders.AskAsync(
                "billing@test", "  ",
                [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
                Consequences, cancellationToken: ct),
            "trying to decide");
    }

    [Fact]
    public async Task An_answer_that_leaves_a_sub_question_unresolved_is_refused()
    {
        // The likelier half of §5.3, and the one that costs most. Answering the first of two
        // closes the exchange exactly as firmly as answering both — so the second question is
        // gone, with nothing recording that it was skipped rather than answered.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [
                new SubQuestion("q1", "Is the rate stamped at time of sale?"),
                new SubQuestion("q2", "And on a refund?")
            ],
            Consequences, cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();
        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                exchangeId, [new Resolution("q1", "answered", "Yes, at issue.")],
                cancellationToken: ct),
            "q2");

        // Both, and it lands — the refusal is a correction rather than a dead end.
        var complete = await billing.AnswerAsync(
            exchangeId,
            [
                new Resolution("q1", "answered", "Yes, at issue."),
                new Resolution("q2", "unknown", "Refunds I am not sure about.")
            ],
            cancellationToken: ct);

        Assert.True(complete["ok"]!.GetValue<bool>(), complete.ToJsonString());
    }

    [Fact]
    public async Task Answering_one_sub_question_twice_is_refused()
    {
        // The mirror of two sub-questions sharing an id, which is refused when the question is
        // asked. Stored, it is a self-contradiction — "answered: gross" and "unknown" against
        // the same id — and the rollup quietly takes the pessimistic one, so the aggregate stops
        // describing what the answerer actually said.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                asked["exchange_id"]!.GetValue<long>(),
                [
                    new Resolution("q1", "answered", "Yes, at issue."),
                    new Resolution("q1", "unknown", "Actually I am not sure.")
                ],
                cancellationToken: ct),
            "q1", "more than once");
    }

    [Fact]
    public async Task A_bystander_learns_nothing_about_an_exchange_it_is_not_in()
    {
        // The §13 boundary, and a hole opened by the fix above rather than found in old code.
        // Checking that an answer resolves the right sub-questions means reading which ones were
        // asked — and the first version read them for anybody, so the refusal it produced
        // carried another pair's identifiers to whoever had guessed the exchange id. Identifiers
        // are chosen by agents, so one may well name what a team is working on.
        //
        // Ordering the guards would not fix it: the read has to be scoped, so that there is
        // nothing to leak rather than a careful order that stops it leaking.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        var (_, inventory) = await fixture.LaunchAsync("inventory@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the rate lives.",
            [new SubQuestion("migrate-payments-elsewhere", "Is the rate stamped at sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        var refused = await inventory.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("anything", "answered", "Nothing to do with me.")],
            cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Equal("notaparticipant", refused["refused"]!.GetValue<string>());
        Assert.DoesNotContain(
            "migrate-payments-elsewhere", refused.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_resolving_a_sub_question_nobody_asked_is_refused()
    {
        // It used to be stored, and it closed the exchange: the asker's real question went
        // unanswered while the exchange recorded 'answered', so §15 counted a deployment
        // resolving something it had not.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);

        var asked = await orders.AskAsync(
            "billing@test", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            Consequences, cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        AssertMalformed(
            await billing.AnswerAsync(
                asked["exchange_id"]!.GetValue<long>(),
                [new Resolution("q99", "answered", "An answer to nothing.")],
                cancellationToken: ct),
            "q99", "q1");
    }
}
