namespace Parley.Core;

/// <summary>
/// How a person arrived at the ownership line they accepted (SPEC.md §17 question 10).
/// </summary>
/// <remarks>
/// <para>
/// Recorded because the three are different evidence, not because a surface needs to draw them
/// differently. Question 10's amendment permits a model-drafted line on the condition that approval
/// is a recorded positive act — so what is worth keeping is *which* act it was, and the answer
/// narrows what the field can be claimed to mean: a <see cref="Proposed"/> line is an agent's words
/// that a person read and agreed to, and a <see cref="Composed"/> one is a person's own.
/// </para>
/// <para>
/// <b>It is also what keeps a person's characters from being attributed to a model.</b> The length
/// recorded beside an accepted line is the length of the *agent's* message, and there is no such
/// length for a line nobody proposed. 0.29.0's second review finding was exactly this mistake from
/// the other side: a retry wrote into <c>size_refusals</c> a length no agent had composed, and
/// <c>size_refusals</c> is the table §17 question 16 reads its evidence out of.
/// </para>
/// </remarks>
public enum OwnershipSource
{
    /// <summary>The agent's own words, accepted untouched.</summary>
    Proposed = 0,

    /// <summary>The agent's proposal, changed by the person before they accepted it.</summary>
    Edited = 1,

    /// <summary>Written by the person, with no ask at all. There is no proposal and no discussion.</summary>
    Composed = 2
}
