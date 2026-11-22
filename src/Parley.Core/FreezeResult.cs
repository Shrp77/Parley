namespace Parley.Core;

/// <summary>What became of an agent's process when a person froze it (SPEC.md §14).</summary>
public enum FreezeProcess
{
    /// <summary>Its node was asked to stop it once its current turn is over.</summary>
    Stopping,

    /// <summary>Nothing was running under the name, so there was nothing to stop.</summary>
    NotRunning,

    /// <summary>It runs only as a process Parley did not start (§11.7), so no node can end it. It is frozen all the same: nothing will start it again.</summary>
    OpenOutside,

    /// <summary>No node is joined for its machine, so nothing could be asked. The freeze is recorded and stands when the node returns.</summary>
    NoNode
}

/// <summary>What freezing an agent did, in one project.</summary>
/// <param name="Changed"><see langword="false"/> when the agent was frozen already.</param>
/// <param name="Process">What became of its process.</param>
/// <remarks>
/// A freeze is recorded whatever the process did: what a person decided is that nothing starts the agent, and that stays true
/// when its process cannot be ended from here.
/// </remarks>
public sealed record FreezeResult(bool Changed, FreezeProcess Process);
