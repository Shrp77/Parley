namespace Parley.Core;

/// <summary>
/// The outcome recorded against a single sub-question when an exchange is answered.
/// </summary>
public enum Resolution
{
    /// <summary>
    /// Resolved from the answering agent's own authority.
    /// </summary>
    Answered = 0,

    /// <summary>
    /// Outside the answering agent's boundary. A redirect target should accompany this.
    /// </summary>
    /// <remarks>
    /// Not a separate message type — it is an answer — but it must remain distinguishable,
    /// because it is the system's most useful diagnostic. A cluster of these between two
    /// agents means the ownership map is wrong, which is a defect that would otherwise go
    /// unnoticed.
    /// </remarks>
    OutOfScope = 1,

    /// <summary>
    /// Within the answering agent's boundary, but genuinely undetermined.
    /// </summary>
    /// <remarks>
    /// Preferable to a confident guess. The asker will act on whatever comes back, so an
    /// admitted gap is cheaper than a fabrication. Also produced automatically when the
    /// clarification depth cap is reached.
    /// </remarks>
    Unknown = 2,

    /// <summary>
    /// Nobody answered, and nobody is going to.
    /// </summary>
    /// <remarks>
    /// The one resolution no agent may write: the broker records it when an orphan's grace
    /// period expires. Distinct from <see cref="Unknown"/> for the reason
    /// <see cref="OutOfScope"/> is distinct from <see cref="Answered"/> — both close an
    /// exchange without resolving it, but one means a peer considered the question and could
    /// not settle it, and the other means no peer ever engaged with it at all. Collapsing them
    /// would hide an infrastructure failure inside a legitimate answer.
    /// </remarks>
    Ghosted = 3
}
