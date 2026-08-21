namespace Parley.Store;

/// <summary>
/// What one application of the retention policy did.
/// </summary>
/// <param name="Removed">Messages deleted.</param>
/// <param name="HeldByFloor">
/// Messages the idle rule or the age cap released, and the floor of SPEC.md §9.4 kept anyway.
/// </param>
/// <remarks>
/// <para>
/// Two numbers rather than one because they answer different questions and a single total
/// conflates them. "Nothing was old enough" and "plenty was old enough and the floor is holding
/// it" are indistinguishable from a count of what went, and they call for opposite responses:
/// the first means the policy is looser than the operator thought, the second means traffic is
/// still in flight.
/// </para>
/// <para>
/// <see cref="HeldByFloor"/> is expected to be small and non-zero on a busy deployment — every
/// message inside the sixty-second deduplication window counts while it is there. A number that
/// stays large across sweeps is the signal worth acting on, because it means exchanges are
/// sitting open or a peer has stopped collecting its inbox.
/// </para>
/// </remarks>
public readonly record struct PruneResult(int Removed, int HeldByFloor);
