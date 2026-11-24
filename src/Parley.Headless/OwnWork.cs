namespace Parley.Headless;

/// <summary>What a resident agent does that no turn the node gave it accounts for (SPEC.md §11.6).</summary>
/// <param name="TurnBegan">A turn the agent began on its own, because background work it started finished.</param>
/// <param name="TurnEnded">That turn ended; given when it began, and how it ended.</param>
/// <param name="BackgroundChanged">How many background tasks it has running now.</param>
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
    Func<int, Task> BackgroundChanged);
