using Parley.Core;

namespace Parley.Client.Views;

/// <summary>How a conversations list is ordered.</summary>
public enum SortBy
{
    /// <summary>Most recently active first.</summary>
    /// <remarks>
    /// Zero, so the default order is the one a person opening a project almost always wants: what
    /// has been happening. It is also the order the broker returns, so the default costs nothing.
    /// </remarks>
    Activity = 0,

    /// <summary>By identifier, ordinally.</summary>
    Name = 1,
}

/// <summary>One conversation, as the list shows it.</summary>
/// <param name="Id">Its identifier, which is its name.</param>
/// <param name="LastActivityUtc">When the most recent message in it was written.</param>
/// <param name="Messages">How many it holds.</param>
/// <param name="Participants">Who is party to it.</param>
/// <param name="HasOpenExchange">Whether anything in it is still awaiting a resolution.</param>
/// <param name="Archived">Whether an operator has put it out of the way.</param>
/// <param name="ActiveSinceArchived">
/// Whether a message has arrived since it was archived. Always false when it is not archived.
/// </param>
public sealed record ConversationRow(
    string Id,
    DateTimeOffset LastActivityUtc,
    int Messages,
    IReadOnlyList<AgentName> Participants,
    bool HasOpenExchange,
    bool Archived,
    bool ActiveSinceArchived);

/// <summary>
/// The conversations half of a project view: a list you can order, and a description of one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here needs a new broker operation.</b> SPEC.md §10.2's <c>conversations()</c> already
/// returns the identifier, the participants, the message count, first and last activity and whether
/// anything is open, plus an archived flag and an include-archived option — everything the list and
/// the detail panel ask for.
/// </para>
/// <para>
/// <b>The highlight is a conversation, never a row number.</b> Sorting a list by moving the rows
/// under a numeric selection moves the selection to a different conversation, which is the kind of
/// thing nobody notices in a test that sorts an empty list.
/// </para>
/// </remarks>
public static class ConversationsView
{
    /// <summary>What to say when a project holds no conversations.</summary>
    public const string Nothing = "No conversations here yet.";

    /// <summary>What to say when every conversation there is has been archived.</summary>
    /// <remarks>
    /// A different fact from a project nothing has ever discussed, and the difference is one a
    /// person can act on: turning the toggle on brings them back. Saying "no conversations" to
    /// somebody who archived six would read as having lost them.
    /// </remarks>
    public const string AllArchived = "Every conversation here is archived.";

    /// <summary>Orders a page of conversations.</summary>
    /// <param name="rows">The conversations.</param>
    /// <param name="by">How to order them.</param>
    /// <returns>The rows in that order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <remarks>
    /// Ties break on the identifier in both orders, so the sort is total. Two conversations last
    /// active in the same millisecond are not rare — the Windows system clock ticks about every
    /// 15.6ms — and an unstable order would shuffle rows under a person on every redraw.
    /// </remarks>
    public static IReadOnlyList<ConversationRow> Sorted(
        IReadOnlyList<ConversationRow> rows, SortBy by)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return by == SortBy.Name
            ? [.. rows.OrderBy(r => r.Id, StringComparer.Ordinal)]
            : [.. rows.OrderByDescending(r => r.LastActivityUtc)
                      .ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>Turns a page of summaries into rows.</summary>
    /// <param name="summaries">What the broker returned.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summaries"/> is null.</exception>
    /// <remarks>
    /// <c>ActiveSinceArchived</c> is left false here and set by whoever knows when the archiving
    /// happened. The summary does not carry that instant, and inventing it from the last activity
    /// would mark every archived conversation as busy.
    /// </remarks>
    public static IReadOnlyList<ConversationRow> Rows(IReadOnlyList<ConversationSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        return
        [
            .. summaries.Select(s => new ConversationRow(
                s.Id,
                s.LastActivityUtc,
                s.Messages,
                s.Participants,
                s.HasOpenExchange,
                s.Archived,
                ActiveSinceArchived: false))
        ];
    }

    /// <summary>What the list says under the rows, about what it is not showing.</summary>
    /// <param name="archivedOmitted">How many archived conversations were left out.</param>
    /// <param name="showingArchived">Whether the toggle is on.</param>
    /// <returns>The line, or empty when there is nothing to say.</returns>
    /// <remarks>
    /// <b>Always present when anything is hidden.</b> Archiving does not stop a conversation
    /// receiving messages, and an operator's decision not to un-archive on a message (SPEC.md
    /// §10.2) is only safe because this line exists: without it, a busy conversation somebody filed
    /// away last month is invisible with nothing saying so.
    /// </remarks>
    public static string Hidden(int archivedOmitted, bool showingArchived)
    {
        if (showingArchived || archivedOmitted <= 0)
        {
            return string.Empty;
        }

        return archivedOmitted == 1
            ? "1 archived conversation is not shown."
            : $"{archivedOmitted} archived conversations are not shown.";
    }

    /// <summary>Describes the highlighted conversation.</summary>
    /// <param name="row">The conversation, or null when nothing is highlighted.</param>
    /// <returns>The lines of the detail panel.</returns>
    public static IReadOnlyList<string> Describe(ConversationRow? row)
    {
        if (row is null)
        {
            return [];
        }

        var lines = new List<string>
        {
            row.Id,
            $"{row.Messages} message{(row.Messages == 1 ? string.Empty : "s")}",
            row.Participants.Count == 0
                ? "nobody is party to it"
                : string.Join(", ", row.Participants.Select(p => p.Value)),
        };

        if (row.HasOpenExchange)
        {
            lines.Add("something in it is still open");
        }

        if (row.Archived)
        {
            lines.Add(row.ActiveSinceArchived
                ? "archived, and it has had messages since"
                : "archived");
        }

        return lines;
    }
}
