namespace Parley.Client.Rendering;

/// <summary>What freezing and unfreezing an agent cost, in the words both surfaces use (SPEC.md §11.1).</summary>
/// <remarks>
/// One place, as <see cref="StoppingWords"/> is, so the web console, the terminal and the graph cannot describe the same act in
/// different words.
/// </remarks>
public static class FreezingWords
{
    /// <summary>Says what freezing an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="keepFrozen">Whether the freeze outlasts a logon.</param>
    /// <returns>The sentences.</returns>
    public static string Freezing(string agent, bool keepFrozen = false) =>
        $"'{agent}' will be stopped once its current turn ends, and then nothing will start it: not new work for it, not a Start. "
        + "What it owes stays open and waits. Anything it started in the background ends with its process. Unfreeze undoes this. "
        + (keepFrozen
            ? "It stays frozen after a logon or a restart of the computer, until you unfreeze it."
            : "A logon or a restart of the computer, which starts the broker again, unfreezes it, and it is started if it owes work.");

    /// <summary>Says what unfreezing an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <returns>The sentences.</returns>
    public static string Unfreezing(string agent) =>
        $"'{agent}' can be started again. If it owes open work it is asked to start now, as after a restart; one that owes "
        + "nothing stays as it is until work arrives.";

    /// <summary>Says what freezing every agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string FreezingAll(string project) =>
        $"Every agent in '{project}' that is not frozen will be frozen, each as freezing one is: stopped once its turn ends, and kept "
        + "from starting until unfrozen. Each is frozen in every project that serves it, so this may stop work for other projects "
        + "too. What they owe stays open.";

    /// <summary>Says what unfreezing every frozen agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string UnfreezingAll(string project) =>
        $"Every frozen agent in '{project}' can be started again, and those that owe open work are asked to start now.";
}
