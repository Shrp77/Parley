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
        + "again, as any agent is started; to keep it from being started, freeze it, or retire it for good.";

    /// <summary>Describes a restart before the person confirms it.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The description.</returns>
    public static string Restarting(string agent) =>
        $"'{agent}' is stopped once its current turn is over, and started again, reading its launch configuration "
        + "afresh. " + WhatItKeeps;

    /// <summary>What ending a process a node that is gone left running does, said before it is done (SPEC.md §11.1).</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="frozen">Whether a person has frozen it, so nothing starts it again after this.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// Said plainly that this does not wait: nothing holds the process, so nothing can say when its turn ends, and what it
    /// is doing now is lost. It is for a process left by a node that is gone, never one somebody opened in an editor. A
    /// frozen one is not started again by new work, so it is not told it will be (review, lens 3 run 2 on 6fde4ef3).
    /// </remarks>
    public static string Killing(string agent, bool frozen = false) =>
        $"A process of '{agent}' was started by a node that is gone, so no node holds it and it cannot be stopped at the end "
        + "of its turn. This ends the process now, and whatever it is in the middle of is lost, along with what the session "
        + "had learned. A process of it that the node here started is left alone. What it owes stays owed, and its "
        + "conversations are untouched. "
        + (frozen
            ? "It stays frozen, so nothing starts it again until you unfreeze it."
            : "The next work for it starts it again, under the node that is here; to keep it from being started, freeze it, "
              + "or retire it for good.");

    /// <summary>What a person is told when there is no process a node that is gone left running to end (SPEC.md §11.1).</summary>
    /// <param name="agent">The agent's name.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>Said instead of <see cref="Killing"/>, with nothing to confirm, so a person is never told an untrue reason.</remarks>
    public static string NothingToKill(string agent) =>
        $"'{agent}' is not running as a process a node that is gone left behind, so there is nothing to end here. A process "
        + "a node supervises is stopped, not killed, and one somebody opened in an editor is theirs to close.";

    /// <summary>What a person is told when the broker refused to end a process.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="why">The broker's reason.</param>
    /// <returns>The sentence, naming the act rather than calling it a read.</returns>
    public static string KillRefused(string agent, string why) =>
        $"Nothing was ended: the broker refused to end {agent}'s process. {why}";

    /// <summary>What a person is told after asking for such a process to be ended.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="asked">Whether the broker sent the node the command.</param>
    /// <returns>The sentence.</returns>
    public static string KillOutcome(string agent, bool asked) =>
        asked
            ? $"Asked the node to end {agent}'s process now. Its page shows whether it has gone."
            : $"Nothing was asked: {agent} is not running.";

    /// <summary>What a person is told about an agent a node that is gone left running, beside the way to end it.</summary>
    public const string LeftRunning =
        "A node that is gone started this process, so Parley cannot give it a turn or stop it, and it will not be started "
        + "again while it runs. Nobody opened it in an editor.";

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
