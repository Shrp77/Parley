using System.Globalization;
using Parley.Core;

namespace Parley.Client.Views;

/// <summary>What a participant is doing, as far as the broker can say.</summary>
public enum Doing
{
    /// <summary>The registry could not be read, so nothing is claimed — not even whether it is running.</summary>
    Unknown = 0,

    /// <summary>Nothing is running it.</summary>
    NotRunning = 1,

    /// <summary>Running, and none of the states below.</summary>
    Running = 2,

    /// <summary>A turn is running for it right now (SPEC.md §11.6).</summary>
    Working = 3,

    /// <summary>It asked another participant something in this conversation and is waiting for the answer.</summary>
    Waiting = 4,

    /// <summary>Its last turn ended after it picked up work in this conversation that it still owes.</summary>
    Stopped = 5,
}

/// <summary>One participant's activity.</summary>
/// <param name="Doing">What it is doing.</param>
/// <param name="Since">When that began: the turn's start when working, its end when stopped; null otherwise.</param>
/// <param name="PermissionDenials">Tool calls its last turn had refused for want of permission, when stopped.</param>
/// <param name="WaitingOn">Who it is waiting on, when waiting.</param>
public sealed record Activity(
    Doing Doing,
    DateTimeOffset? Since = null,
    int PermissionDenials = 0,
    AgentName? WaitingOn = null);

/// <summary>
/// Derives what each participant in a conversation is doing, and how both surfaces say it (specs/024).
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-03</b>: an agent was shown as working for nearly three hours after a command it was not
/// allowed to run ended its turn silently. 0.37.5 had derived "working" from "picked up and not answered", which is what an
/// agent mid-task and an agent that has stopped both look like. The node knew the difference and, since §11.6, says so.
/// </para>
/// <para>
/// <b>Working means a turn is running, and nothing else.</b> An agent whose turns nobody reports is never shown as working
/// or stopped on the strength of a guess (FR-010): it is shown by presence alone. <b>Waiting is not stopped</b> (FR-009): an
/// agent that asked somebody and ended its turn to wait is doing exactly what it should.
/// </para>
/// <para>
/// <b>Here rather than in either surface</b>, because the terminal's room and the web UI's conversation page describe the
/// same agents, and two derivations would come to describe one agent two ways (FR-013).
/// </para>
/// </remarks>
public static class Activities
{
    /// <summary>The mark for an agent with a turn running.</summary>
    public const string WorkingMark = "◆";

    /// <summary>The mark for an agent running and doing none of the other things.</summary>
    public const string RunningMark = "●";

    /// <summary>The mark for an agent that stopped with work still owed.</summary>
    public const string StoppedMark = "■";

    /// <summary>The mark for an agent nothing is running.</summary>
    public const string NotRunningMark = "○";

    /// <summary>What the marks mean, said once beside them.</summary>
    public static IReadOnlyList<string> Legend { get; } =
    [
        $"{RunningMark} running",
        $"{WorkingMark} working on a turn",
        $"{StoppedMark} stopped, still owes work",
        $"{NotRunningMark} not running",
    ];

    /// <summary>What each participant is doing.</summary>
    /// <param name="export">The conversation.</param>
    /// <param name="waiting">The wait-for graph, deployment-wide.</param>
    /// <param name="registry">The registry, or null when it could not be read.</param>
    /// <returns>An entry for every agent the registry names, or none at all when it could not be read.</returns>
    public static IReadOnlyDictionary<AgentName, Activity> Of(
        ConversationExport export,
        IReadOnlyList<WaitingEdge> waiting,
        IReadOnlyList<AgentRecord>? registry)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(waiting);

        var result = new Dictionary<AgentName, Activity>();

        if (registry is null)
        {
            return result;
        }

        var open = export.Exchanges
            .Where(exchange => exchange.State == ExchangeState.Open)
            .Select(exchange => exchange.ExchangeId)
            .ToHashSet();

        var here = waiting.Where(edge => open.Contains(edge.ExchangeId)).ToList();

        foreach (var agent in registry)
        {
            result[agent.Name] = Derive(agent, export, here);
        }

        return result;
    }

    /// <summary>The word after a participant's name, or nothing.</summary>
    /// <param name="activity">What it is doing.</param>
    /// <param name="now">Now, for how long.</param>
    /// <returns>For example <c>working 4m</c>, <c>waiting on orders</c> or <c>stopped 2h48m — 1 permission refused</c>.</returns>
    public static string Says(Activity activity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity switch
        {
            { Doing: Doing.Working, Since: { } since } => "working " + For(since, now),
            { Doing: Doing.Waiting, WaitingOn: { } on } => "waiting on " + on.Role,
            { Doing: Doing.Stopped, Since: { } since, PermissionDenials: > 0 } =>
                $"stopped {For(since, now)} — {Refused(activity.PermissionDenials)}",
            { Doing: Doing.Stopped, Since: { } since } => "stopped " + For(since, now),
            _ => string.Empty,
        };
    }

    /// <summary>The mark in front of a participant.</summary>
    /// <param name="activity">What it is doing, or null when nothing is known.</param>
    /// <returns>One character; a space when nothing is known, so nothing is claimed.</returns>
    public static string Mark(Activity? activity) => activity?.Doing switch
    {
        Doing.Working => WorkingMark,
        Doing.Stopped => StoppedMark,
        Doing.Running or Doing.Waiting => RunningMark,
        Doing.NotRunning => NotRunningMark,
        _ => " ",
    };

    /// <summary>How long, in the fewest characters that still read as a duration.</summary>
    /// <param name="since">When it started.</param>
    /// <param name="now">Now.</param>
    /// <returns>For example <c>&lt;1m</c>, <c>3m</c> or <c>2h05m</c>.</returns>
    public static string For(DateTimeOffset since, DateTimeOffset now)
    {
        var span = now - since;

        if (span < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        return span < TimeSpan.FromHours(1)
            ? ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m"
            : ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
                + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>What both surfaces say about work stopped by a refused permission (SPEC.md §6, specs/024).</summary>
    /// <param name="waitingOn">The agent that stopped.</param>
    /// <param name="waiting">Who is waiting on it.</param>
    /// <param name="since">When its turn ended, already written for the surface.</param>
    /// <returns>The sentence, ending with what a person can do about it.</returns>
    /// <remarks>
    /// One sentence in one place, so the terminal's Health and the web UI's health page cannot come to describe one flag two
    /// ways. It names the remedy because the flag exists for exactly one reason: only a person can get the agent past it.
    /// </remarks>
    public static string RefusedFlag(AgentName waitingOn, AgentName waiting, string since) =>
        $"{waitingOn.Value} stopped on {waiting.Value}'s request at {since}: a tool call it needed was refused for want of " +
        "permission. Allow it in that repository's settings, then start the agent again.";

    private static string Refused(int count) =>
        count == 1 ? "1 permission refused" : count.ToString(CultureInfo.InvariantCulture) + " permissions refused";

    private static Activity Derive(AgentRecord agent, ConversationExport export, List<WaitingEdge> here)
    {
        var presence = agent.Presence == AgentPresence.Running ? Doing.Running : Doing.NotRunning;
        var turns = agent.Turns ?? TurnStanding.Unreported;

        // FR-010: nobody reports this agent's turns, so working and stopped would both be guesses.
        if (!turns.Reported)
        {
            return new Activity(presence);
        }

        if (turns.RunningSince is { } running)
        {
            return new Activity(Doing.Working, running);
        }

        // FR-009: asked somebody and waiting is not stopped, whatever else it owes.
        if (here.FirstOrDefault(edge => edge.Waiter == agent.Name) is { } asked)
        {
            return new Activity(Doing.Waiting, WaitingOn: asked.WaitedOn);
        }

        if (turns.LastEnded is { } ended && PickedUpBefore(agent.Name, ended, export, here))
        {
            return new Activity(Doing.Stopped, ended, turns.LastPermissionDenials);
        }

        return new Activity(presence);
    }

    /// <summary>Whether the agent picked up work here it still owes, before its last turn ended.</summary>
    private static bool PickedUpBefore(
        AgentName agent, DateTimeOffset ended, ConversationExport export, List<WaitingEdge> here) =>
        here.Where(edge => edge.WaitedOn == agent).Any(edge =>
            export.Messages
                .Where(message => message.ExchangeId == edge.ExchangeId && message.To == agent)
                .OrderBy(message => message.Id)
                .LastOrDefault()?.DeliveredUtc is { } picked
            && picked <= ended);
}
