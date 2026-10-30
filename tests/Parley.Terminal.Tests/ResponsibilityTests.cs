using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// The one page for what an agent is responsible for, which replaced "say what it owns" and "ask what it owns".
/// </summary>
/// <remarks>
/// <para>
/// <b>Each of these names what it fails against.</b> The page adds no operation: saving is the old record and
/// refreshing is the old ask, so what needs holding is the page itself — what it opens holding, what it says
/// about whose words are whose, and when it refuses to let a line be written.
/// </para>
/// </remarks>
[Integration]
public sealed class ResponsibilityTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static readonly DateTimeOffset When = new(2026, 10, 7, 9, 30, 0, TimeSpan.Zero);

    private static AgentRecord Agent(
        string? owns = "Invoicing only.",
        string? accepted = null,
        OwnershipSource? source = null) =>
        new(
            Orders,
            "test",
            AgentTier.Full,
            "/src/orders",
            null,
            null,
            false,
            AgentStatus.Active,
            owns,
            AgentPresence.Startable,
            accepted,
            source);

    private static OwnershipQuestion Question(QuestionState state, string? answer) =>
        new(Orders, "owns-orders-20261007-093000", 7, state, answer, answer, null, When, null);

    [Fact]
    public void It_opens_on_an_answer_still_waiting_for_a_person_before_anything_else()
    {
        // The answer is what the person is here to decide. Must fail against opening on the line peers read, which
        // would have them overwrite an answer they have not seen by pressing Enter.
        var agent = Agent(owns: "Old line.", accepted: "Accepted line.");

        Assert.Equal(
            "Billing, never invoicing.",
            Responsibility.Opening(agent, Question(QuestionState.Waiting, "Billing, never invoicing.")));
    }

    [Fact]
    public void It_opens_on_the_accepted_line_and_then_the_one_peers_read()
    {
        Assert.Equal("Accepted line.", Responsibility.Opening(Agent(owns: "Old line.", accepted: "Accepted line."), null));
        Assert.Equal("Old line.", Responsibility.Opening(Agent(owns: "Old line."), null));
        Assert.Equal(string.Empty, Responsibility.Opening(Agent(owns: null), null));
    }

    [Fact]
    public void A_decided_answer_does_not_pre_empt_the_line_the_agent_has()
    {
        // An accepted answer is already the line, so the field holds the line. Must fail against opening on any
        // answer at all, which would show a stale sentence after it had been replaced.
        var agent = Agent(owns: "New line.", accepted: "New line.");

        Assert.Equal(
            "New line.",
            Responsibility.Opening(agent, Question(QuestionState.Accepted, "An older answer.")));
    }

    [Fact]
    public void It_says_what_peers_read_and_marks_the_agents_words_as_the_agents()
    {
        var said = Responsibility.Described(
            Agent(),
            Question(QuestionState.Waiting, "Billing, never invoicing."),
            [],
            elsewhere: null);

        Assert.Contains("Peers read: 'Invoicing only.'", said, StringComparison.Ordinal);
        Assert.Contains("orders@test said this", said, StringComparison.Ordinal);
        Assert.Contains("its words, not this screen's", said, StringComparison.Ordinal);
        Assert.Contains("Billing, never invoicing.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_with_no_line_is_said_in_words_rather_than_as_an_empty_quote()
    {
        var said = Responsibility.Described(Agent(owns: null), null, [], elsewhere: null);

        Assert.Contains("no line yet", said, StringComparison.Ordinal);
        Assert.DoesNotContain("''", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accepted_line_peers_have_not_read_yet_is_said_apart_from_the_one_they_have()
    {
        var said = Responsibility.Described(
            Agent(owns: "Old line.", accepted: "New line.", source: OwnershipSource.Edited),
            null,
            [],
            elsewhere: null);

        Assert.Contains("Peers read: 'Old line.'", said, StringComparison.Ordinal);
        Assert.Contains("Accepted, not yet read by peers: 'New line.'", said, StringComparison.Ordinal);
    }

    [Fact]
    public void It_shows_what_the_others_claim_without_judging_it()
    {
        var said = Responsibility.Described(
            Agent(), null, [("billing@test", "Billing.")], elsewhere: null);

        Assert.Contains("billing@test: Billing.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("overlap", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_says_why_a_line_cannot_be_written_here_and_that_asking_still_can_be()
    {
        // Must fail against saying nothing: a disabled Save with no reason is a button that looks broken.
        var said = Responsibility.Described(Agent(), null, [], elsewhere: "That agent runs on another machine.");

        Assert.Contains("That agent runs on another machine.", said, StringComparison.Ordinal);
        Assert.Contains("still ask it to refresh", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agents_words_are_neutralised_before_they_are_drawn()
    {
        // The answer is somebody else's text. Must fail against drawing it raw: an escape sequence in an answer
        // would clear the screen above the very buttons that decide what happens to it.
        var said = Responsibility.Described(
            Agent(), Question(QuestionState.Waiting, "Billing.\u001b[2J"), [], elsewhere: null);

        Assert.DoesNotContain('\u001b', said);
    }

    [Fact]
    public void The_page_opens_holding_the_line_and_can_be_saved()
    {
        using var driving = Driving.Open();

        var page = new ResponsibilityDialog("described", "Invoicing only.", canSave: true);

        Assert.Equal("Invoicing only.", page.Typed);
        Assert.True(page.CanSave);
        Assert.Equal(string.Empty, page.Problem);
    }

    [Fact]
    public void A_line_the_store_would_refuse_cannot_be_saved_and_says_why()
    {
        // The statement's own rule, applied as it is typed. Must fail against letting it reach the write, where
        // the refusal would arrive after the page had closed and taken the words with it.
        using var driving = Driving.Open();

        var page = new ResponsibilityDialog("described", "One line" + (char)0x2028 + "and another.", canSave: true);

        Assert.False(page.CanSave);
        Assert.NotEqual(string.Empty, page.Problem);
    }

    [Fact]
    public void An_agent_on_another_machine_cannot_have_its_line_saved_from_here()
    {
        using var driving = Driving.Open();

        var page = new ResponsibilityDialog("described", "Invoicing only.", canSave: false);

        Assert.False(page.CanSave);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public void Each_way_out_reports_which_it_was_with_what_the_field_held(int which)
    {
        var choice = (ResponsibilityChoice)which;

        using var driving = Driving.Open();

        var page = new ResponsibilityDialog("described", "Invoicing only.", canSave: true);
        ResponsibilityOutcome? outcome = null;

        page.Done += (_, said) => outcome = said;
        page.Finish(choice);

        Assert.NotNull(outcome);
        Assert.Equal(choice, outcome!.Choice);
        Assert.Equal("Invoicing only.", outcome.Text);
    }

    [Fact]
    public void The_sentences_the_page_says_when_it_declines_to_act_say_what_to_do_next()
    {
        // A refusal with no next step is a dead end. Must fail against a bare "not now".
        Assert.Contains("Save it", Responsibility.StillWaiting, StringComparison.Ordinal);
        Assert.Contains("ask again", Responsibility.StillWaiting, StringComparison.Ordinal);
        Assert.Contains("ownership review", Responsibility.StillAsked, StringComparison.Ordinal);
        Assert.Contains("nothing was recorded", Responsibility.Unchanged, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_two_old_entries_are_one_title_now()
    {
        Assert.Equal("What this agent is responsible for", Responsibility.Title);
    }
}
