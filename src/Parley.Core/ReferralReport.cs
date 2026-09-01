namespace Parley.Core;

/// <summary>
/// The <c>out_of_scope</c> rate per pair of SPEC.md §15, and who the work was sent to.
/// </summary>
/// <param name="Pairs">Pairs where at least one exchange resolved out of scope.</param>
/// <param name="Targets">Who was named, per pair, most-referred first.</param>
/// <remarks>
/// <para>
/// §15 calls this the signal that the ownership map is wrong somewhere, and the two halves come
/// from different tables for a reason worth knowing when reading it. The rate is computed from
/// <c>exchanges</c>, which retention never touches, so it is accurate over the whole life of the
/// deployment. The targets come from <c>message_referrals</c>, which exists precisely because the
/// names used to live in a message payload that §9.4 deletes.
/// </para>
/// <para>
/// A high rate is not a fault in the answerer. It says two agents disagree about where a boundary
/// runs, or that one of them has a stale idea of what the other owns — which is a question for
/// the people who own those repositories, and exactly what an <c>owns</c> line is for.
/// </para>
/// </remarks>
public sealed record ReferralReport(
    IReadOnlyList<ReferralPair> Pairs,
    IReadOnlyList<ReferralTarget> Targets)
{
    /// <summary>Gets whether anything has been referred at all.</summary>
    public bool IsEmpty => (Pairs is null || Pairs.Count == 0)
                           && (Targets is null || Targets.Count == 0);
}
