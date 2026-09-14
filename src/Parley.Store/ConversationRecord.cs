using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A conversation as stored: its identifier and where it stands.
/// </summary>
/// <param name="Id">The identifier, named by whoever started it or assigned by the broker.</param>
/// <param name="State">Whether it is in progress, polling, or closed.</param>
/// <param name="OpenedUtc">When the Request that started it was admitted.</param>
/// <param name="ClosedUtc">When it closed, or <see langword="null"/> if it has not.</param>
/// <param name="ClosedBy">Who closed it, or <see langword="null"/> while open or where nothing recorded it.</param>
/// <remarks>
/// Deliberately not a domain type with transitions on it. A conversation's rules are all about other
/// rows — which participants hold open exchanges, who has voted, whether a voter has been retired — and
/// the queries that can ask directly are a better home for them than an object that needed the whole
/// table loaded to answer a question about itself.
/// </remarks>
public sealed record ConversationRecord(
    string Id,
    ConversationState State,
    DateTimeOffset OpenedUtc,
    DateTimeOffset? ClosedUtc,
    ConversationCloser? ClosedBy = null);
