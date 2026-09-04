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

    private readonly ListView _lines = new() { Width = Dim.Fill(), Height = Dim.Fill() };

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

        _written = problem
            ?? (export is null || export.Messages.Count == 0
                ? Empty
                : Transcript.Write(conversationId, project, export.Messages, export.Exchanges ?? []));

        Redraw();
    }

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
    private void Redraw()
    {
        var width = Viewport.Width > 0 ? Viewport.Width : 80;

        var wrapped = _written
            .ReplaceLineEndings("\n")
            .Split('\n')
            .SelectMany(line => Wrap(line, width))
            .ToList();

        _lines.SetSource(new ObservableCollection<string>(wrapped));
    }
}
