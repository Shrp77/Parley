namespace Parley.Core;

/// <summary>How a conversation came to have its parent (SPEC.md §5.0).</summary>
public enum ConversationLinkKind
{
    /// <summary>The person started it from the parent.</summary>
    SpinOff,

    /// <summary>The person made it by moving exchanges out of the parent.</summary>
    Move,
}

/// <summary>A conversation's place among others: its parent, and the conversations that have it as theirs.</summary>
/// <param name="Parent">Its parent, or <see langword="null"/>.</param>
/// <param name="How">How it came to have that parent, or <see langword="null"/> with none.</param>
/// <param name="Children">The conversations whose parent it is.</param>
/// <remarks>Names only. The person's map, and nothing an agent reads or sets.</remarks>
public sealed record ConversationLinks(string? Parent, ConversationLinkKind? How, IReadOnlyList<string> Children)
{
    /// <summary>A conversation with no parent and no children.</summary>
    public static ConversationLinks None { get; } = new(null, null, []);
}

/// <summary>One act of moving exchanges between conversations (SPEC.md §5.0).</summary>
/// <param name="Id">Which move.</param>
/// <param name="Source">Where they came from.</param>
/// <param name="Destination">Where they went.</param>
/// <param name="MovedBy">Who moved them.</param>
/// <param name="MovedUtc">When.</param>
/// <param name="FirstMessage">The earliest message that moved, which is where a transcript draws the note, or <see langword="null"/>.</param>
/// <param name="Messages">How many messages moved.</param>
/// <param name="Exchanges">Which exchanges moved.</param>
/// <remarks>Holds no message text: it is kept when the messages it describes are not.</remarks>
public sealed record MoveRecord(
    long Id,
    string Source,
    string Destination,
    string MovedBy,
    DateTimeOffset MovedUtc,
    long? FirstMessage,
    int Messages,
    IReadOnlyList<long> Exchanges);

/// <summary>A settled pair split across two conversations, as read from one of them (SPEC.md §5.6).</summary>
/// <param name="Exchange">The exchange that was settled.</param>
/// <param name="SettledBy">The exchange that settled it.</param>
/// <param name="Conversation">The other conversation: whichever of the pair is not in the one being read.</param>
public sealed record SettledElsewhere(long Exchange, long SettledBy, string Conversation);

/// <summary>One exchange a move would take.</summary>
/// <param name="Id">The exchange.</param>
/// <param name="Open">Whether it is still in progress.</param>
/// <param name="Requester">Who asked.</param>
/// <param name="Receiver">Who was asked.</param>
/// <param name="Messages">How many messages it holds now.</param>
/// <param name="FirstWords">The start of its Request, on one line, or empty when that message has been pruned.</param>
/// <param name="AddedBecause">Why it goes though it was not picked, or <see langword="null"/> when it was picked.</param>
public sealed record MovePreviewExchange(
    long Id,
    bool Open,
    AgentName Requester,
    AgentName Receiver,
    int Messages,
    string FirstWords,
    string? AddedBecause);

/// <summary>What a move would take, changing nothing.</summary>
/// <param name="Exchanges">Everything that would go.</param>
/// <param name="FilesStaying">The files placed in the source, which stay there.</param>
/// <param name="Refused">Why it cannot be done, or <see langword="null"/>.</param>
public sealed record MovePreview(
    IReadOnlyList<MovePreviewExchange> Exchanges,
    IReadOnlyList<string> FilesStaying,
    string? Refused);

/// <summary>What became of a move.</summary>
/// <param name="Refused">Why nothing moved, or <see langword="null"/> when it was done.</param>
/// <param name="Exchanges">How many exchanges moved.</param>
/// <param name="Messages">How many messages moved.</param>
public sealed record MoveOutcome(string? Refused, int Exchanges, int Messages)
{
    /// <summary>Gets a value indicating whether anything moved.</summary>
    public bool Moved => Refused is null;
}

/// <summary>That an open exchange an agent is party to moved to another conversation (SPEC.md §5.0).</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="From">The conversation it was in.</param>
/// <param name="To">The conversation it is in now.</param>
/// <remarks>Not a message: it opens nothing, is owed nothing and wakes nobody. Two names and a number, all already bounded.</remarks>
public sealed record MoveNotice(long Exchange, string From, string To);
