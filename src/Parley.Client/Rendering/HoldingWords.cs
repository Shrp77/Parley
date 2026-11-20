namespace Parley.Client.Rendering;

/// <summary>What holding and resuming an agent cost, in the words both surfaces use (SPEC.md §11.1).</summary>
/// <remarks>
/// One place, as <see cref="StoppingWords"/> is, so the web console, the terminal and the graph cannot describe the same act in
/// different words.
/// </remarks>
public static class HoldingWords
{
    /// <summary>Says what holding an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <returns>The sentences.</returns>
    public static string Holding(string agent) =>
        $"'{agent}' will be stopped once its current turn ends, and then nothing will start it: not a restart of the broker or of "
        + "this computer, not new work for it, not a Start. What it owes stays open and waits. Anything it started in the "
        + "background ends with its process. Resume undoes this.";

    /// <summary>Says what resuming an agent does before a person agrees to it.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <returns>The sentences.</returns>
    public static string Resuming(string agent) =>
        $"'{agent}' can be started again. If it owes open work it is asked to start now, as after a restart; one that owes "
        + "nothing stays as it is until work arrives.";

    /// <summary>Says what holding every agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string HoldingAll(string project) =>
        $"Every agent in '{project}' that is not held will be held, each as holding one is: stopped once its turn ends, and kept "
        + "from starting until resumed. Each is held in every project that serves it, so this may stop work for other projects "
        + "too. What they owe stays open.";

    /// <summary>Says what resuming every held agent in a project does.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The sentences.</returns>
    public static string ResumingAll(string project) =>
        $"Every held agent in '{project}' can be started again, and those that owe open work are asked to start now.";
}
