namespace Parley.Broker;

/// <summary>
/// What putting a roadmap forward came to (SPEC.md §10.1 <c>propose_roadmap</c>).
/// </summary>
/// <param name="Id">The proposal's identifier, or <see langword="null"/> when it was refused.</param>
/// <param name="Problem">A sentence saying why it was refused, or <see langword="null"/>.</param>
/// <remarks>
/// A refusal is a value rather than an exception because an agent can send a proposal again without whatever was
/// wrong with it, and the sentence is what it needs in order to do so.
/// </remarks>
public sealed record ProposalOutcome(long? Id, string? Problem);
