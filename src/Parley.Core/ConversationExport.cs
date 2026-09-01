namespace Parley.Core;

/// <summary>
/// The state of one exchange, for a reader who has its messages in front of them.
/// </summary>
/// <param name="ExchangeId">Which exchange.</param>
/// <param name="State">Where it stands in the machine of SPEC.md §6.</param>
/// <param name="OpenedUtc">When it opened.</param>
/// <param name="ClosedUtc">When it closed, or <see langword="null"/> while it is open.</param>
/// <remarks>
/// State is read rather than inferred, and the difference is the whole reason this exists.
/// Deducing it from the messages present cannot tell an orphaned exchange from an open one, and
/// cannot see a ghost at all — and an operator opens a conversation precisely because something
/// went wrong, which makes rendering an abandoned question identically to an answered one the
/// most misleading thing a console could do.
/// </remarks>
public sealed record ExchangeSnapshot(
    long ExchangeId,
    ExchangeState State,
    DateTimeOffset OpenedUtc,
    DateTimeOffset? ClosedUtc);

/// <summary>
/// Everything one conversation holds: its messages, and where each exchange stands.
/// </summary>
/// <param name="Messages">Every message carrying the conversation's identifier, in the order the broker recorded them.</param>
/// <param name="Exchanges">The state of every exchange those messages belong to.</param>
/// <remarks>
/// <para>
/// The two travel together rather than in two calls, so a client never holds one without the
/// other and cannot show a transcript whose states it never asked for. They are two reads on one
/// connection rather than a snapshot: an exchange that closes between them shows as it was, and
/// corrects on the next refresh.
/// </para>
/// <para>
/// Ordering follows the order the broker recorded, not a timestamp. Every message in a project
/// passes through one process, so insertion order is authoritative and immune to the clock
/// differences between the machines the agents run on.
/// </para>
/// </remarks>
public sealed record ConversationExport(
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ExchangeSnapshot> Exchanges);
