namespace Parley.Core;

/// <summary>
/// A participant's answer to a closure poll (SPEC.md §5.0).
/// </summary>
/// <remarks>
/// <para>
/// Two values and no abstention. A participant that has not answered has not agreed — silence does not
/// assent — so the absence of a vote already means "not yet", and a third value would say the same
/// thing at the cost of a call.
/// </para>
/// <para>
/// The asymmetry between the two is the design. <see cref="Done"/> is one voice and settles nothing on
/// its own; <see cref="NotDone"/> blocks closure outright, because a majority that can close over an
/// objection is authority over another participant by a different name.
/// </para>
/// </remarks>
public enum Vote
{
    /// <summary>This participant considers the conversation finished.</summary>
    Done = 0,

    /// <summary>This participant has a live concern, and closure is blocked.</summary>
    /// <remarks>
    /// Durable, and changed by calling again. It lapses with its holder: a retired participant's
    /// decline no longer blocks, since a judgement is only worth honouring while the agent that made it
    /// can act on it.
    /// </remarks>
    NotDone = 1
}
