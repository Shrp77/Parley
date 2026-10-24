using Xunit;

namespace Parley.Mcp.Tests;

/// <summary>
/// What an agent is told when the conversation it named is not one it may write into (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// An agent holding no request may start a conversation by naming it, so a refusal must not say it can only continue
/// one: the conversation exists without it, or its name is taken (a destroyed conversation, or one reserved for a
/// roadmap item). What it reads is what it acts on, so the sentence has to be true.
/// </remarks>
public sealed class ConversationRefusalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Naming_another_pairs_conversation_is_refused_with_a_sentence_that_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ServerFixture(T0);

        var (_, orders) = await fixture.LaunchAsync("orders@test", ct);
        var (_, billing) = await fixture.LaunchAsync("billing@test", ct);
        await fixture.LaunchAsync("inventory@test", ct);

        var theirs = await billing.RequestAsync("inventory@test", "How many are in stock?", "theirs", cancellationToken: ct);
        Assert.True(theirs["ok"]!.GetValue<bool>(), theirs.ToJsonString());

        var refused = await orders.RequestAsync("billing@test", "May I join?", "theirs", cancellationToken: ct);

        Assert.False(refused["ok"]!.GetValue<bool>(), refused.ToJsonString());

        var why = refused["why"]!.GetValue<string>();

        Assert.DoesNotContain("only continue", why, StringComparison.Ordinal);
        Assert.Contains("not one you are part of, and not one you can start", why, StringComparison.Ordinal);
        Assert.Contains("Leave conversation_id out", why, StringComparison.Ordinal);
    }
}
