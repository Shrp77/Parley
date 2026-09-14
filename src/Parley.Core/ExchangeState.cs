namespace Parley.Core;

/// <summary>
/// Where an exchange stands (SPEC.md §6).
/// </summary>
/// <remarks>
/// Two states and no third. An exchange whose receiver went away used to become orphaned and was
/// then closed as ghosted after a grace period, and both steps ended work because of silence —
/// which principle 2.8 forbids. Silence is flagged to the person instead (§6), and an exchange stays
/// open until its receiver sends a Result or its conversation is closed.
/// </remarks>
public enum ExchangeState
{
    /// <summary>Work is owed; somebody holds the next move.</summary>
    Open = 0,

    /// <summary>A Result was sent, or the conversation was closed first. Terminal.</summary>
    Closed = 1
}
