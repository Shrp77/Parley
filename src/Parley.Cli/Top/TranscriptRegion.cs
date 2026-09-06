using System.Collections.ObjectModel;
using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// One conversation, message by message, with who saw each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The text is <see cref="Transcript.Write"/>'s, not this region's.</b> The web console renders
/// the same conversation from the same function, which is what makes SC-005 true by construction
/// rather than by two implementations being watched. A region that formatted messages itself would
/// be the second renderer, and the two would disagree about visibility on the day it mattered.
/// </para>
/// <para>
/// <b>It wraps rather than truncating, and scrolls rather than fitting.</b> A message is capped at
/// eight thousand characters and a conversation is not capped at all, so a region that showed what
/// fitted and dropped the rest would silently be showing part of a transcript to somebody deciding
/// something. The 004 defect this inherits the rule from was a recipient column dropped at forty
/// columns with nothing said.
/// </para>
/// </remarks>
internal sealed class TranscriptRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Transcript";

    /// <summary>What is shown before a conversation has been opened.</summary>
    internal const string Nothing = "No conversation is open. Choose one and press Enter.";

    /// <summary>What is shown for a conversation the broker holds no messages for.</summary>
    internal const string Empty = "That conversation has no messages.";

    /// <summary>What is said when the identifier names a conversation somebody destroyed.</summary>
    /// <remarks>
    /// Distinct from <see cref="Empty"/> on purpose, and that distinction is the whole of FR-006.
    /// A conversation with no messages is one nothing was said in; a destroyed one is one whose
    /// messages were taken away. Saying the first for the second leaves an operator unable to tell
    /// a destroy from a store that lost something.
    /// </remarks>
    internal const string Destroyed = "That conversation was destroyed.";

    private readonly ListView _lines = Lists.Filling();

    private string _written = Nothing;

    internal TranscriptRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        Add(_lines);
        Redraw();
    }

    /// <summary>Which conversation is open, or null when none is.</summary>
    internal string? Open { get; private set; }

    /// <summary>The whole transcript as written, whether or not it all fits.</summary>
    internal string Written => _written;

    /// <summary>Draws a conversation.</summary>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">The messages and the state of their exchanges, or null when unread.</param>
    /// <param name="problem">Why the broker could not be asked, or null when it could.</param>
    internal void Show(
        string conversationId,
        string project,
        ConversationExport? export,
        string? problem)
    {
        Open = problem is null ? conversationId : null;

        // **Read on screen rather than exported to it.** What was here was `Transcript.Write`,
        // which is the export -- one flattened line of JSON per message, times in UTC, no breaks
        // anywhere. That is right for a file somebody takes away and reads elsewhere, and it is
        // what the owner called "one long ass blob that is not readable by a human" when it was put
        // on a screen. `Reading` lays the same messages out for a person: the author's own line
        // breaks kept, JSON indented, times in the reader's own zone, and every header at column
        // zero so it can be told from body text with all colour stripped.
        //
        // Neutralised line by line either way: the layout is this surface's and the content is the
        // agents' own, and a payload is eight thousand characters of whatever they chose to send.
        // The indent is what keeps that safe now -- a peer writing a line shaped like a header
        // still arrives indented, so it cannot be mistaken for one.
        _written = Safe.Lines(
            problem
            ?? (export?.Destroyed is { } gone
                ? Destroyed + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, Reading.Gone(gone))
                : export is null || export.Messages.Count == 0
                    ? Empty
                    : Lay(conversationId, project, export)));

        Redraw();
    }

    /// <summary>Lays a conversation out for reading.</summary>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">Its messages and their exchange states.</param>
    /// <returns>The transcript, ready to wrap.</returns>
    /// <remarks>
    /// A heading of its own before the messages, because the export's heading said which
    /// conversation and in which project, and losing that would make a screenshot of a transcript
    /// unattributable. The zone is said once rather than on every line: repeating an offset per
    /// message would spend the width a message body needs.
    /// </remarks>
    private static string Lay(string conversationId, string project, ConversationExport export)
    {
        var lines = Reading.Lines(export.Messages, export.Exchanges ?? []);
        var written = new System.Text.StringBuilder()
            .Append("Conversation ").Append(conversationId)
            .Append(" in project ").AppendLine(project)
            .AppendLine(LocalTime.Note())
            .AppendLine();

        foreach (var line in lines)
        {
            written.AppendLine(line.Text);
        }

        return written.ToString();
    }

    /// <summary>Lays the transcript out again for a width.</summary>
    /// <param name="width">How many columns there are.</param>
    /// <remarks>
    /// Rebuilt from the written transcript rather than from the lines already wrapped, so
    /// narrowing and widening is not a lossy round trip. A region that wrapped its own output
    /// would break the first wrap's lines again on the second pass, and those on the third.
    /// </remarks>
    internal void Resize(int width) => Redraw(width);

    /// <summary>Closes whatever is open.</summary>
    internal void Clear()
    {
        Open = null;
        _written = Nothing;
        Redraw();
    }

    /// <summary>Wraps a line to a width, breaking between words where it can.</summary>
    /// <param name="line">The line.</param>
    /// <param name="width">How many columns are available.</param>
    /// <returns>The pieces, in order. Never empty.</returns>
    /// <remarks>
    /// A word longer than the width is broken rather than left to overflow, because the
    /// alternative loses its tail off the edge of the region — and the words most likely to be
    /// longer than a narrow region are identifiers and paths, which are exactly the ones somebody
    /// is reading the transcript to check.
    /// </remarks>
    internal static IReadOnlyList<string> Wrap(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (width <= 0 || line.Length <= width)
        {
            return [line];
        }

        var pieces = new List<string>();
        var rest = line;

        while (rest.Length > width)
        {
            var at = rest.LastIndexOf(' ', width);

            if (at <= 0)
            {
                at = width;
            }

            pieces.Add(rest[..at].TrimEnd());
            rest = rest[at..].TrimStart();

            if (rest.Length == 0)
            {
                return pieces;
            }
        }

        pieces.Add(rest);

        return pieces;
    }

    /// <inheritdoc/>
    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        Redraw();
    }

    /// <summary>Lays the written transcript out for the width there is.</summary>
    private void Redraw() => Redraw(Viewport.Width > 0 ? Viewport.Width : 80);

    /// <summary>Lays the written transcript out for a given width.</summary>
    /// <param name="width">How many columns there are.</param>
    private void Redraw(int width)
    {
        var wrapped = _written
            .ReplaceLineEndings("\n")
            .Split('\n')
            .SelectMany(line => Wrap(line, width))
            .ToList();

        _lines.SetSource(new ObservableCollection<string>(wrapped));
    }
}
