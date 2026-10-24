using System.Globalization;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The conversations in the selected project, most recent first.
/// </summary>
/// <remarks>
/// <para>
/// A conversation identifier is opaque and chosen by an agent (SPEC.md §5.0), so the same one may
/// exist in two projects. Nothing here is reachable without the project it belongs to — opening
/// one without saying which is a coin toss the reader would never know had been made.
/// </para>
/// <para>
/// <b>The list does not move when a transcript opens.</b> The transcript has its own region, so
/// reading one conversation and then the next is an arrow and a key rather than a journey back
/// out. That is most of what the four-region layout is for.
/// </para>
/// </remarks>
internal sealed class ConversationsRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Conversations";

    /// <summary>An exchange nobody has settled.</summary>
    internal const string Open = "open";

    /// <summary>An exchange that has been concluded.</summary>
    internal const string Settled = "settled";

    /// <summary>What is shown when a project has had none, a short row at a time.</summary>
    /// <remarks>
    /// Rows rather than one sentence, because a list row does not wrap: a sentence wider than the half-width pane a
    /// project view gives this region would be cut, and the cut half is the one that says how to start a
    /// conversation. Each row fits in the narrowest pane this surface draws.
    /// </remarks>
    internal static readonly IReadOnlyList<string> Nothing =
    [
        "No conversation here yet.",
        "To start one, press Alt+k.",
    ];

    private readonly GuardedList _list = Lists.Filling(Colours.Conversations);

    private List<ConversationSummary> _conversations = [];

    internal ConversationsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));
        Add(_list);
        Lists.Tint(_list, Tint);

        // The list's own accept rather than a key handler on this region. Enter reaches the
        // focused view first and the list takes it, so a region-level handler never runs. A double click raises the same
        // event, which is what makes the mouse route and the key route one code path (FR-003).
        _list.Accepting += (_, args) =>
        {
            if (OpenSelected())
            {
                args.Handled = true;
            }
        };
    }

    /// <summary>Raised when the person opens the conversation they are on.</summary>
    internal event EventHandler<ConversationSummary>? Opened;

    /// <summary>The conversations as drawn, in order.</summary>
    internal IReadOnlyList<ConversationSummary> Conversations => _conversations;

    /// <summary>Whichever conversation the person is on, or null when there are none.</summary>
    internal ConversationSummary? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _conversations.Count
            ? _conversations[at]
            : null;

    /// <summary>The list, so a test can pick a row by its text.</summary>
    internal ListView List => _list;

    /// <summary>The note under a list filtered to one agent's conversations.</summary>
    /// <param name="agent">The agent.</param>
    /// <returns>The note, naming the agent and how to see the rest.</returns>
    internal static string FilteredTo(AgentName agent) =>
        $"  Only conversations {Safe.Text(agent.Value)} is in. Esc shows them all.";

    /// <summary>Draws a project's conversations.</summary>
    /// <param name="conversations">What the broker said, or null when it could not be asked.</param>
    /// <param name="problem">Why the broker could not be asked, or null when it could.</param>
    /// <param name="archivedOmitted">How many archived conversations were left out.</param>
    /// <param name="showingArchived">Whether the archived ones were asked for.</param>
    /// <param name="only">The agent the list is filtered to, or null when it is not filtered.</param>
    internal void Show(
        IReadOnlyList<ConversationSummary>? conversations,
        string? problem,
        int archivedOmitted = 0,
        bool showingArchived = false,
        AgentName? only = null)
    {
        var keep = Selected?.Id;

        // A local until it goes in with the rows: the colour of a row is read from this list while
        // the list is drawn, so the two must never be a draw apart.
        var summaries = problem is null ? [.. conversations ?? []] : new List<ConversationSummary>();

        // **Said whenever anything is hidden, and that is what makes the archived state safe.**
        // Archiving does not stop a conversation receiving messages, and a message does not
        // un-archive it, so without this line a busy conversation somebody filed away last month is
        // invisible with nothing saying so.
        var hidden = ConversationsView.Hidden(archivedOmitted, showingArchived);

        var lines = problem is not null
            ? [Safe.Text(problem)]
            : summaries.Count == 0
                ? archivedOmitted > 0 ? new List<string> { ConversationsView.AllArchived } : [.. Nothing]
                : [.. summaries.Select(Line)];

        if (hidden.Length > 0 && problem is null)
        {
            lines.Add(string.Empty);
            lines.Add(hidden);
        }

        // **A filter is said on screen whenever one is on** (FR-020), for the reason the archived line above is: rows left
        // out with nothing saying so are indistinguishable from rows that do not exist. After the rows, like that line,
        // so a row's index is still its conversation's.
        if (only is { } agent && problem is null)
        {
            lines.Add(string.Empty);
            lines.Add(FilteredTo(agent));
        }

        var at = keep is null ? -1 : summaries.FindIndex(c => c.Id == keep);

        // Something is always on, so that Enter has something to open the moment the region has
        // focus. A list whose selection is null until somebody presses an arrow makes the first
        // Enter do nothing at all, which reads as a surface that has not finished loading.
        _list.Replace(
            [.. lines],
            at >= 0 ? at : summaries.Count > 0 ? 0 : null,
            also: () => _conversations = summaries);
    }

    /// <summary>What colour a row earns from where its exchanges stand.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null for a row that is not a conversation.</returns>
    /// <remarks>
    /// <para>
    /// Open and settled, and nothing else — because those are the two words this region writes.
    /// An archived conversation is deliberately <i>not</i> dimmed here: this list does not say
    /// "archived" on the row, so a dim row would be the one fact on the screen that exists only as
    /// a colour, which FR-030 forbids and which the suite could not see. The colour follows the
    /// words rather than getting ahead of them.
    /// </para>
    /// <para>
    /// Open is the one that is coloured because it is the one somebody can act on: an exchange
    /// nobody has settled is a peer still waiting.
    /// </para>
    /// </remarks>
    private Color? Tint(int row) =>
        row >= 0 && row < _conversations.Count
            ? _conversations[row].HasOpenExchange ? Colours.Warn : Colours.Quiet
            : null;

    /// <summary>Opens whatever the person is on.</summary>
    /// <returns>Whether there was anything to open.</returns>
    /// <remarks>
    /// The one action of this region, reachable by the key, by a double click, and by a caller.
    /// FR-003 asks for every action to be available both ways, and the way to be sure of that is
    /// for both routes to end in the same call rather than in two copies of it.
    /// </remarks>
    internal bool OpenSelected()
    {
        if (Selected is not { } conversation)
        {
            return false;
        }

        Opened?.Invoke(this, conversation);

        return true;
    }

    /// <summary>How one conversation reads.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>Its identifier, who was in it, and where it stands.</returns>
    /// <remarks>
    /// The identifier alone is often a hash somebody's adapter generated, which distinguishes two
    /// rows without telling a reader which one they meant. The participants are what a person
    /// actually remembers a conversation by. A roadmap item's conversation says which item, because
    /// <c>rm1-3</c> alone says nothing.
    /// </remarks>
    private static string Line(ConversationSummary conversation) =>
        $"{Safe.Text(conversation.Id)}  "
        + (conversation.Item is { } item ? $"[{Safe.Text(Parley.Client.Roadmaps.ItemWording.Belongs(item))}]  " : string.Empty)
        + $"({string.Join(", ", conversation.Participants.Select(p => Safe.Text(p.Value)))})  "
        + $"{conversation.Messages.ToString(CultureInfo.InvariantCulture)} messages  "
        + $"[{(conversation.HasOpenExchange ? Open : Settled)}]  "
        + conversation.LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
