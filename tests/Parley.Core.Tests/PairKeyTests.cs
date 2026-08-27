using Xunit;

namespace Parley.Core.Tests;

public sealed class PairKeyTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    [Fact]
    public void Key_is_unordered()
    {
        Assert.Equal(PairKey.For(Orders, Billing), PairKey.For(Billing, Orders));
    }

    [Fact]
    public void Distinct_pairs_produce_distinct_keys()
    {
        var inventory = AgentName.Create("inventory@test");

        Assert.NotEqual(PairKey.For(Orders, Billing), PairKey.For(Orders, inventory));
    }

    [Fact]
    public void Concatenation_cannot_be_ambiguous()
    {
        // Without a separator, ("a","bc") and ("ab","c") would collide. The separator is
        // excluded from the AgentName grammar precisely so this cannot happen.
        var a = AgentName.Create("a@m");
        var bc = AgentName.Create("bc@m");
        var ab = AgentName.Create("ab@m");
        var c = AgentName.Create("c@m");

        Assert.NotEqual(PairKey.For(a, bc), PairKey.For(ab, c));
    }

    [Fact]
    public void Self_pairing_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => PairKey.For(Orders, Orders));
    }

    [Fact]
    public void Round_trips_through_storage()
    {
        var key = PairKey.For(Orders, Billing);

        Assert.Equal(key, PairKey.FromStorage(key.Value));
    }

    [Theory]
    [InlineData("Orders")]      // uppercase
    [InlineData("1orders")]     // leading digit
    [InlineData("-orders")]     // leading hyphen
    [InlineData("order_s")]     // underscore
    [InlineData("")]
    [InlineData("thisnameisveryverymuchtoolongtobevalid")]
    public void Invalid_agent_names_are_rejected(string candidate)
    {
        Assert.False(AgentName.TryCreate(candidate, out _));
    }
}
