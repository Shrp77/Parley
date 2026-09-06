using System.Globalization;
using Parley.Client.Views;
using Parley.Core;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

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

    /// <summary>What is shown when a project has had none.</summary>
    internal const string Nothing =
        "No conversation has happened in this project yet. One begins when an agent asks another "
        + "something, or when you do from a seat.";

    private readonly ListView _list = Lists.Filling();

    private List<ConversationSummary> _conversations = [];

    internal ConversationsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        Add(_list);

        // The list's own accept rather than a key handler on this region. Enter reaches the
        // focused view first and the list takes it, so a region-level handler never runs — which
        // is how the first version of this quietly did nothing. A double click raises the same
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

    /// <summary>Draws a project's conversations.</summary>
    /// <param name="conversations">What the broker said, or null when it could not be asked.</param>
    /// <param name="problem">Why the broker could not be asked, or null when it could.</param>
    /// <param name="archivedOmitted">How many archived conversations were left out.</param>
    /// <param name="showingArchived">Whether the archived ones were asked for.</param>
    internal void Show(
        IReadOnlyList<ConversationSummary>? conversations,
        string? problem,
        int archivedOmitted = 0,
        bool showingArchived = false)
    {
        var keep = Selected?.Id;

        _conversations = problem is null ? [.. conversations ?? []] : [];

        // **Said whenever anything is hidden, and that is what makes the archived state safe.**
        // Archiving does not stop a conversation receiving messages, and a message does not
        // un-archive it, so without this line a busy conversation somebody filed away last month is
        // invisible with nothing saying so.
        var hidden = ConversationsView.Hidden(archivedOmitted, showingArchived);

        var lines = problem is not null
            ? [Safe.Text(problem)]
            : _conversations.Count == 0
                ? new List<string> { archivedOmitted > 0 ? ConversationsView.AllArchived : Nothing }
                : [.. _conversations.Select(Line)];

        if (hidden.Length > 0 && problem is null)
        {
            lines.Add(string.Empty);
            lines.Add(hidden);
        }

        _list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(lines));

        var at = keep is null ? -1 : _conversations.FindIndex(c => c.Id == keep);

        // Something is always on, so that Enter has something to open the moment the region has
        // focus. A list whose selection is null until somebody presses an arrow makes the first
        // Enter do nothing at all, which reads as a surface that has not finished loading.
        _list.SelectedItem = at >= 0 ? at : _conversations.Count > 0 ? 0 : null;
    }

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
    /// actually remembers a conversation by.
    /// </remarks>
    private static string Line(ConversationSummary conversation) =>
        $"{Safe.Text(conversation.Id)}  "
        + $"({string.Join(", ", conversation.Participants.Select(p => Safe.Text(p.Value)))})  "
        + $"{conversation.Messages.ToString(CultureInfo.InvariantCulture)} messages  "
        + $"[{(conversation.HasOpenExchange ? Open : Settled)}]  "
        + conversation.LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
