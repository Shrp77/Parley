namespace Parley.Core;

/// <summary>
/// Enough of a conversation to choose between conversations, without reading any of them.
/// </summary>
/// <param name="Id">The identifier an agent chose for it.</param>
/// <param name="Participants">Everyone party to an exchange carrying that identifier.</param>
/// <param name="Messages">How many messages it holds.</param>
/// <param name="FirstActivityUtc">When the earliest of them was written.</param>
/// <param name="LastActivityUtc">When the latest was.</param>
/// <param name="HasOpenExchange">Whether anything in it is still awaiting a resolution.</param>
/// <remarks>
/// <para>
/// <b>Participation is derived and is not evidence of agreement.</b> SPEC.md §5.0 makes an
/// identifier a claim by the sender: an agent may name a discussion it was not part of and
/// thereby join it. So this reports who is party to what, and asserts nothing about anyone having
/// consented to be there.
/// </para>
/// <para>
/// It carries no message text on purpose. This is the list somebody scans to find the discussion
/// they mean, and a summary that quoted the traffic would be the export in a worse form —
/// unbounded, and read by whoever opened the page rather than by whoever asked for it.
/// </para>
/// </remarks>
public sealed record ConversationSummary(
    string Id,
    IReadOnlyList<AgentName> Participants,
    int Messages,
    DateTimeOffset FirstActivityUtc,
    DateTimeOffset LastActivityUtc,
    bool HasOpenExchange);

/// <summary>
/// One page of a listing, and the token for the next.
/// </summary>
/// <typeparam name="T">What is being listed.</typeparam>
/// <param name="Items">This page.</param>
/// <param name="NextPageToken">
/// What to pass to get the following page, or <see langword="null"/> at the end.
/// </param>
/// <remarks>
/// Paged from the first version rather than bounded later. The message log grows for as long as a
/// deployment runs, and SPEC.md §10.2 makes the same argument rule 8.5 makes about message size,
/// one level up: an unbounded operator query is the mistake the cap exists to prevent, and a bound
/// retrofitted is a bound nobody applies. The broker enforces its own ceiling rather than trusting
/// the caller's number.
/// </remarks>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextPageToken);
