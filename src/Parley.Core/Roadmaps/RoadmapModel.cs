namespace Parley.Core;

/// <summary>Where a roadmap proposal stands (SPEC.md §5.7).</summary>
public enum ProposalState
{
    /// <summary>An agent put it forward and a person has not decided. It is work waiting on the person.</summary>
    Waiting,

    /// <summary>A person confirmed it, as a roadmap or as a revision of one.</summary>
    Confirmed,

    /// <summary>A person declined it. Nothing was persisted beyond this record.</summary>
    Declined,

    /// <summary>Another proposal against the same roadmap was confirmed first.</summary>
    Superseded,
}

/// <summary>How a roadmap came to hold the words it holds (SPEC.md §5.7).</summary>
public enum RoadmapOrigin
{
    /// <summary>Confirmed exactly as the agent proposed it.</summary>
    Proposed,

    /// <summary>Confirmed or since changed by a person.</summary>
    Edited,
}

/// <summary>
/// One item of a roadmap as it is proposed, confirmed or edited, before it has an identity of its own.
/// </summary>
/// <param name="Number">
/// The item this is, when it is an existing one carried through an edit, or <see langword="null"/> for a
/// new one. It is what lets an edit that reorders a roadmap keep each item's conversation.
/// </param>
/// <param name="Text">One line, led by a verb, naming the thing to be done.</param>
/// <param name="IntendedAgent">Who it is meant for, where that is known. A suggestion and never an assignment.</param>
/// <param name="Needs">
/// The items that must be done first, as one-based positions in the list this item is in. Each must be earlier
/// than this item, which makes a cycle impossible rather than checked for.
/// </param>
/// <remarks>
/// Positions rather than identifiers because a proposal has no identifiers yet and a person reordering a draft
/// has not committed to any. An existing item is named by <paramref name="Number"/> instead.
/// </remarks>
public sealed record RoadmapItemDraft(
    int? Number,
    string Text,
    AgentName? IntendedAgent,
    IReadOnlyList<int> Needs);

/// <summary>
/// A title and an ordered list of items: what an agent proposes and what a person confirms or edits.
/// </summary>
/// <param name="Title">A few words.</param>
/// <param name="Items">The items, in order.</param>
public sealed record RoadmapContent(string Title, IReadOnlyList<RoadmapItemDraft> Items);

/// <summary>
/// A roadmap an agent has put forward (SPEC.md §5.7), as an operator reads it.
/// </summary>
/// <param name="Id">The proposal's identifier.</param>
/// <param name="Proposer">The agent that proposed it, which may since have been purged.</param>
/// <param name="SourceConversation">Where it came from, or <see langword="null"/>.</param>
/// <param name="TargetTitle">The title of the roadmap it would revise. A hint, and nothing joins on it.</param>
/// <param name="Content">The proposal as written.</param>
/// <param name="Size">Characters in the whole proposal, kept as evidence for a later bound (SPEC.md §16).</param>
/// <param name="State">Where it stands.</param>
/// <param name="CreatedUtc">When it was proposed.</param>
/// <param name="DecidedUtc">When it was decided, or <see langword="null"/> while it waits.</param>
public sealed record RoadmapProposal(
    long Id,
    AgentName Proposer,
    string? SourceConversation,
    string? TargetTitle,
    RoadmapContent Content,
    long Size,
    ProposalState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DecidedUtc);

/// <summary>
/// One roadmap in a list (SPEC.md §10.2 <c>roadmaps</c>).
/// </summary>
/// <param name="Id">The roadmap's identifier.</param>
/// <param name="Number">Its small per-project number, the <c>n</c> of <c>rm&lt;n&gt;-&lt;m&gt;</c>.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Revision">How many times it has been confirmed or edited.</param>
/// <param name="LiveItems">How many items it holds that are not removed.</param>
/// <param name="StartedItems">How many of those have a conversation.</param>
public sealed record RoadmapSummary(
    long Id, int Number, string Title, int Revision, int LiveItems, int StartedItems);

/// <summary>
/// What the broker holds about one live item's conversation, from which a surface derives where it stands.
/// </summary>
/// <param name="ConversationExists">Whether work on the item has started.</param>
/// <param name="OpenExchanges">How many exchanges in it are open.</param>
/// <param name="Collected">Whether every open exchange's request has been collected by its receiver.</param>
/// <param name="CompleteExchanges">How many closed exchanges ended with a Result that did the work or answered.</param>
/// <param name="BlockedExchanges">How many closed exchanges ended blocked.</param>
/// <param name="ClosedExchanges">How many closed exchanges ended with no Result, closed by the broker or a person.</param>
/// <param name="Blocker">The blocker kind a blocked exchange named, or <see langword="null"/>.</param>
/// <param name="ClosedBy">Who closed the conversation, or <see langword="null"/> while it is not closed.</param>
/// <param name="Destroyed">Whether the conversation was destroyed (SPEC.md §14).</param>
/// <param name="FirstSentText">The words first sent for the item, or <see langword="null"/> if none were.</param>
/// <param name="OpenExchangeIds">
/// The identifiers of the open exchanges, so a surface can join them to the wait-for graph and learn whose move each is.
/// </param>
/// <remarks>
/// <b>Facts, and never a state.</b> The broker says what it holds and the surface decides what that means, so that
/// there is one derivation (<c>ItemStates</c>) rather than one per surface, and no state is written anywhere
/// that could disagree with it (SPEC.md §5.7). Counts rather than a latest outcome because an item sent to several
/// agents holds several exchanges, and what the item has come to is all of them.
/// </remarks>
public sealed record ItemFacts(
    bool ConversationExists,
    int OpenExchanges,
    bool Collected,
    int CompleteExchanges,
    int BlockedExchanges,
    int ClosedExchanges,
    string? Blocker,
    string? ClosedBy,
    bool Destroyed,
    string? FirstSentText,
    IReadOnlyList<long>? OpenExchangeIds = null)
{
    /// <summary>Gets the open exchanges' identifiers, empty when none were given.</summary>
    public IReadOnlyList<long> OpenIds => OpenExchangeIds ?? [];
}

/// <summary>
/// One item of a confirmed roadmap, as a person reads it.
/// </summary>
/// <param name="Id">The item's identifier.</param>
/// <param name="Number">Its number within the roadmap, never reused.</param>
/// <param name="Position">Its place in the order.</param>
/// <param name="Text">What is to be done.</param>
/// <param name="IntendedAgent">Who it is meant for, or <see langword="null"/>.</param>
/// <param name="Needs">The numbers of the items it needs done first.</param>
/// <param name="ConversationName">The name its conversation has, or will have when work on it starts.</param>
/// <param name="RemovedRevision">The revision that removed it, or <see langword="null"/> while it is live.</param>
/// <param name="Facts">What the broker holds about its conversation. Present only for a live item.</param>
public sealed record RoadmapItem(
    long Id,
    int Number,
    int Position,
    string Text,
    AgentName? IntendedAgent,
    IReadOnlyList<int> Needs,
    string ConversationName,
    int? RemovedRevision,
    ItemFacts? Facts)
{
    /// <summary>Whether the item has been removed from its roadmap.</summary>
    public bool Removed => RemovedRevision is not null;
}

/// <summary>
/// A confirmed roadmap whole (SPEC.md §10.2 <c>roadmap</c>).
/// </summary>
/// <param name="Id">The roadmap's identifier.</param>
/// <param name="Number">Its small per-project number.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Origin">Whether it began as an agent's proposal as written or has been changed by a person.</param>
/// <param name="Proposer">The agent whose proposal it began as, kept as text, or <see langword="null"/>.</param>
/// <param name="Revision">How many times it has been confirmed or edited.</param>
/// <param name="Items">Every item in order, with removed ones marked.</param>
public sealed record Roadmap(
    long Id,
    int Number,
    string Title,
    RoadmapOrigin Origin,
    string? Proposer,
    int Revision,
    IReadOnlyList<RoadmapItem> Items);

/// <summary>
/// The roadmap item a conversation belongs to, as the broker's reservation records it (SPEC.md §5.7).
/// </summary>
/// <param name="RoadmapId">The roadmap's identifier.</param>
/// <param name="RoadmapNumber">Its small per-project number.</param>
/// <param name="Title">The roadmap's title.</param>
/// <param name="ItemNumber">The item's number within it.</param>
/// <param name="Removed">Whether the item has since been removed, so the conversation formerly belonged to it.</param>
/// <remarks>
/// A fact about a conversation that a surface shows, and never something to derive from its name or its
/// messages: only the reservation says an identifier is an item's, so an agent cannot make a conversation look like
/// one (SPEC.md §13).
/// </remarks>
public sealed record RoadmapItemRef(long RoadmapId, int RoadmapNumber, string Title, int ItemNumber, bool Removed);

/// <summary>
/// What a call that changes a roadmap or a proposal came to.
/// </summary>
/// <param name="Done">Whether it was done.</param>
/// <param name="Problem">A sentence saying why not, or <see langword="null"/>.</param>
/// <param name="RoadmapId">The roadmap written, when one was.</param>
/// <param name="Revision">Its revision after the change.</param>
/// <param name="Origin">How the broker found the final content related to the proposal, when it was confirmed.</param>
public sealed record RoadmapChange(
    bool Done,
    string? Problem,
    long? RoadmapId = null,
    int? Revision = null,
    RoadmapOrigin? Origin = null);

/// <summary>
/// What an agent is told about a proposal it made (SPEC.md §5.7).
/// </summary>
/// <param name="ProposalId">The proposal.</param>
/// <param name="Title">The agent's own title for it.</param>
/// <param name="Outcome">Confirmed, declined or superseded.</param>
/// <param name="CreatedUtc">When the decision was made.</param>
/// <remarks>
/// Not a message: it opens nothing, is owed nothing and wakes nobody, as a change notice for a file does not.
/// </remarks>
public sealed record RoadmapNotice(
    long ProposalId, string Title, ProposalState Outcome, DateTimeOffset CreatedUtc);
