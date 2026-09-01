namespace Parley.Core;

/// <summary>
/// What one application of the retention policy did.
/// </summary>
/// <param name="Removed">Messages deleted.</param>
/// <param name="HeldByFloor">
/// Messages the idle rule or the age cap released, and the floor of SPEC.md §9.4 kept anyway.
/// </param>
/// <param name="Contended">
/// Messages another sweep removed between this one deciding and doing.
/// </param>
/// <param name="Capped">
/// Whether this pass stopped at its own limit with spent messages still to remove.
/// </param>
/// <remarks>
/// <para>
/// Three numbers rather than one because they answer different questions and a single total
/// conflates them. "Nothing was old enough" and "plenty was old enough and the floor is holding
/// it" are indistinguishable from a count of what went, and they call for opposite responses:
/// the first means the policy is looser than the operator thought, the second means traffic is
/// still in flight.
/// </para>
/// <para>
/// <see cref="Contended"/> is the third, and it exists because leaving it out made the other two
/// lie. Every server prunes on its own schedule and an operator may run <c>prune</c> beside
/// them, so losing the race is ordinary rather than exceptional — six sweeps at once over thirty
/// thousand spent messages had four of them removing nothing. Folded into
/// <see cref="HeldByFloor"/> that read as "thirty thousand messages are undelivered or on open
/// exchanges", which is not merely imprecise: it points at a broken deployment when nothing is
/// wrong. It is not an error count, for the same reason <see cref="ReapResult.Contended"/> is
/// not: the work was done, by somebody else.
/// </para>
/// <para>
/// <see cref="Capped"/> is a fact rather than a count, and it is here rather than left to a
/// caller comparing <see cref="Removed"/> against a published limit, because a caller can get
/// that comparison wrong and because the limit is a storage decision that has no business in the
/// vocabulary of whatever renders this. A pass is capped only on a first prune against a log
/// that grew without a policy; saying nothing would leave an operator believing the log was now
/// inside its retention when it was not.
/// </para>
/// <para>
/// <see cref="HeldByFloor"/> is expected to be small and non-zero on a busy deployment — every
/// message inside the sixty-second deduplication window counts while it is there. A number that
/// stays large across sweeps is the signal worth acting on, because it means exchanges are
/// sitting open or a peer has stopped collecting its inbox.
/// </para>
/// </remarks>
public sealed record PruneResult(
    int Removed, int HeldByFloor, int Contended, bool Capped)
{
    /// <summary>
    /// The result of a pass that did nothing, including a tick that did not reach its retention
    /// interval.
    /// </summary>
    /// <remarks>
    /// A tick that skipped the sweep is distinct from one that swept and found nothing only in
    /// that no scan happened, and the two are deliberately not distinguished here. A caller
    /// acting differently on them would be acting on how often the sweep runs rather than on the
    /// state of the log, and the interval is not a fact about the deployment worth propagating
    /// that far.
    /// </remarks>
    public static PruneResult Nothing { get; } = new(0, 0, 0, Capped: false);
}
