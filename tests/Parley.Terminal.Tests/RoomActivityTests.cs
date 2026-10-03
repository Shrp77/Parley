using Parley.Client.Seating;
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

    private static IReadOnlyList<AgentRecord> Registry(params (AgentName Name, AgentPresence Presence)[] agents) =>
        [.. agents.Select(a => new AgentRecord(a.Name, "mcp", AgentTier.Pull, null, null, null, true, AgentStatus.Active, Presence: a.Presence))];

    [Fact]
    public void An_agent_that_picked_up_another_agents_request_is_working_since_it_did()
    {
        var activity = Activities.Of(
            AgentAsksAgent(pickedUp: true),
            [new WaitingEdge(Asker, Worker, 7)],
            Registry((Asker, AgentPresence.Running), (Worker, AgentPresence.Running), (Idle, AgentPresence.Startable)));

        Assert.Equal(new Activity(Doing.Working, T0.AddMinutes(1)), activity[Worker]);
        Assert.Equal(Doing.Running, activity[Asker].Doing);
        Assert.Equal(Doing.NotRunning, activity[Idle].Doing);
    }

    [Fact]
    public void A_request_not_yet_picked_up_is_not_work_yet()
    {
        // Working means taken: a message waiting in somebody's inbox says nothing about what they are doing.
        var activity = Activities.Of(
            AgentAsksAgent(pickedUp: false),
            [new WaitingEdge(Asker, Worker, 7)],
            Registry((Worker, AgentPresence.Running)));

        Assert.Equal(Doing.Running, activity[Worker].Doing);
    }

    [Fact]
    public void Work_in_another_conversation_is_not_this_ones()
    {
        // The wait-for graph is deployment-wide; only this conversation's exchanges count here.
        var activity = Activities.Of(
            AgentAsksAgent(pickedUp: true),
            [new WaitingEdge(Asker, Idle, 99)],
            Registry((Idle, AgentPresence.Running)));

        Assert.Equal(Doing.Running, activity[Idle].Doing);
    }

    [Fact]
    public void A_registry_that_could_not_be_read_claims_nothing()
    {
        var activity = Activities.Of(AgentAsksAgent(pickedUp: false), [], registry: null);

        Assert.False(activity.ContainsKey(Worker));
    }

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
                Registry((Asker, AgentPresence.Running), (Worker, AgentPresence.Running), (Idle, AgentPresence.Startable))),
            now: now);

        var rows = room.Written;

        // The role alone: nobody else in the room shares it, and "@here" on every row is what pushed the state off the edge.
        Assert.Contains(rows, row => row.Contains($"{RoomRegion.WorkingMark} billing{RoomRegion.WorkingFor}3m", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains($"{RoomRegion.RunningMark} orders", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains($"{RoomRegion.NotRunningMark} inventory", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("@here", StringComparison.Ordinal));

        Assert.All(RoomRegion.Legend, line => Assert.Contains(line, rows));
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
            [new OutstandingItem(waiting, $"{waiting.Value} — waiting for your reply", 3, null)],
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
        Assert.Contains(rows, row => row.Contains("waiting for your reply", StringComparison.Ordinal));
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

        Assert.Contains(room.Written, row => row.Contains($"{RoomRegion.WorkingMark} billing", StringComparison.Ordinal));
        Assert.Contains(room.Written, row => row.Contains($"{RoomRegion.RunningMark} orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_room_keeps_itself_current_while_the_conversation_is_followed()
    {
        // It was read only on arriving, so the room went on describing the moment the conversation opened: an agent that
        // joined, or picked a request up, changed nothing there until somebody left and came back.
        var ct = TestContext.Current.CancellationToken;

        await using var seated = await Seated.OpenAsync(ct, followEvery: TimeSpan.FromMilliseconds(20));
        var shell = seated.Shell;

        Assert.DoesNotContain(shell.Room.Written, row => row.Contains(Seated.Billing.Role, StringComparison.Ordinal));

        // Billing joins the conversation; the test does not go anywhere, so only following can show it.
        await seated.AskAsync(Seated.Billing, Seated.Conversation, ct, go: false);

        await Eventually.UntilAsync(
            () => shell.Room.Written.Any(row => row.Contains(Seated.Billing.Role, StringComparison.Ordinal)),
            "the room never showed the agent that joined",
            ct);
    }

    [Theory]
    [InlineData(30, "<1m")]
    [InlineData(180, "3m")]
    [InlineData(7500, "2h05m")]
    public void How_long_is_said_in_few_characters(int seconds, string expected) =>
        Assert.Equal(expected, Activities.For(T0, T0.AddSeconds(seconds)));
}
