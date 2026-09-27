using Grpc.Net.Client;
using Parley.Cli.Tests;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Parley.Store;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// Asking an agent what it owns, and accepting what it answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two operations and one file, because they are two ends of one act.</b> The ask is offered in the
/// agents pane, where the row is the agent; the acceptance is offered in the transcript, where the
/// conversation is. A surface that offered only the first would spend a turn on a question nobody could
/// then act on.
/// </para>
/// <para>
/// <b>What each of these fails against is written beside it.</b> A probe that cannot fail is this
/// repository's most-repeated defect, and the wording assertions here are not cosmetic: FR-003 is a
/// promise about what the agent was <em>told</em>, so the words are the requirement.
/// </para>
/// </remarks>
public sealed class AskingWhatItOwnsTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    private static readonly DateTimeOffset When = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static AgentRecord Agent(string name) =>
        new(
            AgentName.Create(name),
            "test",
            AgentTier.Full,
            "/src/thing",
            null,
            null,
            false,
            AgentStatus.Active,
            "the thing",
            AgentPresence.Startable);

    private static Message Proposed(string text) =>
        new(
            2,
            1,
            MessageType.Result,
            Orders,
            AgentName.Create("arvind@test"),
            "some-model-9",
            $$"""{"outcomes":[{"id":"request","outcome":"answered","text":"{{text}}"}]}""",
            OwnershipAsk.Conversation(Orders, When),
            When.AddMinutes(1),
            null);

    [Fact]
    public async Task The_agents_pane_offers_the_ask_for_the_agent_under_the_cursor()
    {
        // Must fail against the ask existing only as a key. This surface's rule is that no action is
        // reachable only by pressing something nothing advertises.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        shell.Focus(2);

        Assert.Contains(
            shell.Actions(),
            a => a.Label.StartsWith(Ownership.AskTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ask_is_offered_without_a_seat_rather_than_hidden()
    {
        // **0.28.2's defect, guarded from the other side.** Starting a conversation was hidden while
        // no seat was held and nothing said one was needed, so the owner reached for an instruction
        // instead. Must fail against gating this action on `Held`.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        shell.Focus(2);

        Assert.Null(shell.Held);
        Assert.Contains(
            shell.Actions(),
            a => a.Label.StartsWith(Ownership.AskTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Asking_with_no_seat_says_a_seat_is_needed_and_how_to_take_one()
    {
        // Must fail against a silent refusal, and against "your seat was released" -- 0.29.1 records
        // that sentence being the defect when the seat was never held in the first place.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("orders@test")], NodeCatalogue.Empty, null);
        shell.Focus(2);

        var said = string.Empty;

        await Ownership.AskAsync(driving.App, null, Orders, When, _ => { }, reported =>
        {
            said = reported;

            return Task.CompletedTask;
        });

        Assert.Equal(Ownership.NoSeat, said);
        Assert.Contains("'j'", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_sent_when_there_is_no_seat_to_send_it_from()
    {
        // The refusal must also not queue work. Must fail against a version that says a seat is
        // needed and then sends anyway, which is the shape of every half-applied guard in this file's
        // neighbours.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        var queued = 0;

        await Ownership.AskAsync(
            driving.App, null, Orders, When, _ => queued++, _ => Task.CompletedTask);

        Assert.Equal(0, queued);
    }

    [Fact]
    public void What_the_agent_will_be_told_is_on_the_screen_before_it_is_sent()
    {
        // **FR-003 is a promise about what the agent was told**, so a surface that summarised the ask
        // would be the one place the promise could quietly stop being true. Must fail against a
        // description that paraphrases rather than shows.
        // **Compared on the words rather than on the layout.** A first version of this asserted
        // "Nothing is routed by it automatically" and failed: the constant wraps between "Nothing is"
        // and "routed", so the substring existed in the prose and not in the string. Re-wrapping the
        // ask is not a change to what the agent is told, and a test that broke on it would be
        // measuring the margin. Changing the promise still fails this.
        var asked = Words(Ownership.Asked(Orders, OwnershipAsk.Conversation(Orders, When)));

        Assert.Contains("What do you own in this deployment", asked, StringComparison.Ordinal);
        Assert.Contains("it costs each of them context every time", asked, StringComparison.Ordinal);
        Assert.Contains("Nothing is routed by it automatically", asked, StringComparison.Ordinal);
        Assert.Contains("what you do not own", asked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One line of words, so an assertion is about prose and not about wrapping.</summary>
    private static string Words(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void What_the_person_reads_first_says_the_ask_costs_a_turn_and_writes_nothing()
    {
        // The cost is named before it is spent, and the harmlessness is named too: a person who
        // believed asking wrote something would not ask.
        var asked = Ownership.Asked(Orders, OwnershipAsk.Conversation(Orders, When));

        Assert.Contains("one turn", asked, StringComparison.Ordinal);
        Assert.Contains("Nothing is written by asking", asked, StringComparison.Ordinal);
    }

    [Fact]
    public void The_choice_that_changes_nothing_is_the_one_under_the_cursor()
    {
        // Every dialog on this surface opens on the answer that does nothing, and a description is
        // decorative if the destructive answer is what Enter takes.
        Assert.StartsWith("Do not", Ownership.Leave, StringComparison.Ordinal);
        Assert.NotEqual(Ownership.Leave, Ownership.Ask);
        Assert.Contains("spend", Ownership.Ask, StringComparison.Ordinal);
        Assert.Contains("nothing was spent", Ownership.Left, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transcript_offers_the_acceptance_in_an_ownership_discussion()
    {
        // **Recognised by the conversation's identifier and never by a marker in a payload.** §13
        // tells a receiver a payload is inert data, and 017's chip rule is what has kept that true.
        // Must fail against the action existing only as a key, as its neighbour in the agents pane
        // must.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);
        var discussion = OwnershipAsk.Conversation(Orders, When);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, discussion);

        shell.Transcript.Show(discussion, "alpha", new ConversationExport([Proposed("Orders.")], []), null);
        shell.Focus(1);

        Assert.Contains(
            shell.Actions(),
            a => a.Label.StartsWith(Ownership.AcceptTitle, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ordinary_conversation_does_not_offer_to_accept_an_ownership_line()
    {
        // Must fail against offering the acceptance everywhere, which would invite somebody to accept
        // an ownership line out of an ordinary piece of work -- and the line it wrote would be
        // whatever that agent last said about anything.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Conversation, "the-build-is-broken");

        shell.Transcript.Show(
            "the-build-is-broken", "alpha", new ConversationExport([Proposed("Orders.")], []), null);
        shell.Focus(1);

        Assert.DoesNotContain(
            shell.Actions(),
            a => a.Label.StartsWith(Ownership.AcceptTitle, StringComparison.Ordinal));
    }

    [Fact]
    public void The_proposal_is_shown_as_the_agents_words_with_who_said_it_and_when()
    {
        // **FR-013.** Question 10's amendment turns on a reader being able to tell a model's sentence
        // from a person's, so must fail against a screen that shows the text alone: the record of
        // `proposed` against `composed` would then be a claim nobody could check.
        var reviewed = Ownership.Reviewed(
            Orders,
            "Invoicing only.",
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."));

        Assert.Contains("Orders and invoicing.", reviewed, StringComparison.Ordinal);
        Assert.Contains("orders@test proposed this", reviewed, StringComparison.Ordinal);
        Assert.Contains("its words, not this screen's", reviewed, StringComparison.Ordinal);
        Assert.Contains("2026-09-27", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void What_peers_read_today_is_shown_beside_what_was_proposed()
    {
        // **FR-014.** A person deciding whether to accept is comparing two lines, and a screen
        // showing only the new one makes them remember the old one.
        var reviewed = Ownership.Reviewed(
            Orders,
            "Invoicing only.",
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."));

        Assert.Contains("'Invoicing only.'", reviewed, StringComparison.Ordinal);
        Assert.Contains("read", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_with_no_line_yet_is_said_in_words_rather_than_as_an_empty_quote()
    {
        // A blank pair of quotes reads as a bug, which 0.25.1 records paying for in a dangling colon.
        var reviewed = Ownership.Reviewed(
            Orders,
            null,
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."));

        Assert.Contains("no line yet", reviewed, StringComparison.Ordinal);
        Assert.DoesNotContain("''", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_line_in_the_registry_is_the_same_as_no_line()
    {
        // `OwnershipStatement.Read` collapses blank to absent, so a variable set to an empty string
        // and one never set say the same thing. Must fail against quoting whitespace back.
        var reviewed = Ownership.Reviewed(
            Orders,
            "   ",
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."));

        Assert.Contains("no line yet", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepting_as_it_stands_and_editing_it_are_told_apart_on_the_screen()
    {
        // The field opens holding the agent's sentence, so Enter accepts and typing edits -- and the
        // screen has to say that the two are recorded differently, because the difference is the
        // whole of what question 10's amendment rests on.
        var reviewed = Ownership.Reviewed(
            Orders,
            null,
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."));

        Assert.Contains("Enter accepts it as it stands", reviewed, StringComparison.Ordinal);
        Assert.Contains("records it as yours rather than as", reviewed, StringComparison.Ordinal);
        Assert.Contains("next starts", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ask_that_has_not_been_answered_says_so_over_a_real_broker()
    {
        // **End to end over a socket, and the whole of what it proves is the order of two checks.**
        // The ask is sent from a seat, so the discussion holds exactly one message -- the person's,
        // addressed to the agent. Reading it back must therefore find the agent as the *recipient*
        // and then find that it has said nothing.
        //
        // Must fail two ways. Against `Subject` reading senders alone, this says "which single agent"
        // -- the surface unable to name an agent it had just asked. And against a review that opens a
        // dialog regardless, it offers an empty line for acceptance and then writes it.
        var ct = TestContext.Current.CancellationToken;

        using var connection = ParleyDatabase.OpenInMemory();

        await new AgentRepository(connection).UpsertAsync(
            new AgentRecord(Orders, "mcp", AgentTier.Pull, null, null, null, false, AgentStatus.Active),
            ct);

        await using var broker = LoopbackOperator.Start(connection);
        using var driving = Driving.Open();
        using var directory = Wired(broker);
        using var seats = new SeatBook(directory.AgentsFor);
        using var shell = await SeatedAsync(driving, directory, seats);

        var discussion = OwnershipAsk.Conversation(Orders, When);

        var sent = await new SeatVoice(directory.AgentsFor(directory.Projects[0]), shell.Held!.Seat)
            .RequestAsync(Orders, OwnershipAsk.Text, discussion, ct);

        Assert.Contains("Sent.", sent, StringComparison.Ordinal);

        var said = string.Empty;

        await Ownership.ReviewAsync(
            driving.App,
            new BrokerClient(directory.ClientFor(directory.Projects[0])),
            discussion,
            [],
            directory.Projects[0].Name.Value,
            _ => { },
            reported =>
            {
                said = reported;

                return Task.CompletedTask;
            },
            ct);

        Assert.Equal(Ownership.NothingProposed, said);
    }

    private static BrokerDirectory Wired(LoopbackOperator broker) =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "unused.db", broker.Address, null)],
            _ => GrpcChannel.ForAddress(broker.Address));

    private static async Task<Shell> SeatedAsync(
        Driving.Session driving, BrokerDirectory directory, SeatBook seats)
    {
        var shell = new Shell(directory) { Seats = seats };

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Join();

        var dialog = (TextDialog)driving.Find(AgentsRegion.JoinTitle);

        driving.Type("arvind");
        dialog.Finish(taken: true);

        await shell.Settled;

        Assert.NotNull(shell.Held);

        return shell;
    }

    [Fact]
    public void Accepting_it_unchanged_is_recorded_as_the_agents_words()
    {
        // **Must fail against recording every acceptance as one thing**, which is the obvious
        // implementation and the one that makes the record worth nothing: §17 question 10's amendment
        // permits an agent-drafted line only because a reader can tell it from a person's.
        Assert.Equal(
            OwnershipSource.Proposed,
            Ownership.Chosen("Orders and invoicing.", "Orders and invoicing."));
    }

    [Fact]
    public void Changing_it_before_accepting_is_recorded_as_the_persons_words()
    {
        // Must fail against comparing loosely -- a changed word, case or dash is an edit, because what
        // is being claimed is that the words are the agent's.
        Assert.Equal(
            OwnershipSource.Edited,
            Ownership.Chosen("Orders only.", "Orders and invoicing."));

        Assert.Equal(
            OwnershipSource.Edited,
            Ownership.Chosen("orders and invoicing.", "Orders and invoicing."));

        Assert.Equal(
            OwnershipSource.Edited,
            Ownership.Chosen("Orders and invoicing", "Orders and invoicing."));
    }

    [Fact]
    public void Whitespace_the_dialog_added_is_not_an_edit_somebody_made()
    {
        // The field trims, so surrounding whitespace is the dialog's doing. Must fail against a strict
        // comparison of the raw strings, which would record `edited` for a person who pressed Enter.
        Assert.Equal(
            OwnershipSource.Proposed,
            Ownership.Chosen("  Orders and invoicing.  ", "Orders and invoicing."));
    }

    [Fact]
    public void What_the_others_already_claim_is_shown_beside_the_proposal()
    {
        // **FR-017.** A person deciding whether a line is right is deciding it against what is already
        // claimed, and they will not remember nineteen lines. Must fail against a screen that shows the
        // proposal alone.
        var reviewed = Ownership.Reviewed(
            Orders,
            null,
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."),
            [("inventory@test", "Stock and shipping."), ("billing@test", "Invoicing.")]);

        Assert.Contains("What the others already say they own", reviewed, StringComparison.Ordinal);
        Assert.Contains("inventory@test: Stock and shipping.", reviewed, StringComparison.Ordinal);
        Assert.Contains("billing@test: Invoicing.", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlap_is_shown_without_a_word_about_which_is_right()
    {
        // **FR-030 and Principle II.** `billing` claims invoicing and so does the proposal. Must fail
        // against a screen that flags a clash, ranks the claims or warns -- no agent has authority over
        // another, and a person is reading this precisely so they can decide.
        var reviewed = Ownership.Reviewed(
            Orders,
            null,
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."),
            [("billing@test", "Invoicing.")]);

        Assert.Contains("billing@test: Invoicing.", reviewed, StringComparison.Ordinal);

        foreach (var verdict in new[] { "conflict", "overlap", "clash", "already claimed by", "instead" })
        {
            Assert.DoesNotContain(verdict, reviewed, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_project_with_no_other_lines_says_nothing_about_neighbours()
    {
        // The first agent asked in a deployment has no neighbours, and an empty heading would read as a
        // list that failed to load.
        var reviewed = Ownership.Reviewed(
            Orders,
            null,
            new OwnershipProposal(Proposed("Orders and invoicing."), "Orders and invoicing."),
            []);

        Assert.DoesNotContain("What the others", reviewed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restart_offer_says_what_it_costs_and_leaving_it_is_a_complete_answer()
    {
        // **FR-019, and the reason it is offered rather than done.** SPEC.md §12 says what stopping an
        // agent costs: the process is what held everything it had learned. Accepting a sentence about
        // scope is not a reason to spend that silently.
        //
        // Must fail against a description that presents leaving it as a failure, or that omits the
        // cost -- either would make the safe answer look like the wrong one.
        var said = Ownership.Restarted(Orders);

        Assert.Contains("do not read it yet", said, StringComparison.Ordinal);
        Assert.Contains("next start", said, StringComparison.Ordinal);
        Assert.Contains("everything it has learned", said, StringComparison.Ordinal);
        Assert.Contains("leaving it is", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_answer_that_changes_nothing_is_the_one_under_the_cursor()
    {
        // Every dialog on this surface opens on the answer that does nothing, and the restart is the
        // one here that costs a running session.
        Assert.StartsWith("Leave", Ownership.LeaveIt, StringComparison.Ordinal);
        Assert.Contains("Restart it now", Ownership.RestartIt, StringComparison.Ordinal);
        Assert.NotEqual(Ownership.LeaveIt, Ownership.RestartIt);
        Assert.Contains("peers read", Ownership.RestartTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_reports_what_was_asked_for_rather_than_claiming_the_line_is_live()
    {
        // **`Operators.Start` asks a node to start a role.** Whether the agent attaches, reaches this
        // broker and registers is a different question, and the registry is the only thing that
        // answers it -- 0.25.2 records a screen predicting a registration instead of checking one.
        //
        // Must fail against "peers now read it", which is a sentence about the next few seconds
        // printed as a result.
        Assert.Contains("Asked its node", Ownership.Restarting, StringComparison.Ordinal);
        Assert.DoesNotContain("peers now read", Ownership.Restarting, StringComparison.Ordinal);

        // Declining says what is true of the deployment, and names where the fact stays visible.
        Assert.Contains("until the agent next starts", Ownership.NotRestarted, StringComparison.Ordinal);
        Assert.Contains("roster", Ownership.NotRestarted, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_accept_and_nobody_to_accept_it_from_are_different_sentences()
    {
        // Two states a first draft would collapse: the agent has not answered, and the discussion
        // does not say which single agent it is about. 0.29.1 records what collapsing two states of
        // one screen costs -- a person told the wrong thing to do next.
        Assert.NotEqual(Ownership.NothingProposed, Ownership.NoSubject);
        Assert.Contains("has not answered yet", Ownership.NothingProposed, StringComparison.Ordinal);
        Assert.Contains("next turn", Ownership.NothingProposed, StringComparison.Ordinal);
        Assert.Contains("which single agent", Ownership.NoSubject, StringComparison.Ordinal);
        Assert.Contains("agents pane instead", Ownership.NoSubject, StringComparison.Ordinal);
    }
}
