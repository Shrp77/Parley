using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// Asking a peer that never comes back.
/// </summary>
/// <remarks>
/// The whole of §4.2 rests on something eventually arriving: an agent is told not to block
/// because the answer will reach its inbox. When nobody answers, the exchange used to end
/// correctly and silently — so the one case where an agent most needs to hear something was the
/// one case it heard nothing at all.
/// </remarks>
public sealed class GhostedQuestionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_asker_whose_peer_never_answers_is_told_so_through_its_inbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (ordersSession, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            to: "billing",
            intent: "Deciding whether to store a tax rate on the order or read it from you.",
            subQuestions: [new SubQuestion("q1", "Does an invoice carry the rate at time of sale?")],
            consequences: "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        var exchangeId = asked["exchange_id"]!.GetValue<long>();

        // Billing collects the question and then goes quiet for good.
        Assert.Single((await billing.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        for (var hour = 1; hour <= 12; hour++)
        {
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            await ordersSession.RunAsync((broker, now, c) => broker.TickAsync(now, c), ct);
        }

        var notice = Assert.Single((await orders.InboxAsync(cancellationToken: ct))["messages"]!.AsArray());

        Assert.Equal("ghost", notice!["type"]!.GetValue<string>());
        Assert.Equal(exchangeId, notice["exchange_id"]!.GetValue<long>());

        var envelope = notice["message"]!.GetValue<string>();

        // Wrapped like everything else (§13), attributed to the broker (§11.3), and carrying the
        // asker's own consequences back (§14) — which is what makes it something to act on
        // rather than a bereavement notice.
        Assert.Contains("--- begin peer payload", envelope, StringComparison.Ordinal);
        Assert.Contains("responder: parley:broker", envelope, StringComparison.Ordinal);
        Assert.Contains("No answer came from billing", envelope, StringComparison.Ordinal);
        Assert.Contains("snapshot my own copy", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ghosted_exchange_is_over_and_says_so_when_touched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (ordersSession, orders) = await fixture.LaunchAsync("orders", ct);
        var (_, billing) = await fixture.LaunchAsync("billing", ct);

        var asked = await orders.AskAsync(
            "billing", "Deciding where the tax rate lives.",
            [new SubQuestion("q1", "Is the rate stamped at time of sale?")],
            "If it does I read it from you; if not I snapshot my own copy.",
            cancellationToken: ct);

        await billing.InboxAsync(cancellationToken: ct);

        for (var hour = 1; hour <= 12; hour++)
        {
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            await ordersSession.RunAsync((broker, now, c) => broker.TickAsync(now, c), ct);
        }

        // The peer turning up late is refused rather than reopening a closed conversation.
        var late = await billing.AnswerAsync(
            asked["exchange_id"]!.GetValue<long>(),
            [new Resolution("q1", "answered", "Sorry — yes, at issue.")],
            cancellationToken: ct);

        Assert.False(late["ok"]!.GetValue<bool>(), late.ToJsonString());
    }
}
