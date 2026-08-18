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
    Unknown = 2
}
