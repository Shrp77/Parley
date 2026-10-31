namespace Parley.Core;

/// <summary>
/// The state of one exchange, for a reader who has its messages in front of them.
/// </summary>
/// <param name="ExchangeId">Which exchange.</param>
/// <param name="State">Where it stands in the machine of SPEC.md §6.</param>
/// <param name="OpenedUtc">When it opened.</param>
/// <param name="ClosedUtc">When it closed, or <see langword="null"/> while it is open.</param>
/// <param name="Outcome">How it closed, or <see langword="null"/> while it is open.</param>
/// <param name="SettledBy">
/// For an exchange closed as <see cref="ExchangeOutcome.Settled"/>, the exchange whose Request settled it (SPEC.md §5.6);
/// otherwise <see langword="null"/>.
/// </param>
/// <param name="RequestGroup">
/// For one member of a Request to a role, the group every member shares (SPEC.md §5.5); otherwise <see langword="null"/>.
/// </param>
/// <remarks>
/// State and outcome are read rather than inferred from the messages present, and the difference is
/// the whole reason this exists. A person opens a conversation because something went wrong, and a
/// transcript that could not tell work ended by the person from work its receiver finished would be
/// the most misleading thing a surface could show.
/// </remarks>
public sealed record ExchangeSnapshot(
    long ExchangeId,
    ExchangeState State,
    DateTimeOffset OpenedUtc,
    DateTimeOffset? ClosedUtc,
    ExchangeOutcome? Outcome = null,
    long? SettledBy = null,
    string? RequestGroup = null);

/// <summary>
/// Everything one conversation holds: its messages, and where each exchange stands.
/// </summary>
/// <param name="Messages">Every message carrying the conversation's identifier, in the order the broker recorded them.</param>
/// <param name="Exchanges">The state of every exchange those messages belong to.</param>
/// <param name="Destroyed">
/// What is left of a conversation an operator destroyed, or null when this one was not.
/// Both lists are empty when it is set.
/// </param>
/// <param name="Item">
/// The roadmap item this conversation belongs to, or null for an ordinary one (SPEC.md §5.7). Carried here so a
/// surface holding only a conversation can offer its roadmap without asking for every roadmap.
/// </param>
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
    IReadOnlyList<ExchangeSnapshot> Exchanges,
    DestroyedConversation? Destroyed = null,
    RoadmapItemRef? Item = null);

/// <summary>
/// What is left of a conversation an operator destroyed.
/// </summary>
/// <param name="Id">Its identifier, which was its name.</param>
/// <param name="DestroyedUtc">When it was destroyed.</param>
/// <param name="Messages">How many messages went with it.</param>
/// <param name="Participants">Which agents were party to it.</param>
/// <remarks>
/// <para>
/// <b>Facts and no content.</b> SPEC.md §9.4 opens by saying Parley is a channel rather than a
/// store and argues that a log kept past its usefulness grows the blast radius of a leaked
/// database; keeping message text past a destroy is that argument inverted. So there is no field
/// here that could hold any, and there is deliberately no room to add one without the addition
/// being obvious.
/// </para>
/// <para>
/// <b>It answers a question, which is why it is returned by <c>export</c> rather than by an
/// operation of its own.</b> Somebody arrives at this holding an identifier and asking for a
/// conversation. An operator who had to already know it was destroyed in order to ask about its
/// destruction would not have been helped — and an empty export cannot be told from a store that
/// lost something, which is the failure the record exists to prevent.
/// </para>
/// </remarks>
public sealed record DestroyedConversation(
    string Id,
    DateTimeOffset DestroyedUtc,
    int Messages,
    IReadOnlyList<AgentName> Participants);
