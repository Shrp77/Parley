namespace Parley.Core;

/// <summary>
/// How a person arrived at the ownership line they accepted (SPEC.md §17 question 10).
/// </summary>
/// <remarks>
/// <para>
/// Recorded because the three are different evidence, not because a surface needs to draw them
/// differently. What is worth keeping is *which* act it was, and the answer narrows what the
/// field can be claimed to mean: a <see cref="Proposed"/> line is an agent's words that were
/// accepted, and a <see cref="Composed"/> one is a person's own.
/// </para>
/// <para>
/// <b>It is also what keeps a person's characters from being attributed to a model.</b> The length
/// recorded beside an accepted line is the length of the *agent's* message, and there is no such
/// length for a line nobody proposed. Recording one would write into <c>size_refusals</c> a length no
/// agent had composed, and <c>size_refusals</c> is the table §17 question 16 reads its evidence out of.
/// </para>
/// </remarks>
public enum OwnershipSource
{
    /// <summary>The agent's own words, accepted untouched.</summary>
    Proposed = 0,

    /// <summary>The agent's proposal, changed by the person before they accepted it.</summary>
    Edited = 1,

    /// <summary>Written by the person, with no ask at all. There is no proposal and no discussion.</summary>
    Composed = 2,

    /// <summary>
    /// The agent's own words, made its line by the project's Auto Accept switch with no person involved
    /// (SPEC.md §10.2). Only runs of whitespace were collapsed.
    /// </summary>
    /// <remarks>
    /// <b>Recorded only by the broker itself, and refused from any operator call.</b> The three routes above
    /// are a person's acts; this one is the absence of one, and a person's decision recorded as the
    /// machine's — or the machine's as a person's — would make provenance the one thing it exists not to be.
    /// </remarks>
    Automatic = 3
}
