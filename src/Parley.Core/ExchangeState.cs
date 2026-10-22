namespace Parley.Core;

/// <summary>
/// Where an exchange stands (SPEC.md §6).
/// </summary>
/// <remarks>
/// Two states and no third. Nothing ends work because of silence (principle 2.8): silence is flagged
/// to the person (§6), and an exchange stays open until its receiver sends a Result or its
/// conversation is closed.
/// </remarks>
public enum ExchangeState
{
    /// <summary>Work is owed; somebody holds the next move.</summary>
    Open = 0,

    /// <summary>A Result was sent, or the conversation was closed first. Terminal.</summary>
    Closed = 1
}
