namespace Parley.Core;

/// <summary>
/// What kind of blocker a <see cref="Outcome.Blocked"/> item names (SPEC.md §5.3).
/// </summary>
/// <remarks>
/// Three kinds because each points somewhere different (§15): missing information that keeps
/// coming back to one agent means the ownership map is wrong, a recurring conflict between two
/// agents is a knot worth a decision record, and a missing permission is about the deployment rather
/// than any agent. Pooling them would lose exactly that.
/// </remarks>
public enum BlockerKind
{
    /// <summary>
    /// Something the receiver needs and could not get: nobody in the project could provide it and no
    /// person was seated to ask.
    /// </summary>
    MissingInformation = 0,

    /// <summary>A conflict the receiver tried to resolve, including by asking others to do work.</summary>
    Conflict = 1,

    /// <summary>A permission the receiver does not have, named together with who could grant it.</summary>
    Permission = 2
}
