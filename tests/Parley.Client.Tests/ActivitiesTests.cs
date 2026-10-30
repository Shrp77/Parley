using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// That what a participant is doing is derived from its reported turns, and never guessed.
/// </summary>
/// <remarks>
/// <b>Working is derived from a running turn</b>, not from "picked up and not answered", or an agent would read as working
/// for hours after a refused command ended its turn silently. A turn
/// that ended with work still owed reads as stopped, with what it was refused.
/// </remarks>
[Integration]
public sealed class ActivitiesTests
{
    private static readonly AgentName Asker = AgentName.Create("orders@here");
    private static readonly AgentName Worker = AgentName.Create("billing@here");
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One request from the asker to the worker, picked up at T0 + 1m, still open.</summary>
    private static ConversationExport Asked() =>
        new(
            [new Message(1, 7, MessageType.Request, Asker, Worker, "model", """{"ask":"x"}""", "room", T0, T0.AddMinutes(1))],
            [new ExchangeSnapshot(7, ExchangeState.Open, T0, null)]);

    private static readonly IReadOnlyList<WaitingEdge> AskerWaitsOnWorker = [new WaitingEdge(Asker, Worker, 7)];

    private static AgentRecord Agent(AgentName name, TurnStanding? turns, AgentPresence presence = AgentPresence.Running) =>
        new(name, "mcp", AgentTier.Headless, null, null, null, true, AgentStatus.Active, Presence: presence, Turns: turns);

    private static Activity Of(TurnStanding? workerTurns, TurnStanding? askerTurns = null) =>
        Activities.Of(Asked(), AskerWaitsOnWorker, [Agent(Worker, workerTurns), Agent(Asker, askerTurns)])[Worker];

    [Fact]
    public void Working_only_while_a_turn_is_running()
    {
        var activity = Of(new TurnStanding(true, T0.AddMinutes(2), null, false, 0));

        Assert.Equal(Doing.Working, activity.Doing);
        Assert.Equal("working 3m", Activities.Says(activity, T0.AddMinutes(5)));
    }

    [Fact]
    public void A_turn_that_ended_after_the_pickup_with_work_still_owed_is_stopped_and_says_what_was_refused()
    {
        // Picked up at 9:01, the turn ended at 9:05 with a command refused, nothing since.
        var activity = Of(new TurnStanding(true, null, T0.AddMinutes(5), true, 1));

        Assert.Equal(Doing.Stopped, activity.Doing);
        Assert.Equal("stopped 2h48m — 1 permission refused", Activities.Says(activity, T0.AddMinutes(5).AddHours(2).AddMinutes(48)));
        Assert.Equal(Activities.StoppedMark, Activities.Mark(activity));
    }

    [Fact]
    public void A_stop_without_refusals_says_stopped_and_nothing_more()
    {
        var activity = Of(new TurnStanding(true, null, T0.AddMinutes(5), true, 0));

        Assert.Equal("stopped 10m", Activities.Says(activity, T0.AddMinutes(15)));
    }

    [Fact]
    public void A_turn_that_ended_before_the_pickup_is_not_a_stop()
    {
        // It has not had a turn since it took the work, so it has not stopped on it.
        var activity = Of(new TurnStanding(true, null, T0.AddSeconds(30), true, 0));

        Assert.Equal(Doing.Running, activity.Doing);
    }

    [Fact]
    public void Waiting_on_somebody_is_not_stopped()
    {
        // The asker ended its turn to wait for the worker's answer, which is exactly right.
        var activity = Activities.Of(
            Asked(),
            AskerWaitsOnWorker,
            [Agent(Worker, null), Agent(Asker, new TurnStanding(true, null, T0.AddMinutes(2), true, 0))])[Asker];

        Assert.Equal(Doing.Waiting, activity.Doing);
        Assert.Equal("waiting on billing", Activities.Says(activity, T0.AddMinutes(10)));
    }

    [Fact]
    public void An_agent_whose_turns_nobody_reports_is_never_called_working_or_stopped()
    {
        // A node older than section 11.6, or an agent attached from somebody's editor. Picked up and not answered
        // is a guess, and it is exactly the guess that reads hours of nothing as work.
        var activity = Of(TurnStanding.Unreported);

        Assert.Equal(Doing.Running, activity.Doing);
        Assert.Empty(Activities.Says(activity, T0.AddHours(3)));
    }

    [Fact]
    public void A_registry_that_could_not_be_read_claims_nothing()
    {
        Assert.Empty(Activities.Of(Asked(), AskerWaitsOnWorker, registry: null));
        Assert.Equal(" ", Activities.Mark(null));
    }

    [Fact]
    public void Work_in_another_conversation_does_not_make_an_agent_stopped_here()
    {
        var activity = Activities.Of(
            Asked(),
            [new WaitingEdge(Asker, Worker, 99)],
            [Agent(Worker, new TurnStanding(true, null, T0.AddMinutes(5), true, 2))])[Worker];

        Assert.Equal(Doing.Running, activity.Doing);
    }

    [Theory]
    [InlineData(30, "<1m")]
    [InlineData(180, "3m")]
    [InlineData(7500, "2h05m")]
    public void How_long_is_said_in_few_characters(int seconds, string expected) =>
        Assert.Equal(expected, Activities.For(T0, T0.AddSeconds(seconds)));
}
