namespace Parley.Core;

/// <summary>
/// What clarification cost across a deployment: SPEC.md §10.2's <c>clarifications()</c>.
/// </summary>
/// <param name="Distribution">How many exchanges reached each clarification depth, including zero.</param>
/// <param name="ExchangesAtCap">How many exchanges reached a cap that was set.</param>
/// <param name="LowestCapInForce">The lowest cap any exchange carries.</param>
/// <param name="HighestCapInForce">The highest cap any exchange carries.</param>
/// <param name="Results">How many Results have been recorded.</param>
/// <param name="ResultsWithMixedOutcomes">How many of them gave their items different outcomes.</param>
/// <remarks>
/// <para>
/// The evidence §17 question 4 asks for. With no cap by default the distribution is uncensored, so it
/// is also what a deployment would set a cap from.
/// </para>
/// <para>
/// <b>The cap on an exchange is the requester's</b> (§5.2), so a deployment setting one on some agents
/// and not others produces a distribution shaped by the number being measured — which is why the range
/// of caps in force is reported beside it.
/// </para>
/// </remarks>
public sealed record ClarifyReport(
    IReadOnlyList<ClarifyDepthBucket> Distribution,
    long ExchangesAtCap,
    long LowestCapInForce,
    long HighestCapInForce,
    long Results,
    long ResultsWithMixedOutcomes)
{
    /// <summary>Gets how many exchanges the distribution covers.</summary>
    /// <value>The sum across every depth.</value>
    public long Exchanges => Distribution?.Sum(b => b.Exchanges) ?? 0;

    /// <summary>Gets the deepest clarification any exchange reached.</summary>
    /// <value>Zero when there are no exchanges.</value>
    public int DeepestReached => Distribution is { Count: > 0 } d ? d.Max(b => b.Depth) : 0;
}
