using Parley.Client.Seating;
using Parley.Setup;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every rule this surface appears to enforce is enforced somewhere else, and only quoted
/// here.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.4. A surface that re-implements a protocol rule has a second
/// opinion about it, and the one that disagrees is always the one that lets something through.
/// Three rules are visible on these screens and none of them is this surface's: that an ask is
/// said at all, rule 8.4's size cap, and §5.3's outcomes.
/// </para>
/// <para>
/// <b>The one that matters most is the outcome list.</b> §5.3 reserves <c>closed</c> for the
/// broker closing a conversation; an agent — or a person at a seat — able to write it over their
/// own open work would be forging the broker's hand in the record §15 counts. The list comes from
/// <c>SeatVoice</c>, so this surface cannot grow another by having somebody type one.
/// </para>
/// </remarks>
public sealed class GuardOwnershipTests
{
    [Fact]
    public void The_outcomes_offered_are_the_clients_and_closed_is_not_among_them()
    {
        Assert.NotEmpty(SeatVoice.Outcomes);

        Assert.DoesNotContain(
            SeatVoice.Outcomes,
            outcome => outcome.Value.StartsWith("closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_ask_is_held_to_being_said_at_all_and_to_no_length_this_surface_invented()
    {
        // The floor that used to live here was `done_when`'s twenty characters, and it went with the
        // field in 0.7: §5.1 asks for the ask and nothing else, and a receiver that cannot scope one
        // sends a Clarify. So the only rule left is that something was typed -- and a short ask is
        // admitted, which is the half worth pinning. A surface that grew its own minimum back would
        // be refusing what the protocol accepts, and nothing but this would notice.
        Assert.NotNull(Participation.RequiredProblem(string.Empty));
        Assert.NotNull(Participation.RequiredProblem("   "));

        Assert.Null(Participation.RequiredProblem("why?"));
    }

    [Fact]
    public void The_ownership_rule_is_the_schemas_rather_than_one_written_here()
    {
        // The rule is a CHECK in the broker's schema. A second copy here would eventually disagree
        // with the one actually enforced, and the disagreement would surface as a registration
        // refused at launch -- which reads as an agent that simply never appeared.
        //
        // It used to be a length, and 0.33.0 removed the length. The field therefore has to apply
        // the rule that is left -- that the statement renders as what it says, on one line -- or it
        // applies nothing at all while still looking like a guard.
        Assert.Null(Adding.OwnsProblem("Carts and order totals. No tax, no stock."));
        Assert.Null(Adding.OwnsProblem(new string('a', 4_000)));

        Assert.NotNull(Adding.OwnsProblem("Carts." + (char)0x2028 + "No tax."));
        Assert.NotNull(Adding.OwnsProblem("Carts." + (char)0x202E + "No tax."));
    }

    [Fact]
    public void An_empty_result_is_refused_because_it_closes_an_exchange_while_saying_nothing()
    {
        // §5.3: a Result closes the exchange. An empty one closes it and tells the requester
        // nothing -- so it is refused in the field rather than sent and regretted.
        Assert.NotNull(Participation.ResultProblem(string.Empty));
        Assert.NotNull(Participation.ResultProblem("   "));
        Assert.Null(Participation.ResultProblem("because the totals are computed downstream"));
    }

    // `A_seat_name_is_checked_by_the_grammar_that_makes_agent_names` was here. There is no seat name to type since
    // specs/023: the broker provides the person seat.

    [Fact]
    public void A_role_is_checked_by_the_same_rule_the_enrolment_command_applies()
    {
        Assert.Equal(
            EnrollmentPlan.IsWellFormedName("orders"),
            Adding.RoleProblem("orders") is null);

        Assert.Equal(
            EnrollmentPlan.IsWellFormedName("Orders Ltd"),
            Adding.RoleProblem("Orders Ltd") is null);
    }

    [Fact]
    public void The_check_is_looking_at_something()
    {
        // Non-vacuity: each helper really does refuse something, so the passes above are not a
        // pair of methods that return null whatever they are given.
        Assert.NotNull(Participation.RequiredProblem(string.Empty));
        Assert.Null(Participation.RequiredProblem("something"));
    }
}
