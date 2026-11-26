using Parley.Core;

namespace Parley.Headless;

/// <summary>What a resident agent does that no turn the node gave it accounts for (SPEC.md §11.6).</summary>
/// <param name="TurnBegan">A turn the agent began on its own, because background work it started finished.</param>
/// <param name="TurnEnded">That turn ended; given when it began, and how it ended.</param>
/// <param name="BackgroundChanged">What it has running in the background now: how many tasks, and each it could name.</param>
/// <remarks>
/// <para>
/// <b>Why it exists</b>: an agent that starts sub-agents or commands in the background ends its turn with its reply while
/// they run, and the runner later begins a turn of its own to read what they found. Without this, the agent read as idle
/// while it waited on its own work, and its own turn went unseen.
/// </para>
/// <para>
/// Each call is made in order, one at a time, off the thread that reads the agent's output, and a failure in one is
/// dropped: reporting must never stall or end the agent.
/// </para>
/// </remarks>
public sealed record OwnWork(
    Func<DateTimeOffset, Task> TurnBegan,
    Func<DateTimeOffset, TurnOutcome, Task> TurnEnded,
    Func<BackgroundSnapshot, Task> BackgroundChanged);

/// <summary>What an agent has running in the background, as its runner last listed it.</summary>
/// <param name="Count">How many tasks the runner listed.</param>
/// <param name="Listed">Each it could name, in the runner's order, at most <see cref="BackgroundTaskText.MostTasks"/>,
/// each held to <see cref="BackgroundTaskText"/>'s bounds. Fewer than <paramref name="Count"/> when a task had no id the
/// node would hold, or the list was longer than the bound.</param>
/// <remarks>
/// <para>
/// The count and the list are one fact, read and replaced together: a report of one without the other would say
/// something the runner never did.
/// </para>
/// <para>
/// <b>Equal by what it says</b>: the same count and the same tasks in the same order, so two readings of one line are
/// equal although each holds its own list. A record compares a list by reference, which is why equality is written here.
/// </para>
/// </remarks>
public sealed record BackgroundSnapshot(int Count, IReadOnlyList<BackgroundTask> Listed)
{
    /// <summary>Nothing running.</summary>
    public static BackgroundSnapshot None { get; } = new(0, []);

    /// <inheritdoc/>
    public bool Equals(BackgroundSnapshot? other) =>
        other is not null && Count == other.Count && Listed.SequenceEqual(other.Listed);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);

        foreach (var task in Listed)
        {
            hash.Add(task);
        }

        return hash.ToHashCode();
    }
}
