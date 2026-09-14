using Parley.Core;
using Parley.Web.Pages.Projects;
using Xunit;

namespace Parley.Web.Tests;

/// <summary>
/// What the console's Health page says about each kind of flag.
/// </summary>
/// <remarks>
/// Found by the review gate, lens 3 run 3.6: the fourth kind — a conversation waiting only on a vote — was rendered by
/// every surface and pinned by none. The sentence is the part a person acts on, so each kind is held to saying who is
/// waiting on whom, and a vote flag to naming no exchange.
/// </remarks>
public sealed class HealthDescribeTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_vote_flag_names_who_has_not_answered_and_the_conversation()
    {
        var said = HealthModel.Describe(new Flag(FlagKind.NotVoted, 0, "pricing", Billing, Orders, T0));

        Assert.Contains("billing@test", said, StringComparison.Ordinal);
        Assert.Contains("orders@test", said, StringComparison.Ordinal);
        Assert.Contains("pricing", said, StringComparison.Ordinal);
        Assert.Contains("closing it has waited on billing@test", said, StringComparison.Ordinal);
        Assert.DoesNotContain("has not collected", said, StringComparison.Ordinal);
        Assert.DoesNotContain("exchange", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FlagKind.NotCollected, "has not collected")]
    [InlineData(FlagKind.NoProgress, "has done nothing on it")]
    [InlineData(FlagKind.Retired, "is retired")]
    [InlineData(FlagKind.NotVoted, "closing it has waited on")]
    public void Each_kind_says_its_own_thing(FlagKind kind, string words)
    {
        var said = HealthModel.Describe(new Flag(kind, kind == FlagKind.NotVoted ? 0 : 7, "pricing", Billing, Orders, T0));

        Assert.Contains(words, said, StringComparison.Ordinal);
    }
}
