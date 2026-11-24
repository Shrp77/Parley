namespace Parley.Server;

/// <summary>What became of a person asking for an agent to be stopped or restarted (SPEC.md §11.1).</summary>
public enum StopOutcome
{
    /// <summary>Its node was told, and stops it once its current turn is over.</summary>
    Sent,

    /// <summary>Nothing is running under the name, so there is nothing to stop.</summary>
    NotRunning,

    /// <summary>It runs only as a process Parley did not start (§11.7), which belongs to whoever opened it.</summary>
    OpenOutside,

    /// <summary>No node is joined for its machine, or that node cannot start it, so nothing here holds its process.</summary>
    NoNode,
}
