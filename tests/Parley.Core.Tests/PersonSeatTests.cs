using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That <see cref="PersonSeat"/> names the person for the project and recognises only its own role (SPEC.md §4.1.1,
/// §12.3).
/// </summary>
/// <remarks>
/// <b>The role test is what every door asks, so a loose one is a door left open.</b> A prefix match would treat
/// <c>human-users</c> or <c>human-user-bot</c> as the person, and refusing those to an agent would be a surprise;
/// admitting a lookalike as the person at a surface would be worse. Both directions are asserted.
/// </remarks>
public sealed class PersonSeatTests
{
    private static readonly ProjectName Alpha = ProjectName.Create("alpha");

    [Fact]
    public void The_person_is_named_for_the_project_and_not_a_machine()
    {
        var person = PersonSeat.On(Alpha);

        Assert.Equal("human-user@alpha", person.Value);
        Assert.Equal(PersonSeat.Role, person.Role);
    }

    [Fact]
    public void The_facilitator_is_named_by_the_same_rule()
    {
        // Two broker-provided participants, one rule (specs/023 D7). If one of them went back to a machine the other
        // would be the odd one out, and a reader would have to learn two conventions for one kind of thing.
        Assert.Equal("parley-facilitator@alpha", Facilitation.On(Alpha).Value);
    }

    [Theory]
    [InlineData("human-user@alpha", true)]
    [InlineData("human-user@beta", true)]
    [InlineData("human-users@alpha", false)]
    [InlineData("human-user-bot@alpha", false)]
    [InlineData("user@alpha", false)]
    [InlineData("arvind@alpha", false)]
    [InlineData("parley-facilitator@alpha", false)]
    public void Only_the_reserved_role_is_the_person(string name, bool isPerson)
    {
        Assert.Equal(isPerson, PersonSeat.Is(AgentName.Create(name)));
        Assert.Equal(isPerson, PersonSeat.IsReserved(AgentName.Create(name).Role));
    }

    [Fact]
    public void The_person_and_the_facilitator_are_never_one_participant()
    {
        Assert.NotEqual(PersonSeat.Role, Facilitation.Role);
        Assert.NotEqual(PersonSeat.Adapter, Facilitation.Adapter);
        Assert.False(Facilitation.Is(PersonSeat.On(Alpha)));
        Assert.False(PersonSeat.Is(Facilitation.On(Alpha)));
    }

    [Fact]
    public void A_refusal_names_the_role_and_says_what_it_is()
    {
        Assert.Contains("'human-user'", PersonSeat.Reserved, StringComparison.Ordinal);
        Assert.Contains("person", PersonSeat.Reserved, StringComparison.Ordinal);
    }
}
