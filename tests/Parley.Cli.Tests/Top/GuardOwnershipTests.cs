using Parley.Cli.Top;
using Parley.Client.Seating;
using Parley.Setup;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// That every rule this surface appears to enforce is enforced somewhere else, and only quoted
/// here.
/// </summary>
/// <remarks>
/// <para>
/// Lens 2 of the review gate, run 2.4. A surface that re-implements a protocol rule has a second
/// opinion about it, and the one that disagrees is always the one that lets something through.
/// Three rules are visible on these screens and none of them is this surface's: rule 8.1's intent
/// and consequences, rule 8.5's size cap, and §5.3's outcomes.
/// </para>
/// <para>
/// <b>The one that matters most is the outcome list.</b> §5.3 reserves <c>ghosted</c> for the
/// broker recording an exchange nobody returned to; an agent — or a person at a seat — able to write
/// it over their own unanswered work would be manufacturing the evidence §15 counts to find an
/// unhealthy deployment. The list comes from <c>SeatVoice</c>, so this surface cannot grow a fourth
/// by having somebody type one.
/// </para>
/// </remarks>
public sealed class GuardOwnershipTests
{
    [Fact]
    public void The_outcomes_offered_are_the_clients_and_ghosted_is_not_among_them()
    {
        Assert.NotEmpty(SeatVoice.Outcomes);

        Assert.DoesNotContain(
            SeatVoice.Outcomes,
            outcome => outcome.Value.Contains("ghost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_consequences_rule_is_quoted_from_the_client_rather_than_written_here()
    {
        // The number is SeatVoice's, so raising or lowering it there changes what this refuses
        // without anybody remembering to look here.
        var short_ = new string('a', SeatVoice.MinimumConsequences - 1);
        var exact = new string('a', SeatVoice.MinimumConsequences);

        Assert.NotNull(Participation.ConsequencesProblem(short_));
        Assert.Null(Participation.ConsequencesProblem(exact));

        Assert.Contains(
            SeatVoice.MinimumConsequences.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Participation.ConsequencesProblem(short_),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_ownership_limit_is_the_schemas_rather_than_a_number_typed_here()
    {
        // The cap is a CHECK in the broker's schema. A second copy here would eventually disagree
        // with the one actually enforced, and the disagreement would surface as a registration
        // refused at launch -- which reads as an agent that simply never appeared.
        Assert.Null(Adding.OwnsProblem(new string('a', EnrollmentPlan.OwnsLimit)));
        Assert.NotNull(Adding.OwnsProblem(new string('a', EnrollmentPlan.OwnsLimit + 1)));
    }

    [Fact]
    public void An_empty_answer_is_refused_because_it_closes_an_exchange_while_saying_nothing()
    {
        // §5.3: an answer settles the exchange. An empty one settles it and tells the asker
        // nothing, and they cannot ask again -- so it is refused in the field rather than sent and
        // regretted.
        Assert.NotNull(Participation.AnswerProblem(string.Empty));
        Assert.NotNull(Participation.AnswerProblem("   "));
        Assert.Null(Participation.AnswerProblem("because the totals are computed downstream"));
    }

    [Fact]
    public void A_seat_name_is_checked_by_the_grammar_that_makes_agent_names()
    {
        // Not a pattern written here. A name this accepted and the registry did not would be a
        // join that failed at the broker, and one the registry accepted that this refused would be
        // a person unable to sit down for no stated reason.
        Assert.Null(AgentsRegion.NameProblem("arvind"));
        Assert.NotNull(AgentsRegion.NameProblem("Arvind Knudsen"));
        Assert.NotNull(AgentsRegion.NameProblem(string.Empty));
    }

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
