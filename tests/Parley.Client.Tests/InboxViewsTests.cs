using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>Which of the Inbox's three views an exchange is in, the chip an exchange in flight carries, and how a closed one ended.</summary>
/// <remarks>
/// <b>The owner, 2026-11-06:</b> answering a question took it off the Inbox, with no way to follow it. The views are
/// derived from the broker's graph and each exchange's state, never stored, so they are held here from the inputs alone.
/// </remarks>
[Unit]
public sealed class InboxViewsTests
{
    private static readonly AgentName Me = AgentName.Create("human-user@alpha");
    private static readonly AgentName Orders = AgentName.Create("orders@alpha");
    private static readonly AgentName Billing = AgentName.Create("billing@alpha");
    private static readonly AgentName Facilitator = AgentName.Create("parley-facilitator@alpha");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static Message Said(long id, long exchange, MessageType type, AgentName from, AgentName to, string json = "{\"ask\":\"x\"}", int hoursAgo = 1) =>
        new(id, exchange, type, from, to, "test", json, "talk", Now.AddHours(-hoursAgo), null);

    private static ExchangeSnapshot Open(long id) => new(id, ExchangeState.Open, Now.AddDays(-1), null);

    private static ExchangeSnapshot Closed(long id, int daysAgo, ExchangeOutcome outcome = ExchangeOutcome.Complete) =>
        new(id, ExchangeState.Closed, Now.AddDays(-daysAgo - 1), Now.AddDays(-daysAgo), outcome);

    private static InboxReading Read(
        IReadOnlyList<WaitingEdge> edges,
        IReadOnlyList<Message> messages,
        IReadOnlyList<ExchangeSnapshot> exchanges,
        IReadOnlyList<AgentRecord>? registry = null,
        IReadOnlyList<Flag>? flags = null,
        int older = 0) =>
        Inbox.Build(
            Me,
            edges,
            [new ConversationExport(messages, exchanges)],
            registry ?? [],
            flags ?? [],
            7,
            older,
            Now.AddDays(-7 * (older + 1)),
            Now.AddDays(-7 * older));

    private static AgentRecord Agent(AgentName name, AgentPresence presence, TurnStanding? turns = null, bool outside = false) =>
        new(name, "claude-code", AgentTier.Headless, null, null, null, true, AgentStatus.Active,
            Presence: presence, Turns: turns, OpenOutside: outside);

    [Fact]
    public void A_question_to_the_person_is_waiting_on_them_and_their_reply_puts_it_in_flight()
    {
        var asked = Said(1, 10, MessageType.Request, Orders, Me);

        var before = Read([new WaitingEdge(Orders, Me, 10)], [asked], [Open(10)]);

        Assert.Equal(InboxView.WaitingOnYou, Assert.Single(before.Rows).View);

        // The person replied with a clarify: the turn is the agent's, so the person now waits on it.
        var after = Read(
            [new WaitingEdge(Me, Orders, 10)],
            [asked, Said(2, 10, MessageType.Clarify, Me, Orders)],
            [Open(10)]);

        var row = Assert.Single(after.Rows);

        Assert.Equal(InboxView.InFlight, row.View);
        Assert.Equal(Orders, row.Agent);
        Assert.NotNull(row.Standing);
    }

    [Fact]
    public void A_request_the_person_sent_is_in_flight_until_the_agent_replies()
    {
        var sent = Said(1, 20, MessageType.Request, Me, Billing);

        Assert.Equal(InboxView.InFlight, Assert.Single(Read([new WaitingEdge(Me, Billing, 20)], [sent], [Open(20)]).Rows).View);

        var closed = Read([], [sent, Said(2, 20, MessageType.Result, Billing, Me, """{"outcomes":[{"id":"request","outcome":"done","text":"ok"}]}""")], [Closed(20, 0)]);

        var row = Assert.Single(closed.Rows);

        Assert.Equal(InboxView.Closed, row.View);
        Assert.Equal(Ending.Done, row.Ended!.How);
        Assert.Equal(Billing, row.Ended.By);
    }

    [Fact]
    public void An_exchange_is_in_one_view_only()
    {
        var reading = Read(
            [new WaitingEdge(Orders, Me, 10), new WaitingEdge(Me, Orders, 10)],
            [Said(1, 10, MessageType.Request, Orders, Me)],
            [Open(10)]);

        Assert.Equal(InboxView.WaitingOnYou, Assert.Single(reading.Rows).View);
    }

    [Fact]
    public void Somebody_elses_exchanges_are_in_no_view()
    {
        var reading = Read(
            [new WaitingEdge(Orders, Billing, 30)],
            [Said(1, 30, MessageType.Request, Orders, Billing), Said(2, 31, MessageType.Request, Orders, Billing)],
            [Open(30), Closed(31, 1)]);

        Assert.Empty(reading.Rows);
    }

    [Theory]
    [InlineData(1, "Blocked: missing information", """{"outcomes":[{"id":"request","outcome":"blocked","blocker":"missing_information","text":"who?"}]}""")]
    [InlineData(2, "Answered", """{"outcomes":[{"id":"request","outcome":"answered","text":"yes"}]}""")]
    [InlineData(3, "Received, nothing further to do", """{"outcomes":[{"id":"request","outcome":"acknowledged","text":"ok"}]}""")]
    public void A_closed_exchange_says_how_it_ended(long exchange, string words, string result)
    {
        var reading = Read(
            [],
            [Said(1, exchange, MessageType.Request, Orders, Me), Said(2, exchange, MessageType.Result, Me, Orders, result)],
            [Closed(exchange, 2, ExchangeOutcome.Complete)]);

        Assert.Equal(words, Assert.Single(reading.Rows).Ended!.Words);
    }

    [Fact]
    public void A_question_the_agent_settled_and_one_closed_without_an_answer_say_so()
    {
        var reading = Read(
            [],
            [Said(1, 40, MessageType.Request, Orders, Me), Said(2, 41, MessageType.Request, Billing, Me)],
            [Closed(40, 1, ExchangeOutcome.Settled), Closed(41, 1, ExchangeOutcome.Closed)]);

        Assert.Equal("Settled by the agent", reading.Rows.Single(row => row.Exchange == 40).Ended!.Words);
        Assert.Equal("Closed without an answer", reading.Rows.Single(row => row.Exchange == 41).Ended!.Words);
    }

    [Fact]
    public void A_request_the_broker_closed_reads_closed_not_done_whatever_its_results_say()
    {
        // As a real store holds it: closing a request (or its conversation) writes a Result each way, whose point says
        // "closed". Read as an ordinary Result, that fell through to Done, credited to whoever the last one named.
        const string closedByPerson = """{"outcomes":[{"id":"request","outcome":"closed","text":"closed by the person"}],"closed_by":"person"}""";

        var reading = Read(
            [],
            [
                Said(1, 42, MessageType.Request, Billing, Me),
                Said(2, 42, MessageType.Result, Billing, Me, closedByPerson),
                Said(3, 42, MessageType.Result, Me, Billing, closedByPerson),
            ],
            [Closed(42, 1, ExchangeOutcome.Closed)]);

        var ended = reading.Rows.Single(row => row.Exchange == 42).Ended!;

        Assert.Equal(Ending.Closed, ended.How);
        Assert.Equal("Closed without an answer", ended.Words);
        Assert.Null(ended.By);
    }

    [Fact]
    public void Closed_covers_its_window_and_the_one_before_is_older()
    {
        IReadOnlyList<Message> messages = [Said(1, 50, MessageType.Request, Orders, Me), Said(2, 51, MessageType.Request, Orders, Me)];
        IReadOnlyList<ExchangeSnapshot> exchanges = [Closed(50, 2), Closed(51, 9)];

        Assert.Equal([50L], Read([], messages, exchanges).Rows.Select(row => row.Exchange));
        Assert.Equal([51L], Read([], messages, exchanges, older: 1).Rows.Select(row => row.Exchange));
    }

    [Fact]
    public void An_exchange_closed_exactly_where_a_window_starts_is_in_the_older_window_only()
    {
        // Every instant is in exactly one window: a window runs from just after its start up to and including its end.
        IReadOnlyList<Message> messages = [Said(1, 52, MessageType.Request, Orders, Me)];
        IReadOnlyList<ExchangeSnapshot> exchanges = [Closed(52, 7)];

        Assert.Equal(Now.AddDays(-7), exchanges[0].ClosedUtc);
        Assert.Empty(Read([], messages, exchanges).Rows);
        Assert.Equal([52L], Read([], messages, exchanges, older: 1).Rows.Select(row => row.Exchange));
    }

    [Fact]
    public void A_closed_exchange_with_every_message_pruned_is_not_claimed_as_the_persons()
    {
        // Retention removed the messages: nothing left says the person was on it, so it is not listed as theirs.
        Assert.Empty(Read([], [], [Closed(60, 1)]).Rows);
    }

    [Fact]
    public void No_follow_up_is_offered_to_the_facilitator()
    {
        var reading = Read(
            [new WaitingEdge(Facilitator, Me, 70)],
            [Said(1, 70, MessageType.Request, Facilitator, Me)],
            [Open(70)]);

        Assert.False(Assert.Single(reading.Rows).FollowUp);
    }

    [Theory]
    [InlineData(AgentPresence.Running, true, true, false, AgentState.Working)]
    [InlineData(AgentPresence.Running, true, false, false, AgentState.Idle)]
    [InlineData(AgentPresence.Running, false, false, false, AgentState.Running)]
    [InlineData(AgentPresence.Startable, true, false, false, AgentState.NotRunning)]
    [InlineData(AgentPresence.Stranded, true, false, false, AgentState.CannotStart)]
    [InlineData(AgentPresence.Running, true, true, true, AgentState.OpenOutside)]
    public void The_chip_says_only_what_the_registry_says(AgentPresence presence, bool reported, bool running, bool outside, AgentState expected)
    {
        var turns = new TurnStanding(reported, running ? Now.AddMinutes(-5) : null, null, true, 0);

        Assert.Equal(expected, InFlightStanding.From(Agent(Orders, presence, turns, outside), [], 1).State);
    }

    [Fact]
    public void An_agent_waiting_on_its_own_background_work_is_never_idle_anywhere()
    {
        // SPEC.md §11.6: its turn ended while sub-agents it started run on. The Inbox chip, the agents list and the room all
        // say so, in the same words.
        var turns = new TurnStanding(true, null, Now.AddMinutes(-1), true, 0, BackgroundTasks: 2);
        var agent = Agent(Orders, AgentPresence.Running, turns);

        var standing = InFlightStanding.From(agent, [], 1);

        Assert.Equal(AgentState.Background, standing.State);
        Assert.Contains("2 tasks", standing.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "waiting on 2 background tasks",
            Parley.Client.Views.Activities.Now(agent, Now).Words,
            StringComparison.Ordinal);

        var row = Assert.Single(Parley.Client.Views.Standing.Join(
            [agent], new HashSet<string>(StringComparer.Ordinal), "parley-console", Orders.Machine));

        Assert.Equal("waiting on 2 background tasks", row.Standing);
        Assert.NotEqual(Parley.Client.Views.Standing.Ready, row.Standing);
    }

    [Fact]
    public void An_agent_the_registry_does_not_list_is_unknown_and_never_idle()
    {
        Assert.Equal(AgentState.Unknown, InFlightStanding.From(null, [], 1).State);
    }

    [Fact]
    public void A_flag_on_the_exchange_is_shown_beside_the_agent_and_only_for_that_exchange()
    {
        var running = Agent(Orders, AgentPresence.Running, new TurnStanding(true, null, null, true, 0));
        IReadOnlyList<Flag> flags = [new Flag(FlagKind.NoProgress, 80, "talk", Orders, Me, Now.AddDays(-1))];

        var flagged = InFlightStanding.From(running, flags, 80);

        Assert.Equal(AgentState.Idle, flagged.State);
        Assert.Equal("Stalled", flagged.FlagWords);
        Assert.Null(InFlightStanding.From(running, flags, 81).Flag);
    }

    [Fact]
    public void No_chip_words_claim_the_agent_is_working_on_the_persons_message()
    {
        foreach (var state in Enum.GetValues<AgentState>())
        {
            var standing = new InFlightStanding(state, null, null);

            Assert.DoesNotContain("your", standing.Chip + " " + standing.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }
}
