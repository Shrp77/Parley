namespace Parley.Core;

/// <summary>
/// The evidence SPEC.md §17 question 4 asks for and has never had.
/// </summary>
/// <param name="Distribution">Exchanges by the depth they reached, ascending, zero included.</param>
/// <param name="ExchangesAtCap">
/// Exchanges whose depth equals the cap they were admitted under. This is the whole of what a
/// breach produces: SPEC.md §5.2 refuses the clarification and writes nothing, so there is no
/// broker-authored close to count. An earlier version of this report counted one, which could
/// never be anything but zero.
/// </param>
/// <param name="LowestCapInForce">The smallest cap any counted exchange carries.</param>
/// <param name="HighestCapInForce">The largest cap any counted exchange carries.</param>
/// <remarks>
/// The caps are 64-bit for a reason that is not symmetry with the counts. Rows written before
/// the bound of SPEC.md §9.3 existed carry no constraint at all, so one of them can hold a
/// value no <c>int</c> can. Narrowing it would wrap, and it would wrap in the one field whose
/// entire job is to say what bound produced the distribution beside it.
/// </remarks>
/// <param name="Answers">Answers sent, whatever they resolved to.</param>
/// <param name="AnswersResolvingSubQuestionsDifferently">
/// Answers whose sub-questions did not all resolve the same way.
/// </param>
/// <remarks>
/// <para>
/// Every figure here is read from columns that already existed. Unlike the size cap, whose
/// refusals were discarded and had to be given a table of their own, clarification depth is
/// recorded on a row that outlives the exchange - so this needed no schema change, and whatever
/// history a deployment has already accumulated is readable.
/// </para>
/// <para>
/// <see cref="LowestCapInForce"/> and <see cref="HighestCapInForce"/> are reported because the
/// distribution is meaningless without them. A cap censors its own evidence: depths pile up at
/// whatever bound was in force, whether the true need was one more or forty more. Reading a
/// distribution without knowing where it was truncated is how a censored measurement gets
/// mistaken for a natural one, and the method §17 question 4 proposes is to raise the cap until
/// the pile disappears.
/// </para>
/// <para>
/// <see cref="AnswersResolvingSubQuestionsDifferently"/> is the cheap test of whether
/// sub-questions carry information at all. §17 question 4 argues the depth counter conflates
/// repeated clarification of one point with single requests for several different facts, and that
/// a per-sub-question budget would separate them. That is only worth building if answers ever
/// treat sub-questions as genuinely distinct. If every answer resolves all of them identically,
/// the structure is formatting and the proposal is dead.
/// </para>
/// <para>
/// Counts are 64-bit, depth is not. The schema bounds a depth and nothing bounds a count, and
/// this project has already had an operator report throw <c>OverflowException</c> permanently
/// because a running total was declared as an <c>int</c>.
/// </para>
/// </remarks>
public sealed record ClarifyReport(
    IReadOnlyList<ClarifyDepthBucket> Distribution,
    long ExchangesAtCap,
    long LowestCapInForce,
    long HighestCapInForce,
    long Answers,
    long AnswersResolvingSubQuestionsDifferently)
{
    /// <summary>Gets how many exchanges the distribution covers.</summary>
    public long Exchanges => Distribution?.Sum(b => b.Exchanges) ?? 0;

    /// <summary>Gets the deepest clarification any exchange reached, or zero if none did.</summary>
    public int DeepestReached =>
        Distribution is { Count: > 0 } d ? d.Max(b => b.Depth) : 0;
}
