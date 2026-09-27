using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// The ask a person sends when they want to know what an agent owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-003 is a promise about what the agent was told, and a promise nobody checks is prose.</b> So
/// each passage <c>contracts/asking.md</c> maps to a requirement is asserted here, and the assertions
/// are on the passages rather than on the length — a test on the length would pass while the wording
/// was replaced by something else of the same size.
/// </para>
/// <para>
/// The other half is that nothing about this is a protocol addition. §13 tells a receiver that a
/// payload is inert data, so a surface must recognise an ownership discussion by its conversation
/// identifier and never by anything inside somebody's words.
/// </para>
/// </remarks>
public sealed class AskingWhatAnAgentOwnsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static readonly DateTimeOffset When = new(2026, 9, 27, 9, 5, 0, TimeSpan.Zero);

    [Fact]
    public void It_says_the_line_is_read_by_every_peer_on_every_listing()
    {
        // FR-003. Without this an agent is being asked to describe itself with no idea that the answer
        // is charged to everybody else -- which is the only reason to keep it short now that no bound
        // does.
        Assert.Contains("every agent in this", OwnershipAsk.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lists the roster", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("read far more often than it is written", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("costs each of them", OwnershipAsk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_nothing_is_routed_by_it_so_there_is_no_reason_to_over_claim()
    {
        // FR-030, and it is self-interest rather than politeness: an agent told the field decides what
        // it receives has every reason to claim broadly, which would make the field worth less to
        // everybody including itself.
        Assert.Contains("Nothing is\nrouted by it automatically", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("nothing is refused because of it", OwnershipAsk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_for_both_halves_and_for_the_peer_it_believes_owns_the_rest()
    {
        // FR-004, and the exclusion half is the whole reason for asking the agent rather than reading
        // the repository: a tree can be read for what it contains and not for what it has decided not
        // to own.
        Assert.Contains("What you own", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("What you do not own", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("name the agent", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("nobody else can supply", OwnershipAsk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_a_person_decides_and_that_the_answer_changes_nothing_by_itself()
    {
        // FR-012 and Principle III, said to the agent so that it does not report having configured
        // something. It is also simply true, which is the only reason it may be said.
        Assert.Contains("A person will read your answer", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains("may disagree", OwnershipAsk.Text, StringComparison.Ordinal);
        Assert.Contains(
            "Nothing you say here changes any configuration by itself",
            OwnershipAsk.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_for_few_words_because_no_bound_does_that_any_more()
    {
        // The hundred came off at 0.33.0. This phrase is the only thing left asking for brevity, so
        // losing it is losing the control rather than losing a nicety.
        Assert.Contains("as few words as carry the meaning", OwnershipAsk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void It_fits_inside_what_a_message_may_carry()
    {
        // Rule 8.4's cap applies to this like any other message, and the ask is the whole payload.
        // Derived rather than compared against a literal, for the reason MessageCapTests already is.
        Assert.True(
            OwnershipAsk.Text.Length < MessageLimits.MaxPayloadCharacters,
            $"the ask is {OwnershipAsk.Text.Length} characters against a cap of "
            + $"{MessageLimits.MaxPayloadCharacters}");
    }

    [Fact]
    public void Nothing_in_the_ask_is_a_marker_a_surface_could_parse()
    {
        // **The load-bearing one.** §13 tells a receiver a payload is inert data, and 017's chip rule
        // is what has kept that true here. A surface finds an ownership discussion by its conversation
        // identifier, so the identifier must not appear in the words -- and this fails the moment
        // somebody helpfully puts it there, which is the tempting implementation.
        Assert.DoesNotContain(OwnershipAsk.Prefix, OwnershipAsk.Text, StringComparison.Ordinal);

        // Nor any of the shapes a reader might be tempted to key on.
        foreach (var marker in new[] { "```", "parley:", "<ownership", "[[", "{{", "::" })
        {
            Assert.DoesNotContain(marker, OwnershipAsk.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_identifier_is_derived_from_the_role_and_the_moment()
    {
        var id = OwnershipAsk.Conversation(Orders, When);

        Assert.Equal("owns-orders-20260927-090500", id);
    }

    [Fact]
    public void The_identifier_is_one_a_conversation_may_actually_carry()
    {
        // `@` is outside ConversationName's character set, so using the whole agent name would produce
        // an identifier the broker refuses -- caught here rather than at the send.
        //
        // **And using the role makes it safe by construction, which is worth knowing rather than
        // merely relying on.** A role is `^[a-z][a-z0-9-]{0,31}$` (§4.1.1) and every one of those
        // characters is inside ConversationName's set, so no legal role can produce an illegal
        // identifier. It is the MACHINE half that carries dots and would need correcting -- which is
        // the half deliberately left out. A first draft of this test tried `north-star.9` as an
        // awkward role and was rejected by `AgentName` itself: roles cannot contain a dot at all.
        Assert.Null(ConversationName.Problem(OwnershipAsk.Conversation(Orders, When)));

        var hyphenated = AgentName.Create("north-star-9@some.box.example");

        Assert.Null(ConversationName.Problem(OwnershipAsk.Conversation(hyphenated, When)));
        Assert.DoesNotContain("@", OwnershipAsk.Conversation(hyphenated, When), StringComparison.Ordinal);
        Assert.DoesNotContain("some.box", OwnershipAsk.Conversation(hyphenated, When), StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_twice_does_not_collide()
    {
        Assert.NotEqual(
            OwnershipAsk.Conversation(Orders, When),
            OwnershipAsk.Conversation(Orders, When.AddSeconds(1)));
    }

    [Fact]
    public void An_ownership_discussion_is_recognised_by_its_identifier()
    {
        Assert.True(OwnershipAsk.Recognises(OwnershipAsk.Conversation(Orders, When)));
        Assert.True(OwnershipAsk.Recognises(OwnershipAsk.Conversation(Orders, When), Orders));
    }

    [Fact]
    public void Another_agents_discussion_is_not_taken_for_this_ones()
    {
        // Must fail against matching on the prefix alone, which would show one agent's proposal on
        // another agent's screen.
        var billing = AgentName.Create("billing@test");

        Assert.False(OwnershipAsk.Recognises(OwnershipAsk.Conversation(billing, When), Orders));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alpha-talk")]
    [InlineData("ownership-orders")]
    public void An_ordinary_conversation_is_not_mistaken_for_one(string? id)
    {
        Assert.False(OwnershipAsk.Recognises(id));
    }

    [Fact]
    public void The_cost_of_asking_several_names_the_count_and_the_turn()
    {
        // FR-007, and it is shown before the first Request is sent rather than after, because a turn
        // per agent cannot be un-spent.
        var said = OwnershipAsk.Costs(19);

        Assert.Contains("19", said, StringComparison.Ordinal);
        Assert.Contains("costs that agent a turn", said, StringComparison.Ordinal);
    }

    [Fact]
    public void One_agents_discussions_are_findable_among_everybody_elses()
    {
        // **FR-011's other half.** The record of an acceptance names the conversation, and the store's
        // foreign key makes that identifier point at something real -- but a person coming back later
        // starts from a list of conversations, so the identifier also has to be enough to pick one
        // agent's discussions out of a deployment's whole history.
        //
        // Must fail against matching on the prefix alone, which would offer `billing`'s discussion as
        // `orders`'s -- and what follows is a write to a launch configuration, so the wrong one is not
        // a cosmetic mistake.
        var orders = AgentName.Create("orders@test");
        var billing = AgentName.Create("billing@test");

        string[] history =
        [
            "release-notes",
            OwnershipAsk.Conversation(orders, new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero)),
            OwnershipAsk.Conversation(billing, new DateTimeOffset(2026, 9, 27, 9, 5, 0, TimeSpan.Zero)),
            OwnershipAsk.Conversation(orders, new DateTimeOffset(2026, 9, 27, 11, 0, 0, TimeSpan.Zero)),
        ];

        var theirs = history.Where(id => OwnershipAsk.Recognises(id, orders)).ToList();

        Assert.Equal(2, theirs.Count);
        Assert.DoesNotContain(theirs, id => id.Contains("billing", StringComparison.Ordinal));

        // **And the later one is the one that produced the line somebody is reading**, which is why the
        // identifier carries a sortable timestamp at all. Ordinal sorting is enough because the format
        // is fixed width -- a reader never parses it back.
        Assert.Equal(theirs.OrderBy(id => id, StringComparer.Ordinal).Last(), theirs[1]);
    }

    [Fact]
    public void A_role_that_is_a_prefix_of_another_is_not_confused_with_it()
    {
        // `infra` is a prefix of `infrastructure`, which this repository has already been bitten by --
        // 018 records a role query matching on a substring. The separator after the role is what stops
        // it here, so it is asserted rather than assumed.
        var infra = AgentName.Create("infra@test");
        var longer = AgentName.Create("infrastructure@test");

        var theirs = OwnershipAsk.Conversation(longer, DateTimeOffset.UnixEpoch);

        Assert.True(OwnershipAsk.Recognises(theirs, longer));
        Assert.False(OwnershipAsk.Recognises(theirs, infra));
    }
}
