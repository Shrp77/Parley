using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A conversation as stored: its identifier and where it stands.
/// </summary>
/// <param name="Id">The agent-chosen identifier grouping the discussion.</param>
/// <param name="State">Whether it is in progress, polling, or concluded.</param>
/// <param name="OpenedUtc">When the first exchange naming it was admitted.</param>
/// <param name="ClosedUtc">When it concluded, or <see langword="null"/> if it has not.</param>
/// <remarks>
/// Deliberately not a domain type with transitions on it, unlike <see cref="Exchange"/>. A
/// conversation's rules are all about <em>other</em> rows — which participants hold open
/// exchanges, who has voted, whether a voter has since been retired — and a domain object that
/// needed the whole table loaded to answer a question about itself would be a worse place to
/// put them than the queries that can ask directly. SPEC.md §5.0 makes membership derived
/// rather than declared, and derived state resists being modelled as an aggregate.
/// </remarks>
public sealed record ConversationRecord(
    string Id,
    ConversationState State,
    DateTimeOffset OpenedUtc,
    DateTimeOffset? ClosedUtc);
