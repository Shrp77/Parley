namespace Parley.Client.Rendering;

/// <summary>What freezing and unfreezing an agent cost, in the words both surfaces use (SPEC.md §14).</summary>
/// <remarks>
/// One place, as <see cref="StoppingWords"/> is, so the web console, the terminal and the graph cannot describe the same act in
/// different words.
/// </remarks>
public static class FreezingWords
{
    /// <summary>Says what freezing an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="keepFrozen">Whether the freeze outlasts the broker's next start.</param>
    /// <param name="leftRunning">Whether its process was left running by a node that is gone, which a freeze does not end.</param>
    /// <param name="openOutside">Whether it is open in an editor Parley did not start, which a freeze does not end.</param>
    /// <returns>The sentences.</returns>
    /// <remarks>
    /// The first sentence follows what holds the process: a freeze asks a node to stop only a process a node holds, so the person
    /// is not promised a stop that the broker's own answer then contradicts (review, lens 3 run B on 6257cf80). And any start of
    /// the broker lifts an ordinary freeze, not only a logon: an install, a crash and the broker run again do too.
    /// </remarks>
    public static string Freezing(string agent, bool keepFrozen = false, bool leftRunning = false, bool openOutside = false) =>
        (leftRunning
            ? $"'{agent}' is a process a node that is gone left running, so freezing does not end it; end it with Kill. Then nothing will start it: not new work for it, not a Start. "
            : openOutside
                ? $"'{agent}' is open in an editor Parley did not start, so its session stays until whoever opened it closes it. Nothing will start it again: not new work for it, not a Start. "
                : $"'{agent}' will be stopped once its current turn ends, and then nothing will start it: not new work for it, not a Start. ")
        + "What it owes stays open and waits. Anything it started in the background ends with its process. Unfreeze undoes this. "
        + (keepFrozen
            ? "It stays frozen when the broker starts again, until you unfreeze it."
            : "The broker's next start (a logon, a reboot, installing a new build, or a crash) unfreezes it, and it is started if it owes work or has anything waiting.");

    /// <summary>Says what unfreezing an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <returns>The sentences.</returns>
    public static string Unfreezing(string agent) =>
        $"'{agent}' can be started again. If it owes open work, or anything arrived for it while it was frozen, it is asked to "
        + "start now; one with nothing to do stays as it is until work arrives.";

    /// <summary>Says what freezing every agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string FreezingAll(string project) =>
        $"Every agent in '{project}' that is not frozen will be frozen, each as freezing one is: one a node holds is stopped once its "
        + "turn ends; one a node that is gone left running keeps running until you end it with Kill; one open in an editor stays "
        + "open until whoever opened it closes it. None is started again until unfrozen. Each is frozen in every project that "
        + "serves it, so this may stop work for other projects too. What they owe stays open.";

    /// <summary>Says what unfreezing every frozen agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string UnfreezingAll(string project) =>
        $"Every frozen agent in '{project}' can be started again, and those that owe open work, or have anything waiting, are asked to start now.";
}
