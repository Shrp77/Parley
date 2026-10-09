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

    /// <summary>What the region is called while it is showing headers only.</summary>
    /// <remarks>
    /// **On the frame rather than left to the rows.** A transcript with its bodies hidden looks like a
    /// transcript of very short messages, and a reader who does not know a fold is on reads missing words as
    /// missing messages. The title is on screen whatever the width and survives colour being stripped.
    /// </remarks>
    private const string FoldedTitle = "Transcript (folded — Enter opens one)";

    private readonly GuardedList _lines = Lists.Filling(Colours.Transcript);

    // Which messages are open while the transcript is folded. Identifiers rather than row numbers, because
    // the rows are rebuilt on every read and a remembered row would open whatever had moved into it.
    private readonly HashSet<long> _opened = [];

    private string _written = Nothing;
    private IReadOnlyList<TranscriptLine> _marked = [];
    private List<Color?> _tints = [];

    // Which message each drawn row belongs to, in step with the rows themselves. Swapped with them under the
    // list's own guard, for the reason the colours are: a parallel array kept separately is one more thing
    // that can fall out of step with what is on screen, and this one decides where a reader is sent.
    private List<long?> _owners = [];

    // What is actually on screen, which a fold makes different from what is written. Swapped with the rows
    // under the list's own guard, like the colours and the owners beside it.
    private List<string> _drawn = [];

    // Whether the reader is at the end and so is carried to each new end. **Remembered, never re-derived from the
    // screen's shape** (the owner's report of 2026-10-04: at the bottom, they replied, and nothing that followed was
    // shown). Writing a reply grows the box, which takes rows from this region, and the redraw that resize causes put
    // the view back at the top -- after which every read judged a reader who had not moved to be reading back. Only
    // the reader leaves the end: moving the cursor up it, or scrolling up it. A resize is not the reader.
    private bool _tailing = true;

    // The first row on screen where the reader left it, for a reader who is not following the end. Kept here
    // because the list cannot be trusted to keep it: in Terminal.Gui 2.4.17 a list whose frame changes has its
    // scrollbar reset the view to the first row (`ScrollBar.ShowHide`), so a reader halfway up a conversation was
    // thrown to its start whenever the box grew.
    private int _top;

    // Set while this region lays out its list, when every move of the view is the toolkit's and not the reader's.
    private bool _laying;

    // Set while this region moves the list itself, so its own moves are not taken for the reader's.
    private bool _moving;

    private bool _folded;

    internal TranscriptRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Transcript));
        // **It shows where you are in a conversation**, which the owner asked for beside the box's own: a
        // transcript scrolls and nothing else on this screen said how much of it was above or below.
        _lines.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        Add(_lines);
        _lines.ValueChanged += (_, _) => Moved();
        _lines.ViewportChanged += (_, args) => Scrolled(args);
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
        // **Opened at the end, and read again at the end when the reader was there** (the owner's call of 2026-10-01: a
        // conversation taking place should be tailed). A conversation is read for what has just been said, so the newest
        // message is where somebody arriving belongs. The same conversation read again — after a send, say — keeps a
        // reader who scrolled up where they are, by the rule `Follow` keeps.
        var arriving = !string.Equals(Open, conversationId, StringComparison.Ordinal);

        Open = problem is null ? conversationId : null;
        Retitle();

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

        if (arriving)
        {
            _tailing = true;
        }

        Mark(Laid(conversationId, project, export));
    }

    /// <summary>Whether what is on screen says the reader is at the end, judged when the reader has just scrolled.</summary>
    /// <returns><see langword="true"/> on the last row, or on no row at all.</returns>
    /// <remarks>
    /// <b>No row counts as the end since 2026-10-01</b>, which reverses 0.25.7. That release left a reader who had never
    /// moved the cursor at the top, on the grounds that nothing said they were waiting at the bottom. The owner's answer
    /// is that a conversation taking place is what somebody is waiting on, so the screen tails it. Only a reader who
    /// moved the cursor somewhere above the end has said they are reading back, and only they are left alone.
    /// </remarks>
    private bool Judged()
    {
        var rows = _lines.Source?.Count ?? 0;

        if (rows == 0)
        {
            return true;
        }

        // **What is on screen decides, not where the cursor is** (the owner's nuance of 2026-10-01: at the bottom, tail;
        // anywhere else, do not even move the reader). The wheel scrolls the view and leaves the cursor behind, so a
        // reader who wheeled back to the start still had the cursor on the last row and was carried away from what they
        // were reading. The cursor is the answer only before a layout has given the list a height to judge by.
        //
        // **And a cursor somebody put on a row they can see is that reader's place**, so the view being at the bottom is
        // not enough on its own: a conversation that fits on one screen is always at the bottom, and a reader who moved
        // the cursor up it is reading back just as much as one who scrolled. A cursor scrolled out of sight above says
        // nothing, because the reader has left it behind.
        var view = _lines.Viewport;

        if (view.Height <= 0)
        {
            return _lines.SelectedItem is not { } last || last >= rows - 1;
        }

        var reading = _lines.SelectedItem is { } at && at < rows - 1 && at >= view.Y;

        return view.Y + view.Height >= rows && !reading;
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
    /// same row afterwards — and a reader at the end, who is reading as it arrives, is carried to the new
    /// end. **Since 2026-10-01 that includes a reader on no row**, because a transcript opens at the end and
    /// tails what is said (<see cref="Tailing"/>); 0.25.7 left them at the top, and the owner asked for the
    /// opposite.
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

        Mark(lines);

        return true;
    }

    /// <summary>Whether the reader is carried to the end, for a test to read.</summary>
    internal bool Tailing => _tailing;

    /// <summary>The reader moved the cursor: at the last row they are following again, anywhere else they are not.</summary>
    private void Moved()
    {
        if (_moving)
        {
            return;
        }

        var rows = _lines.Source?.Count ?? 0;

        _tailing = _lines.SelectedItem is not { } at || at >= rows - 1;
    }

    /// <summary>The list's view moved, which outside a layout and outside this region's own redraw is the reader.</summary>
    /// <param name="args">The view before and after.</param>
    private void Scrolled(DrawEventArgs args)
    {
        if (_moving || _laying)
        {
            return;
        }

        _top = args.NewViewport.Y;
        _tailing = Judged();
    }

    /// <inheritdoc/>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        _laying = true;
        base.OnSubViewLayout(args);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The reader is put back where they were after every layout</b>, because a layout is the box growing or the
    /// terminal resizing and neither is the reader moving. A reader following the end is shown the end; anyone else is
    /// shown the row they had at the top, held inside the rows there are.
    /// </remarks>
    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);
        _laying = false;
        Place();
    }

    /// <summary>Puts the view where the reader is: at the end when following it, at their own row when not.</summary>
    private void Place()
    {
        if (_tailing)
        {
            ToTheEnd();

            return;
        }

        if (_lines.Viewport is not { Height: > 0 } view)
        {
            return;
        }

        var rows = _lines.Source?.Count ?? 0;
        var y = Math.Clamp(_top, 0, Math.Max(0, rows - view.Height));

        if (view.Y == y)
        {
            return;
        }

        _moving = true;

        try
        {
            _lines.Viewport = view with { Y = y };
        }
        finally
        {
            _moving = false;
        }
    }

    /// <summary>Shows the last rows, without touching the cursor.</summary>
    private void ToTheEnd()
    {
        var rows = _lines.Source?.Count ?? 0;

        if (_lines.Viewport is not { Height: > 0 } view)
        {
            return;
        }

        var end = Math.Max(0, rows - view.Height);

        if (view.Y == end)
        {
            return;
        }

        _moving = true;

        try
        {
            _lines.Viewport = view with { Y = end };
        }
        finally
        {
            _moving = false;
        }
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
        .. Reading.Lines(export),
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

    /// <summary>Which message each drawn row belongs to, for a test to check where a reader was sent.</summary>
    /// <remarks>
    /// Read rather than recomputed, because a test that worked out the layout for itself would be a second
    /// opinion about it -- and the thing worth checking is that this region and the reader agree.
    /// </remarks>
    internal IReadOnlyList<long?> Rows => _owners;

    /// <summary>Whether the transcript is showing headers only.</summary>
    internal bool Folded => _folded;

    /// <summary>The rows on screen, which a fold makes fewer than what is written.</summary>
    /// <remarks>
    /// <b>Distinct from <see cref="Written"/> on purpose, and that decides what copying does.</b> Written is the
    /// whole conversation however much of it is on screen, and copying takes that: a fold is a way of looking
    /// at a transcript, not a way of shortening it, and somebody who folded it to find a message and then
    /// copied it would not expect the bodies to have gone.
    /// </remarks>
    internal IReadOnlyList<string> Drawn => _drawn;

    /// <summary>
    /// Shows headers only, or everything again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked for so the flow of a conversation can be read.</b> A broadcast to nineteen agents is nineteen
    /// pairwise messages (SPEC.md §5.0) carrying one person's sentence, so the shape of an exchange — who
    /// spoke, to whom, in what order — is buried under its own content on exactly the conversations where it
    /// matters most.
    /// </para>
    /// <para>
    /// Individual messages stay open across a fold and an unfold, because a person who opened two of them to
    /// compare has said something about those two that closing the fold should not throw away.
    /// </para>
    /// </remarks>
    internal void Fold()
    {
        _folded = !_folded;
        Retitle();

        Redraw();

        // **Folding puts the cursor on the first message when it is nowhere.** A reader who has not moved is
        // deliberately on no row (`FollowingTests`), and folding is a deliberate act that says the next thing
        // wanted is a message -- so Enter would otherwise do nothing on the keystroke straight after it.
        //
        // **The first row that is a message, which is not row zero.** A transcript opens with its own
        // preamble -- the conversation, the project, the zone the times are in -- and those rows belong to no
        // message, so landing on one would be a cursor sitting where Enter does nothing.
        if (_folded && _lines.SelectedItem is null && _owners.FindIndex(owner => owner is not null) is >= 0 and var first)
        {
            _lines.SelectedItem = first;
        }
    }

    /// <summary>Expands or re-folds one message while the transcript is folded.</summary>
    /// <param name="message">Which message.</param>
    /// <returns><see langword="true"/> when the fold is on and something changed.</returns>
    /// <remarks>
    /// False when nothing is folded, so the caller can leave Enter meaning whatever it means unfolded rather
    /// than swallowing it to no effect.
    /// </remarks>
    internal bool Expand(long message)
    {
        if (!_folded)
        {
            return false;
        }

        if (!_opened.Remove(message))
        {
            _opened.Add(message);
        }

        Redraw();

        return true;
    }

    /// <summary>One message as it reads on screen, for somebody taking a copy of it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>Its header and its body, one line each and without the indent, or null when it is not here.</returns>
    /// <remarks>
    /// <para>
    /// <b>The whole message whether or not it is folded</b>, because a fold is a way of looking and not a shortening —
    /// the same reason copying the conversation takes <see cref="Written"/> rather than what is drawn.
    /// </para>
    /// <para>
    /// <b>The indent is taken off</b>, because it is this screen's structure rather than anybody's words: it is what keeps a
    /// payload from passing for a header here, and pasted anywhere else it is four spaces in front of every line.
    /// </para>
    /// </remarks>
    internal string? MessageText(long message)
    {
        var lines = _marked.Where(line => line.Message == message).ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var indent = new string(' ', Reading.Indent);

        var rows = lines
            .SelectMany(line => line.Text.ReplaceLineEndings("\n").Split('\n'))
            .Select(row => row.StartsWith(indent, StringComparison.Ordinal) ? row[indent.Length..] : row);

        return Safe.Lines(string.Join(Environment.NewLine, rows)).TrimEnd();
    }

    /// <summary>The message the cursor is on, or null when it is on none.</summary>
    internal long? Under =>
        _lines.SelectedItem is int at && at >= 0 && at < _owners.Count ? _owners[at] : null;

    /// <summary>Puts one message in front of the reader, with what came before it still visible.</summary>
    /// <param name="message">The message to reach.</param>
    /// <returns><see langword="true"/> when it was on screen to reach.</returns>
    /// <remarks>
    /// <para>
    /// <b>The room's "waiting on you" list activates this</b>, because a question is read in its context
    /// rather than in a list -- which is the whole reason that column names messages instead of restating
    /// them.
    /// </para>
    /// <para>
    /// <b>Two selections rather than one, and that is how the neighbours are guaranteed.</b> A list scrolls
    /// its selection into view, so a single jump lands the row against whichever edge it came from -- the
    /// message at the very top of the screen with nothing above it. Selecting a couple of rows earlier first
    /// makes those rows the ones scrolled to, and the message then sits just below them. No viewport is
    /// touched: this is the list's own behaviour, which is the part a headless test can hold.
    /// </para>
    /// <para>
    /// False rather than a guess when the message is not among the rows. A transcript that had been refilled
    /// since the column was drawn is an ordinary race, and scrolling somewhere arbitrary would answer "where
    /// was this said" with somewhere nothing was.
    /// </para>
    /// </remarks>
    internal bool Reach(long message)
    {
        // One snapshot of the reference, not the lock. `GuardedList` keeps its guard private on purpose --
        // handing it out would let a caller take it for the rows and not the colours -- and it does not need
        // handing out here: the list is replaced wholesale, so this read sees the rows as they were or as they
        // now are, and never a half-swapped pair.
        var at = _owners.IndexOf(message);

        if (at < 0)
        {
            return false;
        }

        _lines.SelectedItem = Math.Max(0, at - Neighbours);
        _lines.SelectedItem = at;

        return true;
    }

    /// <summary>How many rows above a reached message are put on screen with it.</summary>
    /// <remarks>
    /// Two: enough that a message does not arrive against the top edge looking like the start of the
    /// conversation, and few enough that the message itself is what somebody is looking at.
    /// </remarks>
    internal const int Neighbours = 2;

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
        Retitle();
        Say(Nothing, LineKind.Annotation);
    }

    /// <summary>Names the conversation in the box's title, and says when the box is folded.</summary>
    /// <remarks>
    /// The owner's call of 2026-10-09: the name of the conversation belongs in the title, since the status line is the only
    /// other place it is, and a person reading a long transcript has lost sight of it by then.
    /// </remarks>
    private void Retitle()
    {
        var named = Open is { } open ? $"{RegionTitle} — {Safe.Text(open)}" : RegionTitle;

        Title = _folded ? FoldedTitle.Replace(RegionTitle, named, StringComparison.Ordinal) : named;
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
        var owners = new List<long?>();
        var room = width > 0 ? Math.Max(1, width - Reading.Indent) : width;

        foreach (var line in _marked.Where(Shown))
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
                    owners.Add(line.Message);
                }
            }
        }

        // The rows and their colours together, under the list's guard. This one is reachable from
        // two threads by two routes rather than one: `Show` arrives from a read that finished on
        // the pool, and `OnViewportChanged` arrives from the draw itself.
        // Where the reader left it, unless following is carrying them to the end.
        //
        // **Clamped, because the rows can get fewer.** Folding is the first thing that shrinks a transcript
        // on purpose, and the toolkit refuses a selection past the end outright rather than clamping it
        // itself -- so without this, folding a conversation whose cursor was near the bottom threw.
        // **No selection stays no selection, which the first version of this clamp threw away.** A reader who
        // has never moved the cursor is left at the top and is not on a row; turning that into row zero makes
        // the surface claim they are reading something, and `FollowingTests` says so in as many words.
        int? selected = _tailing && wrapped.Count > 0
            ? wrapped.Count - 1
            : _lines.SelectedItem is { } at && wrapped.Count > 0
                ? Math.Clamp(at, 0, wrapped.Count - 1)
                : null;

        _moving = true;

        try
        {
            _lines.Replace(wrapped, selected, also: () =>
            {
                _tints = tints;
                _owners = owners;
                _drawn = wrapped;
            });

        }
        finally
        {
            _moving = false;
        }

        // **And a reader who is not being carried is not moved at all.** Setting the selection scrolls it into view, so a
        // reader who wheeled up past a cursor left on the last row was sent back down by the redraw itself. The view is
        // put back where they had it, held inside the rows that now exist; a reader following the end is shown it.
        Place();
    }

    /// <summary>Whether a line is drawn as things stand.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when it belongs on screen.</returns>
    /// <remarks>
    /// Folded, a message is its header until somebody opens it. The blank line between messages goes with the
    /// bodies: a fold exists to fit the shape of a conversation on one screen, and spacing every other row
    /// would give half of that back.
    /// </remarks>
    private bool Shown(TranscriptLine line) =>
        !_folded
        || line.Kind == LineKind.Header
        || (line.Message is { } message && _opened.Contains(message));

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
