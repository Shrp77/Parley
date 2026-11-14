namespace Parley.Client.Rendering;

/// <summary>Stopping and restarting an agent, in the words both surfaces use (SPEC.md §11.1).</summary>
/// <remarks>
/// One place, so the terminal and the web console describe the same act the same way. What a person is deciding is the
/// loss of what the process had learned, so that is said first; what is not lost (the work it owes) is said second,
/// because "stop" reads as "stop the work".
/// </remarks>
public static class StoppingWords
{
    /// <summary>What happens to work and learning, said with both acts.</summary>
    public const string WhatItKeeps =
        "What it owes stays owed, and its conversations are untouched. What the running session had learned is lost.";

    /// <summary>Describes a stop before the person confirms it.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The description.</returns>
    public static string Stopping(string agent) =>
        $"'{agent}' is stopped once its current turn is over. " + WhatItKeeps + " The next work for it starts it "
        + "again, as any agent is started; to keep it from being started, retire it.";

    /// <summary>Describes a restart before the person confirms it.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The description.</returns>
    public static string Restarting(string agent) =>
        $"'{agent}' is stopped once its current turn is over, and started again, reading its launch configuration "
        + "afresh. " + WhatItKeeps;

    /// <summary>What a stop or restart came to.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="restart">Whether it was a restart.</param>
    /// <param name="asked">Whether its node was asked.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// Says the node was asked, never that the agent has stopped: the node waits for the turn, and a screen must not
    /// predict what happens after it.
    /// </remarks>
    public static string Outcome(string agent, bool restart, bool asked) =>
        asked
            ? $"Asked {agent}'s node to {(restart ? "restart" : "stop")} it once its current turn is over."
            : $"Nothing was asked: {agent} is not running" + (restart ? ", so start it instead." : ".");
}
