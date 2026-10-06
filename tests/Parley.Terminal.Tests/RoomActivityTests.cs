using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the room says what each participant is doing, including work between two agents, without being cut off.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-03</b>: one agent sent another a request and nothing on the screen said the second
/// was doing anything. The room described only what stood between each agent and the person, and the machine half of
/// every name pushed even that off the edge of the column. The owner chose marks in front of each name, with a legend.
/// </para>
/// </remarks>
public sealed class RoomActivityTests
{
    private static readonly AgentName Asker = AgentName.Create("orders@here");
    private static readonly AgentName Worker = AgentName.Create("billing@here");
    private static readonly AgentName Idle = AgentName.Create("inventory@here");
    private static readonly AgentName Person = AgentName.Create("human-user@alpha");

    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A conversation in which one agent asked another, and the second has picked it up.</summary>
    private static ConversationExport AgentAsksAgent(bool pickedUp) =>
        new(
            [
                new Message(1, 7, MessageType.Request, Asker, Worker, "model", """{"ask":"confirm the format?"}""", "room",
                    T0, pickedUp ? T0.AddMinutes(1) : null),
            ],
            [new ExchangeSnapshot(7, ExchangeState.Open, T0, null)]);

    private static AgentRecord Agent(AgentName name, AgentPresence presence, TurnStanding? turns = null) =>
        new(name, "mcp", AgentTier.Headless, null, null, null, true, AgentStatus.Active, Presence: presence, Turns: turns);

    [Fact]
    public void The_room_draws_the_marks_short_names_how_long_and_a_legend()
    {
        var room = new RoomRegion();
        var now = T0.AddMinutes(4);

        room.Show(
            [new PairState(Asker, null, Turn.Nobody), new PairState(Worker, null, Turn.Nobody), new PairState(Idle, null, Turn.Nobody)],
            [],
            seated: true,
            Person,
            activity: Activities.Of(
                AgentAsksAgent(pickedUp: true),
                [new WaitingEdge(Asker, Worker, 7)],
                [
                    Agent(Asker, AgentPresence.Running, TurnStanding.Unreported),
                    Agent(Worker, AgentPresence.Running, new TurnStanding(true, T0.AddMinutes(1), null, false, 0)),
                    Agent(Idle, AgentPresence.Startable),
                ]),
            now: now);

        var rows = room.Written;

        // The role alone: nobody else in the room shares it, and "@here" on every row is what pushed the state off the edge.
        Assert.Contains(rows, row => row.Contains($"{Activities.WorkingMark} billing — working 3m", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains($"{Activities.RunningMark} orders", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains($"{Activities.NotRunningMark} inventory", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("@here", StringComparison.Ordinal));

        Assert.All(Activities.Legend, line => Assert.Contains(line, rows));
    }

    [Fact]
    public void An_agent_whose_turn_ended_owing_work_reads_as_stopped_with_what_it_was_refused()
    {
        // **The owner's report, specs/024**: nearly three hours of "working" after a refused command ended the turn. Picked
        // up at 9:01, its turn ended at 9:05 with one call refused, and it has said nothing since.
        var room = new RoomRegion();

        room.Show(
            [new PairState(Asker, null, Turn.Nobody), new PairState(Worker, null, Turn.Nobody)],
            [],
            seated: true,
            Person,
            activity: Activities.Of(
                AgentAsksAgent(pickedUp: true),
                [new WaitingEdge(Asker, Worker, 7)],
                [
                    Agent(Asker, AgentPresence.Running, new TurnStanding(true, null, T0.AddMinutes(1), true, 0)),
                    Agent(Worker, AgentPresence.Running, new TurnStanding(true, null, T0.AddMinutes(5), true, 1)),
                ]),
            now: T0.AddMinutes(5).AddHours(2).AddMinutes(48));

        var text = string.Join("\n", room.Written);

        Assert.Contains($"{Activities.StoppedMark} billing", text, StringComparison.Ordinal);
        Assert.Contains("stopped 2h48m", text, StringComparison.Ordinal);
        Assert.Contains("1 permission refused", text, StringComparison.Ordinal);
        Assert.DoesNotContain("working", text.Replace(Activities.Legend[1], string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // And the asker, which asked and is waiting, is waiting rather than stopped.
        Assert.Contains("waiting on billing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_agents_of_one_role_keep_their_whole_names()
    {
        // The machine is what tells them apart, so it stays.
        var elsewhere = AgentName.Create("billing@there");
        var room = new RoomRegion();

        room.Show([new PairState(Worker, null, Turn.Nobody), new PairState(elsewhere, null, Turn.Nobody)], [], seated: true, Person);

        Assert.Contains(room.Written, row => row.Contains(Worker.Value, StringComparison.Ordinal));
        Assert.Contains(room.Written, row => row.Contains(elsewhere.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void A_status_too_long_for_the_column_goes_under_the_name_rather_than_off_the_edge()
    {
        // **The owner's screenshot of 2026-10-03**, with the marks already in: "<long role> — worki" at the edge of a
        // column about thirty characters wide. Placeholder names of the same length.
        var waiting = AgentName.Create("inventoryledger@here");
        var working = AgentName.Create("inventoryledgerwarrant@here");
        var room = new RoomRegion { Frame = new System.Drawing.Rectangle(0, 0, 32, 40) };

        room.Show(
            [new PairState(waiting, 3, Turn.Mine), new PairState(working, null, Turn.Nobody)],
            [new OutstandingItem(waiting, [new WaitingQuestion(3, T0, null, "which schema?")])],
            seated: true,
            Person,
            activity: new Dictionary<AgentName, Activity>
            {
                [waiting] = new(Doing.Running, null),
                [working] = new(Doing.Working, T0),
            },
            now: T0.AddMinutes(3));

        var width = room.Viewport.Width;
        var rows = room.Written;

        Assert.True(width is > 0 and < 40, $"the room was laid out {width} wide");
        Assert.All(rows, row => Assert.True(row.Length <= width, $"{row.Length} > {width}: '{row}'"));

        // The status is on screen, on the row under the name it belongs to.
        var name = rows.ToList().FindIndex(row => row.Contains(working.Role, StringComparison.Ordinal));

        Assert.Contains("working 3m", rows[name + 1], StringComparison.Ordinal);
        Assert.Contains(rows, row => row.Contains("waiting on you", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Trim() == "inventoryledger (1)");
    }

    [Fact]
    public void Ticking_somebody_keeps_every_mark()
    {
        // A tick changes who a line goes to, not what anybody is doing. Redrawn without the activity, every mark went blank
        // until the next read.
        var room = new RoomRegion();

        room.Show(
            [new PairState(Asker, null, Turn.Nobody), new PairState(Worker, null, Turn.Nobody)],
            [],
            seated: true,
            Person,
            activity: new Dictionary<AgentName, Activity> { [Worker] = new(Doing.Working, T0), [Asker] = new(Doing.Running, null) },
            now: T0.AddMinutes(2));

        room.Address([Worker]);

        Assert.Contains(room.Written, row => row.Contains($"{Activities.WorkingMark} billing", StringComparison.Ordinal));
        Assert.Contains(room.Written, row => row.Contains($"{Activities.RunningMark} orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_room_keeps_itself_current_while_the_conversation_is_followed()
    {
        // It was read only on arriving, so the room went on describing the moment the conversation opened: an agent that
        // joined, or picked a request up, changed nothing there until somebody left and came back.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct, followEvery: TimeSpan.FromMilliseconds(20));
        var shell = seated.Shell;

        // Listed under "not in it yet", which is not being in the conversation.
        Assert.DoesNotContain(InConversation(shell.Room.Written), row => row.Contains(Seated.Billing.Role, StringComparison.Ordinal));

        // Billing joins the conversation; the test does not go anywhere, so only following can show it.
        await seated.AskAsync(Seated.Billing, Seated.Conversation, ct, go: false);

        await Eventually.UntilAsync(
            () => InConversation(shell.Room.Written).Any(row => row.Contains(Seated.Billing.Role, StringComparison.Ordinal)),
            "the room never showed the agent that joined",
            ct);
    }

    /// <summary>The rows under the participants heading, up to the blank row that ends them.</summary>
    private static IEnumerable<string> InConversation(IReadOnlyList<string> rows) =>
        rows.SkipWhile(row => row != RoomRegion.Participants).Skip(1).TakeWhile(row => row.Length > 0);
}
