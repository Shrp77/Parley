using System.Globalization;
using Parley.Core;

namespace Parley.Terminal;

/// <summary>What a participant is doing, as far as the broker can say.</summary>
internal enum Doing
{
    /// <summary>The registry could not be read, so nothing is claimed.</summary>
    Unknown = 0,

    /// <summary>Nothing is running it.</summary>
    NotRunning = 1,

    /// <summary>Running, and holding no request it has picked up.</summary>
    Running = 2,

    /// <summary>Holding a request in this conversation that it has picked up and not yet answered.</summary>
    Working = 3,
}

/// <summary>One participant's activity: what they are doing, and since when if they are working.</summary>
/// <param name="Doing">What they are doing.</param>
/// <param name="Since">When they picked up the request they are working on, or null.</param>
internal sealed record Activity(Doing Doing, DateTimeOffset? Since);

/// <summary>
/// Derives what each participant in a conversation is doing from three reads the room already makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's report of 2026-10-03</b>: one agent sent another a request, and nothing on the screen said the second
/// was doing anything. The room said only what stood between each agent and the <i>person</i>; work between two agents
/// had no row at all.
/// </para>
/// <para>
/// <b>Working means picked up and not answered</b>, which is what the broker can actually say. The wait-for graph names
/// who holds the next move on every open exchange, deployment-wide; the conversation's own exchanges narrow it to this
/// one; and a message's collected time says the holder has taken it. Whether an agent is in the middle of a turn this
/// second is something only its node knows, and no read a surface makes carries it — so it is not claimed.
/// </para>
/// </remarks>
internal static class Activities
{
    /// <summary>What each participant is doing.</summary>
    /// <param name="export">The conversation.</param>
    /// <param name="waiting">The wait-for graph, deployment-wide.</param>
    /// <param name="registry">The registry, or null when it could not be read.</param>
    /// <returns>An entry for every agent the graph or the registry says anything about.</returns>
    internal static IReadOnlyDictionary<AgentName, Activity> Of(
        ConversationExport export,
        IReadOnlyList<WaitingEdge> waiting,
        IReadOnlyList<AgentRecord>? registry)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(waiting);

        var here = export.Exchanges
            .Where(exchange => exchange.State == ExchangeState.Open)
            .Select(exchange => exchange.ExchangeId)
            .ToHashSet();

        var result = new Dictionary<AgentName, Activity>();

        foreach (var agent in registry ?? [])
        {
            result[agent.Name] = new Activity(
                agent.Presence == AgentPresence.Running ? Doing.Running : Doing.NotRunning,
                null);
        }

        foreach (var edge in waiting.Where(edge => here.Contains(edge.ExchangeId)))
        {
            // The latest thing said to the holder in that exchange, and whether they have taken it.
            var latest = export.Messages
                .Where(message => message.ExchangeId == edge.ExchangeId && message.To == edge.WaitedOn)
                .OrderBy(message => message.Id)
                .LastOrDefault();

            if (latest?.DeliveredUtc is not { } picked)
            {
                continue;
            }

            // Working on more than one: the longest, which is the one somebody is most likely waiting to hear about.
            if (!result.TryGetValue(edge.WaitedOn, out var was) || was.Doing != Doing.Working || was.Since > picked)
            {
                result[edge.WaitedOn] = new Activity(Doing.Working, picked);
            }
        }

        return result;
    }

    /// <summary>How long, in the fewest characters that still read as a duration.</summary>
    /// <param name="since">When it started.</param>
    /// <param name="now">Now.</param>
    /// <returns>For example <c>&lt;1m</c>, <c>3m</c> or <c>2h05m</c>.</returns>
    internal static string For(DateTimeOffset since, DateTimeOffset now)
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
}
