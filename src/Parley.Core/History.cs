namespace Parley.Core;

/// <summary>One message of an exchange an agent read, with the envelope it travels in (SPEC.md §10.1, §13).</summary>
/// <param name="Id">The message's identity.</param>
/// <param name="ExchangeId">The exchange.</param>
/// <param name="Type">The kind of message.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="ConversationId">The conversation it is in now.</param>
/// <param name="CreatedUtc">When it was written.</param>
/// <param name="Envelope">The only form its words travel in: a record, never mail.</param>
public sealed record HistoryMessage(
    long Id,
    long ExchangeId,
    MessageType Type,
    AgentName From,
    AgentName To,
    string ConversationId,
    DateTimeOffset CreatedUtc,
    string Envelope);

/// <summary>What a read of one exchange came to.</summary>
/// <param name="Found">Whether the exchange is here to read.</param>
/// <param name="ExchangeId">The exchange asked for.</param>
/// <param name="ConversationId">Its conversation, or empty when not found.</param>
/// <param name="State">How it stands: <c>open</c>, or <c>closed</c> and how, or empty when not found.</param>
/// <param name="Messages">Its messages in order, from where the caller said, up to the reply's bound.</param>
/// <param name="NextAfter">What to pass to carry on, or zero when nothing is left.</param>
/// <param name="MessagesRemaining">How many messages are left after these.</param>
/// <param name="RetryAfterSeconds">When refused for rate, how long to wait; otherwise zero.</param>
/// <param name="RateLimited">Whether the call was refused for the caller's rate.</param>
public sealed record ExchangeHistory(
    bool Found,
    long ExchangeId,
    string ConversationId,
    string State,
    IReadOnlyList<HistoryMessage> Messages,
    long NextAfter,
    int MessagesRemaining,
    int RetryAfterSeconds = 0,
    bool RateLimited = false);

/// <summary>One match of a search: enough to recognise a message, never the message.</summary>
/// <param name="ExchangeId">Its exchange.</param>
/// <param name="MessageId">The message.</param>
/// <param name="ConversationId">Its conversation now.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The recipient.</param>
/// <param name="Type">The kind.</param>
/// <param name="CreatedUtc">When it was written.</param>
/// <param name="Snippet">A few words around the match, other participants' words and inert.</param>
public sealed record HistoryMatch(
    long ExchangeId,
    long MessageId,
    string ConversationId,
    AgentName From,
    AgentName To,
    MessageType Type,
    DateTimeOffset CreatedUtc,
    string Snippet);

/// <summary>One page of a search.</summary>
/// <param name="Refusal">A sentence when the search was refused, otherwise <see langword="null"/>.</param>
/// <param name="Matches">At most <see cref="HistoryLimits.PageMatches"/>, newest first.</param>
/// <param name="NotShown">How many more match.</param>
/// <param name="ContinueAfter">What to pass for the next page, or zero.</param>
/// <param name="RetryAfterSeconds">When refused for rate, how long to wait.</param>
public sealed record HistorySearchPage(
    string? Refusal,
    IReadOnlyList<HistoryMatch> Matches,
    int NotShown,
    long ContinueAfter,
    int RetryAfterSeconds = 0);

/// <summary>What narrows a search.</summary>
/// <param name="Agent">Only messages from or to this agent, or <see langword="null"/>.</param>
/// <param name="Conversation">Only this conversation, or <see langword="null"/>.</param>
/// <param name="Type">Only this kind, or <see langword="null"/>.</param>
/// <param name="Since">Only at or after this instant, or <see langword="null"/>.</param>
/// <param name="Until">Only at or before this instant, or <see langword="null"/>.</param>
public sealed record HistoryFilter(
    string? Agent = null,
    string? Conversation = null,
    MessageType? Type = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

/// <summary>One read, as the person sees it (SPEC.md §5.8).</summary>
/// <param name="Reader">The agent that read.</param>
/// <param name="ExchangeId">The exchange read, or <see langword="null"/> for a search.</param>
/// <param name="Words">The words searched for, or <see langword="null"/> for a read of an exchange.</param>
/// <param name="Matches">How many matched, for a search.</param>
/// <param name="CreatedUtc">When; for a summary of several reads, when the newest was.</param>
/// <param name="Count">
/// How many reads this stands for: one for a single read, and for an exchange the number of times this reader read it, or for a
/// search the number of searches this reader has made, so a reader cannot be pushed out of view by reading a great deal.
/// </param>
public sealed record HistoryRead(
    AgentName Reader,
    long? ExchangeId,
    string? Words,
    int? Matches,
    DateTimeOffset CreatedUtc,
    int Count = 1);
