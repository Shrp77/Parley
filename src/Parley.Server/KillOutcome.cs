namespace Parley.Server;

/// <summary>What became of a person asking for a process a node that is gone left running to be ended (SPEC.md §11.1).</summary>
public enum KillOutcome
{
    /// <summary>The node on its machine was told.</summary>
    Sent,

    /// <summary>Nothing is running under the name, or what runs is supervised or was opened in an editor: not this one's to end.</summary>
    NotLeftRunning,

    /// <summary>It is left running, but its adapter did not say which process hosts it, so nothing can be named.</summary>
    NoProcess,

    /// <summary>No node is joined for its machine, so nothing there can end it.</summary>
    NoNode,
}
