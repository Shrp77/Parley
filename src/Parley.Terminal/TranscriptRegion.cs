using Parley.Client.Rendering;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// One conversation, message by message, with who saw each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The text is <see cref="Reading"/>'s, not this region's.</b> The web console lays the same
/// conversation out from the same function, which is what makes SC-005 true by construction rather
/// than by two implementations being watched. A region that formatted messages itself would be the
/// second renderer, and the two would disagree about visibility on the day it mattered.
/// </para>
/// <para>
/// <b>This paragraph named <see cref="Transcript.Write"/> until a review lens read it.</b> That is
/// the export — one flattened line of JSON per message — and 006 stopped drawing it here, saying so
/// in a comment inside <see cref="Show"/> that contradicted this summary from ten lines away. A
/// remark that has drifted is worse than none: it is the account a reader trusts instead of
/// reading on.
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

    private readonly GuardedList _lines = Lists.Filling(Colours.Transcript);

    private string _written = Nothing;
    private IReadOnlyList<TranscriptLine> _marked = [];
    private List<Color?> _tints = [];

    // Set only for the length of one redraw by Follow, when the reader was on the last row: they are
    // reading the conversation as it arrives, so the new last row is where they belong.
    private bool _followEnd;

    internal TranscriptRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Transcript));
        Add(_lines);
        Lists.Tint(_lines, at => at >= 0 && at < _tints.Count ? _tints[at] : null);
        Say(Nothing, LineKind.Annotation);
    }

    /// <summary>Which conversation is open, or null when none is.</summary>
    internal string? Open { get; private set; }

    /// <summary>The whole transcript as written, whether or not it all fits.</summary>
    internal string Written => _written;

    /// <summary>The list, so a test can read a row and the colour it is drawn in.</summary>
    /// <remarks>
    /// The same accessor the other three regions carry, added for the same reason: <c>Written</c>
    /// is the transcript before it was wrapped to a width, and what is on screen is the wrap.
    /// </remarks>
    internal ListView List => _lines;

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
        if (problem is not null)
        {
            Say(problem, LineKind.Annotation);

            return;
        }

        Mark(Laid(conversationId, project, export));
    }

    /// <summary>Draws the open conversation again, if what it holds has changed.</summary>
    /// <param name="conversationId">Which conversation this read was of.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">What the broker holds now.</param>
    /// <returns>Whether anything was redrawn.</returns>
    /// <remarks>
    /// <para>
    /// <b>Asked for by the owner, whose answer arrived in forty-seven seconds and was not on screen until
    /// they closed the transcript and opened it again.</b> <see cref="Show"/> was the only way in, and it
    /// is called once, when a conversation is opened.
    /// </para>
    /// <para>
    /// <b>The web console decided the opposite, and the reasoning it recorded is answered rather than
    /// ignored.</b> It says "a page that changed under a reader mid-sentence would be worse than one that
    /// is briefly out of date". That is true of a page that redraws from the top. This does not move
    /// what somebody is reading: a transcript only grows at the end, so the row under the cursor is the
    /// same row afterwards — and a reader on the last row, who is reading as it arrives, is kept on the
    /// last row. A reader who has not moved the cursor at all is left at the top, because nothing says
    /// they are waiting at the bottom.
    /// </para>
    /// <para>
    /// Compared on the written text rather than on a message count. A person's close, a destruction or an
    /// exchange changing state is a different transcript with the same number of messages in it.
    /// </para>
    /// </remarks>
    internal bool Follow(string conversationId, string project, ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        // The reader opened something else, or closed it, since this read was queued.
        if (!string.Equals(Open, conversationId, StringComparison.Ordinal))
        {
            return false;
        }

        var lines = Laid(conversationId, project, export);
        var written = Safe.Lines(string.Join(Environment.NewLine, lines.Select(line => line.Text)));

        if (string.Equals(written, _written, StringComparison.Ordinal))
        {
            return false;
        }

        var rows = _lines.Source?.Count ?? 0;

        _followEnd = rows > 0 && _lines.SelectedItem is { } at && at >= rows - 1;

        try
        {
            Mark(lines);
        }
        finally
        {
            _followEnd = false;
        }

        return true;
    }

    /// <summary>Lays a conversation out, whatever state it is in.</summary>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">What the broker holds, or null when it holds nothing.</param>
    /// <returns>The lines to draw.</returns>
    /// <remarks>
    /// One layout for opening and for following, so the two cannot come to disagree about what a
    /// destroyed or an empty conversation looks like — which would make every refresh of one a redraw.
    /// </remarks>
    private IReadOnlyList<TranscriptLine> Laid(
        string conversationId, string project, ConversationExport? export)
    {
        if (export?.Destroyed is { } gone)
        {
            // Indented like any other body text, so that column zero means one thing on this
            // screen and not two. These lines are this surface's own words rather than an agent's,
            // so they were never a forgery — but a rule with an exception in it is a rule a reader
            // has to hold two versions of, and the one that matters is checkable only if it is
            // exact: nothing but a header begins at column zero.
            return
            [
                new TranscriptLine(Destroyed, LineKind.Header, null),
                new TranscriptLine(string.Empty, LineKind.Blank, null),
                .. Reading.Gone(gone).Select(line =>
                    new TranscriptLine(new string(' ', Reading.Indent) + line, LineKind.Body, null)),
            ];
        }

        if (export is null || export.Messages.Count == 0)
        {
            return [new TranscriptLine(new string(' ', Reading.Indent) + Empty, LineKind.Annotation, null)];
        }

        return Lay(conversationId, project, export);
    }

    /// <summary>Lays a conversation out for reading.</summary>
    /// <param name="conversationId">Which conversation.</param>
    /// <param name="project">The project it came from.</param>
    /// <param name="export">Its messages and their exchange states.</param>
    /// <returns>The transcript, line by line and each line knowing what it is.</returns>
    /// <remarks>
    /// <para>
    /// A heading of its own before the messages, because the export's heading said which
    /// conversation and in which project, and losing that would make a screenshot of a transcript
    /// unattributable. The zone is said once rather than on every line: repeating an offset per
    /// message would spend the width a message body needs.
    /// </para>
    /// <para>
    /// <b>Kept as lines rather than flattened to a string.</b> It was flattened, and the sender's
    /// colour slot — which <see cref="Reading"/> already works out, and which the console already
    /// draws with — was thrown away on the way to the screen. A terminal that then wanted it back
    /// would have had to work out who sent a line by matching the text of the header, which is a
    /// second derivation of something the shared renderer had already decided.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<TranscriptLine> Lay(
        string conversationId, string project, ConversationExport export) =>
    [
        new TranscriptLine(
            $"Conversation {conversationId} in project {project}", LineKind.Header, null),
        // Body rather than an annotation, and the difference is emphasis. `Reading` makes an
        // annotation for one thing — who was *not* party to a message — which SC-006 calls the one
        // fact on a transcript recoverable from nowhere else, and which is drawn to be noticed. The
        // zone this surface renders times in is a note about the rendering, and it was the same
        // colour until a dump of the screen put the two side by side.
        new TranscriptLine(new string(' ', Reading.Indent) + LocalTime.Note(), LineKind.Body, null),
        new TranscriptLine(string.Empty, LineKind.Blank, null),
        .. Reading.Lines(export.Messages, export.Exchanges ?? []),
    ];

    /// <summary>Puts one line on screen, and nothing else.</summary>
    /// <param name="said">What it says.</param>
    /// <param name="kind">What kind of line it is.</param>
    /// <remarks>
    /// Indented like everything that is not a header. Two of the three things said this way are
    /// this surface's own words, but the third is a broker's — <c>problem</c> is whatever came
    /// back from the far end of a gRPC call, and a broker able to choose that text could otherwise
    /// choose one shaped like a header and place it at column zero.
    /// </remarks>
    private void Say(string said, LineKind kind) =>
        Mark([new TranscriptLine(new string(' ', Reading.Indent) + said, kind, null)]);

    /// <summary>Takes a laid-out transcript and draws it.</summary>
    /// <param name="lines">The lines, each knowing what it is and who wrote it.</param>
    /// <remarks>
    /// Neutralised line by line: the layout is this surface's and the content is the agents' own,
    /// and a payload is eight thousand characters of whatever they chose to send. The indent is
    /// what keeps that safe — a peer writing a line shaped like a header still arrives indented,
    /// so it cannot be mistaken for one.
    /// </remarks>
    private void Mark(IReadOnlyList<TranscriptLine> lines)
    {
        _marked = lines;
        _written = Safe.Lines(string.Join(Environment.NewLine, lines.Select(line => line.Text)));

        Redraw();
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
        Say(Nothing, LineKind.Annotation);
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
    /// <remarks>
    /// <para>
    /// The colour of every row is worked out here rather than kept beside the source lines,
    /// because wrapping is what decides how many rows there are: a header that needs two rows
    /// needs both of them in the sender's colour, and a list of colours built before the wrap
    /// would be one entry short of the rows it describes.
    /// </para>
    /// <para>
    /// <b>Continuations are indented, and that is what makes FR-018 true rather than nearly
    /// true.</b> The rule the whole view rests on is that a header sits at column zero and nothing
    /// else does, so the distinction survives every colour being stripped — and
    /// <see cref="Wrap"/> trims the leading space off each piece it breaks off, which is the
    /// indent that <i>is</i> the rule. A payload is agent-written and eight thousand characters
    /// long, so its author picks where the break lands: seventy-six filler characters and then a
    /// plausible header put <c>billing@here -&gt; orders@here (Answer)</c> at column zero, in a
    /// message <c>billing@here</c> had no part in.
    /// </para>
    /// <para>
    /// Colour happened to give that away — a body line keeps the terminal's own foreground — which
    /// is exactly the crutch FR-018 forbids leaning on, and is why this is fixed in the structure
    /// instead.
    /// </para>
    /// <para>
    /// Wrapped to <see cref="Reading.Indent"/> columns short so that an indented continuation
    /// still fits the width. The first piece gives up those columns too, which is a few characters
    /// of ragged edge in exchange for a rule with no width at which it stops holding.
    /// </para>
    /// </remarks>
    private void Redraw(int width)
    {
        var wrapped = new List<string>();
        var tints = new List<Color?>();
        var room = width > 0 ? Math.Max(1, width - Reading.Indent) : width;

        foreach (var line in _marked)
        {
            var tint = Tint(line);

            foreach (var safe in Safe.Lines(line.Text).ReplaceLineEndings("\n").Split('\n'))
            {
                var pieces = Wrap(safe, room);

                for (var piece = 0; piece < pieces.Count; piece++)
                {
                    wrapped.Add(piece == 0
                        ? pieces[piece]
                        : new string(' ', Reading.Indent) + pieces[piece]);

                    tints.Add(tint);
                }
            }
        }

        // The rows and their colours together, under the list's guard. This one is reachable from
        // two threads by two routes rather than one: `Show` arrives from a read that finished on
        // the pool, and `OnViewportChanged` arrives from the draw itself.
        // Where the reader left it, unless following is carrying them to the end.
        var selected = _followEnd && wrapped.Count > 0 ? wrapped.Count - 1 : _lines.SelectedItem;

        _lines.Replace(wrapped, selected, also: () => _tints = tints);
    }

    /// <summary>What colour a line is drawn in.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The colour, or null for one drawn in the terminal's own.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the one place an agent's own colour is worth spending.</b> <see cref="Palette"/>
    /// says what the colour is for — helping an eye follow one agent down a long conversation — and
    /// this is the long conversation. The slot is the shared derivation's, so an agent is the same
    /// colour in a browser and in a terminal.
    /// </para>
    /// <para>
    /// Body text is left alone. Colouring it would tint eight thousand characters of somebody
    /// else's words in a hue this surface chose, which is the opposite of the header standing out.
    /// </para>
    /// </remarks>
    private static Color? Tint(TranscriptLine line) => line.Kind switch
    {
        LineKind.Header => line.Colour is { } slot ? Colours.Slot(slot) : Colours.Transcript,
        LineKind.Annotation => Colours.Warn,
        LineKind.Point => Colours.Transcript,
        LineKind.DoneWhen => Colours.Quiet,
        _ => null,
    };
}
