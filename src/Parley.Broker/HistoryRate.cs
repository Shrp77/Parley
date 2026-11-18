using Parley.Core;

namespace Parley.Broker;

/// <summary>How much of the past one agent has read in the last minute (SPEC.md §13).</summary>
/// <remarks>
/// <para>
/// <b>Held in memory and reset by a restart, on purpose.</b> The aim is that the tool cannot copy a store in one sitting,
/// not to account for what was read; the person has the record of that (§5.8). A table and its clean-up would add nothing
/// the rolling minute does not.
/// </para>
/// <para>
/// Thread-safe, because an agent's calls and the broker's other work reach it from different threads.
/// </para>
/// </remarks>
internal sealed class HistoryRate
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Dictionary<AgentName, Queue<(DateTimeOffset At, int Characters)>> _taken = [];

    /// <summary>Says whether a caller may read now, and if not for how long to wait.</summary>
    /// <param name="agent">The reader.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <returns>Zero when it may; otherwise the seconds until it may.</returns>
    public int WaitFor(AgentName agent, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var queue = Trim(agent, nowUtc);

            if (queue.Count < HistoryLimits.CallsPerMinute && queue.Sum(t => t.Characters) < HistoryLimits.CharactersPerMinute)
            {
                return 0;
            }

            var oldest = queue.Peek().At;

            return Math.Max(1, (int)Math.Ceiling((oldest + Window - nowUtc).TotalSeconds));
        }
    }

    /// <summary>Records what a call returned.</summary>
    /// <param name="agent">The reader.</param>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="characters">How many characters it returned.</param>
    public void Took(AgentName agent, DateTimeOffset nowUtc, int characters)
    {
        lock (_lock)
        {
            Trim(agent, nowUtc).Enqueue((nowUtc, characters));
        }
    }

    private Queue<(DateTimeOffset At, int Characters)> Trim(AgentName agent, DateTimeOffset nowUtc)
    {
        if (!_taken.TryGetValue(agent, out var queue))
        {
            queue = new Queue<(DateTimeOffset, int)>();
            _taken[agent] = queue;
        }

        while (queue.Count > 0 && nowUtc - queue.Peek().At >= Window)
        {
            queue.Dequeue();
        }

        return queue;
    }
}
