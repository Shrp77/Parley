using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// Which of an agent's messages a person is shown as its proposal.
/// </summary>
/// <remarks>
/// <b>0.30.1's rule, reused for its own reason.</b> A row points at the latest thing a peer said in an
/// exchange rather than the first, because an exchange can hold a Request, a Clarify and then a
/// revision — so an agent that argued and then changed its mind has said two statements, and the one a
/// person accepts is the later. Taking the first would put in front of them the sentence they had just
/// talked the agent out of, and then write it.
/// </remarks>
public sealed class TheProposalIsTheLatestThingSaidTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Person = AgentName.Create("arvind@test");

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly AgentName Parley = Facilitation.On(ProjectName.Create("test"));

    private static Message Said(
        AgentName from, string payload, int minute, long id, MessageType type = MessageType.Result,
        AgentName? to = null) =>
        new(
            id,
            1,
            type,
            from,
            to ?? (from == Orders ? Person : Orders),
            "some-model-9",
            payload,
            "owns-orders-20260927-090000",
            T0.AddMinutes(minute),
            null);

    private static string Answer(string text) =>
        $$"""{"outcomes":[{"id":"request","outcome":"answered","text":"{{text}}"}]}""";

    private static string Asked(string text) => $$"""{"ask":"{{text}}"}""";

    [Fact]
    public void A_revision_after_a_disagreement_is_what_is_shown()
    {
        // **Must fail against taking the first message.** The transcript is deliberately out of order,
        // so an implementation relying on the caller having sorted it fails here too.
        var transcript = new[]
        {
            Said(Orders, Answer("Everything to do with orders, tax and stock."), minute: 1, id: 2),
            Said(Person, Asked("You do not own stock. Inventory does."), minute: 2, id: 3, MessageType.Clarify),
            Said(Orders, Answer("Carts and order totals. Not tax, not stock."), minute: 3, id: 4),
            Said(Person, Asked(OwnershipAsk.Text), minute: 0, id: 1, MessageType.Request),
        };

        var proposal = OwnershipProposal.Latest(transcript, Orders);

        Assert.NotNull(proposal);
        Assert.Equal("Carts and order totals. Not tax, not stock.", proposal.Statement);
    }

    [Fact]
    public void The_message_travels_with_the_text_so_a_surface_can_attribute_it()
    {
        // FR-013: the proposal must be drawn as the agent's words and told apart from what the surface
        // wrote. A bare string could not be attributed to anybody.
        var proposal = OwnershipProposal.Latest(
            [Said(Orders, Answer("Invoicing and tax."), minute: 1, id: 2)], Orders);

        Assert.NotNull(proposal);
        Assert.Equal(Orders, proposal.Message.From);
        Assert.Equal(T0.AddMinutes(1), proposal.Message.CreatedUtc);
    }

    [Fact]
    public void What_the_person_said_is_never_taken_for_the_agents_proposal()
    {
        // The person's own words are in the same conversation, and they are the later messages more
        // often than not. Must fail against reading the transcript's last message whoever wrote it.
        var transcript = new[]
        {
            Said(Orders, Answer("Invoicing and tax."), minute: 1, id: 2),
            Said(Person, Asked("Are you sure about tax?"), minute: 5, id: 3, MessageType.Clarify),
        };

        var proposal = OwnershipProposal.Latest(transcript, Orders);

        Assert.NotNull(proposal);
        Assert.Equal("Invoicing and tax.", proposal.Statement);
    }

    [Fact]
    public void Another_agents_words_are_not_this_agents_proposal()
    {
        var billing = AgentName.Create("billing@test");

        var transcript = new[]
        {
            Said(Orders, Answer("Carts and orders."), minute: 1, id: 2),
            Said(billing, Answer("Invoicing and tax."), minute: 9, id: 3),
        };

        Assert.Equal("Carts and orders.", OwnershipProposal.Latest(transcript, Orders)!.Statement);
        Assert.Equal("Invoicing and tax.", OwnershipProposal.Latest(transcript, billing)!.Statement);
    }

    [Fact]
    public void A_clarify_the_agent_asked_back_is_shown_as_it_was_written()
    {
        // An answer that is a question is a legitimate answer (§5.2), and this does not try to decide
        // whether prose is a statement of ownership. A surface that guessed would hide the case where
        // an agent asked something back, which is exactly the case a person has to act on.
        var transcript = new[]
        {
            Said(Orders, Asked("Do you mean the checkout, or the whole order pipeline?"),
                minute: 1, id: 2, MessageType.Clarify),
        };

        Assert.Equal(
            "Do you mean the checkout, or the whole order pipeline?",
            OwnershipProposal.Latest(transcript, Orders)!.Statement);
    }

    [Fact]
    public void An_agent_that_has_said_nothing_has_no_proposal()
    {
        // Distinct from a proposal that is empty, and the surface says "not answered yet" rather than
        // "declined" -- 008's rule that a silence is "I do not know" and never "it did not happen".
        Assert.Null(OwnershipProposal.Latest([Said(Person, Asked(OwnershipAsk.Text), 0, 1, MessageType.Request)], Orders));
        Assert.Null(OwnershipProposal.Latest([], Orders));
        Assert.Null(OwnershipProposal.Latest(null, Orders));
    }

    [Fact]
    public void A_payload_nothing_can_be_read_out_of_is_no_proposal()
    {
        // Must fail against returning an empty statement, which a surface would then offer to accept --
        // and an empty line is not a statement of ownership.
        Assert.Null(OwnershipProposal.Latest([Said(Orders, "not json at all", 1, 2)], Orders));
        Assert.Null(OwnershipProposal.Latest([Said(Orders, """{"outcomes":[]}""", 1, 2)], Orders));
    }

    [Fact]
    public void Two_messages_in_the_same_moment_still_have_one_answer()
    {
        // Ties break on the identifier, which is monotonic. Without it the answer would depend on the
        // order the store happened to return rows in.
        var transcript = new[]
        {
            Said(Orders, Answer("First."), minute: 1, id: 2),
            Said(Orders, Answer("Second."), minute: 1, id: 3),
        };

        Assert.Equal("Second.", OwnershipProposal.Latest(transcript, Orders)!.Statement);
        Assert.Equal("Second.", OwnershipProposal.Latest(transcript.Reverse(), Orders)!.Statement);
    }

    [Fact]
    public void Whose_line_a_discussion_is_about_is_whoever_Parley_asked_in_it()
    {
        // **Must fail against parsing the identifier back into a name.** It carries a role and deliberately no machine,
        // so `owns-orders-...` alone cannot say which `orders` was asked. The Request's recipient is a whole name.
        var transcript = new[]
        {
            Said(Parley, Asked(OwnershipAsk.Text), minute: 0, id: 1, MessageType.Request, to: Orders),
            Said(Orders, Answer("Orders and invoicing."), minute: 1, id: 2, to: Parley),
        };

        Assert.Equal(Orders, OwnershipProposal.Subject(transcript));
    }

    [Fact]
    public void An_agent_asked_but_silent_is_still_the_subject()
    {
        // The ordinary state for the first minutes of every question. The surface has to be able to say "not answered
        // yet" about a named agent rather than about nobody.
        var asked = new[] { Said(Parley, Asked(OwnershipAsk.Text), minute: 0, id: 1, MessageType.Request, to: Orders) };

        Assert.Equal(Orders, OwnershipProposal.Subject(asked));
        Assert.Null(OwnershipProposal.Latest(asked, Orders));
    }

    [Fact]
    public void An_agents_own_owns_conversation_is_not_a_question()
    {
        // Research finding 11, and must fail against recognising the prefix: this conversation carries the identifier
        // Parley would have used, and an agent — not Parley — asked in it. Accepting from it would make a line out of an
        // answer to a question nobody asked.
        var transcript = new[]
        {
            Said(Person, Asked("What do you own?"), minute: 0, id: 1, MessageType.Request, to: Orders),
            Said(Orders, Answer("Everything."), minute: 1, id: 2, to: Person),
        };

        Assert.Null(OwnershipProposal.Subject(transcript));
    }

    [Fact]
    public void A_transcript_in_which_Parley_asked_two_agents_has_no_subject()
    {
        // Parley never does. What follows an acceptance is a write to somebody's launch configuration, so a transcript
        // saying otherwise must not have one chosen from it.
        var elsewhere = AgentName.Create("orders@other");

        var transcript = new[]
        {
            Said(Parley, Asked(OwnershipAsk.Text), minute: 0, id: 1, MessageType.Request, to: Orders),
            Said(Parley, Asked(OwnershipAsk.Text), minute: 0, id: 2, MessageType.Request, to: elsewhere),
        };

        Assert.Null(OwnershipProposal.Subject(transcript));
    }

    [Fact]
    public void Nothing_is_a_subject_of_an_empty_or_absent_transcript()
    {
        Assert.Null(OwnershipProposal.Subject(null));
        Assert.Null(OwnershipProposal.Subject([]));
    }
}
