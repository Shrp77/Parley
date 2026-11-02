using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// What a project name accepts, and what it refuses.
/// </summary>
/// <remarks>
/// A project name is deliberately the same grammar as an agent's role, so that an operator who
/// has learned one rule does not meet a second that differs in a detail they discover by being
/// refused. These cases exist to keep the two from drifting apart quietly.
/// </remarks>
[Unit]
public sealed class ProjectNameTests
{
    [Theory]
    [InlineData("orders")]
    [InlineData("billing")]
    [InlineData("a")]
    [InlineData("north-star")]
    [InlineData("v2-orders")]
    public void A_well_formed_name_is_accepted(string candidate)
    {
        Assert.True(ProjectName.IsValid(candidate));
        Assert.Equal(candidate, ProjectName.Create(candidate).Value);
    }

    [Theory]
    [InlineData("Orders")]        // uppercase
    [InlineData("2orders")]       // leading digit
    [InlineData("-orders")]       // leading hyphen
    [InlineData("order_book")]    // underscore
    [InlineData("order book")]    // space
    [InlineData("orders@host")]   // an agent name, not a project name
    [InlineData("")]              // empty
    [InlineData(null)]            // absent
    public void A_malformed_name_is_refused(string? candidate)
    {
        Assert.False(ProjectName.IsValid(candidate));
        Assert.False(ProjectName.TryCreate(candidate, out _));
    }

    [Fact]
    public void A_name_of_exactly_the_limit_is_accepted()
    {
        // The boundary, because an off-by-one here refuses a legal name and gives the operator no
        // way to tell that from the tool being wrong about its own rule.
        Assert.True(ProjectName.IsValid(new string('a', ProjectName.MaxLength)));
    }

    [Fact]
    public void A_name_one_past_the_limit_is_refused()
    {
        Assert.False(ProjectName.IsValid(new string('a', ProjectName.MaxLength + 1)));
    }

    [Fact]
    public void Creating_a_malformed_name_says_what_the_rule_is()
    {
        // Somebody reading this message is looking at a name they thought was fine, so the
        // message has to carry the rule rather than only the verdict.
        var error = Assert.Throws<ArgumentException>(() => ProjectName.Create("Orders"));

        Assert.Contains("lowercase", error.Message, StringComparison.Ordinal);
        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_compare_and_order_ordinally()
    {
        Assert.Equal(ProjectName.Create("orders"), ProjectName.Create("orders"));
        Assert.NotEqual(ProjectName.Create("orders"), ProjectName.Create("billing"));
        Assert.True(ProjectName.Create("billing") < ProjectName.Create("orders"));
    }

    [Fact]
    public void An_uninitialised_name_refuses_to_be_read()
    {
        // Better than returning empty: a default struct that silently reads as "" would let an
        // unset project name flow into a lookup and match nothing, for no stated reason.
        Assert.Throws<InvalidOperationException>(() => default(ProjectName).Value);
    }
}
