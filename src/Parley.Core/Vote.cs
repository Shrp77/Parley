namespace Parley.Core;

/// <summary>
/// A participant's answer to a closure poll, per SPEC.md §5.0.
/// </summary>
/// <remarks>
/// <para>
/// Two values and no abstention, because silence already means something. A participant that
/// does not answer within the window §14 uses to abandon an orphan is taken to agree, so the
/// absence of a vote is the assent — and a third value meaning "no opinion" would be
/// indistinguishable from it while costing an agent a call to say so.
/// </para>
/// <para>
/// The asymmetry between the two is the whole design. <see cref="Done"/> is one voice among
/// several and settles nothing on its own; <see cref="NotDone"/> settles the poll immediately,
/// because §5.0 refuses to let a majority conclude over an objection — that would be authority
/// over another agent by a different name.
/// </para>
/// </remarks>
public enum Vote
{
    /// <summary>
    /// This participant considers the discussion finished.
    /// </summary>
    Done = 0,

    /// <summary>
    /// This participant has a live concern and closure is blocked.
    /// </summary>
    /// <remarks>
    /// Durable, but not something the agent must remember: §5.0 has the broker re-deliver the
    /// closure question when the conversation next falls idle, because depending on an agent to
    /// revisit a subject unprompted is what §7.3 says plainly does not work.
    /// <para>
    /// It lapses with its holder. An agent marked <c>unreachable</c> or <c>retired</c> no longer
    /// blocks, since a judgement is only worth honouring while the agent that made it can still
    /// act on it, and a discussion held open by a repository that no longer exists is the churn
    /// §14 exists to stop.
    /// </para>
    /// </remarks>
    NotDone = 1
}
