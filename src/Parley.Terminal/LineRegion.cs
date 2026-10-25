using Parley.Client.Seating;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The one line a person speaks from, and what it says Enter will do.
/// </summary>
/// <remarks>
/// <para>
/// <b>One box, and every line is a new Request</b> (SPEC.md §5.6). Nothing is derived from whose move it is: an
/// answer to one particular question is a Clarify the person asks for by picking that question. The preview above
/// the line names every recipient, so nothing is recorded that was not shown.
/// </para>
/// <para>
/// <b>And it names the cost.</b> SPEC.md §5.5 makes a plural ask N exchanges, each owing a Result.
/// Counted in exchanges rather than messages because obligations are what a person is deciding about.
/// </para>
/// <para>
/// <b>The seat is read at the send and never captured here.</b> A seat taken when a form opens could
/// go on speaking under a name the book had released, so the send asks.
/// </para>
/// <para>
/// <b>The completion list is drawn between the preview and the box, and the box keeps focus while it shows.</b>
/// That is what makes Enter answerable without looking at the screen: what Enter does is decided
/// by what is active, and the list is never active. Up and down move its selection, Tab completes it, Enter sends.
/// </para>
/// </remarks>
internal sealed class LineRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Say something";

    /// <summary>What the preview says where there is nobody to send to.</summary>
    internal const string Nobody = "nobody is in this conversation yet";

    /// <summary>What the preview says while this terminal is not connected to the project as the person.</summary>
    /// <remarks>Not "take a seat": there is no seat to take, only a connection coming back.</remarks>
    internal const string NoSeat = "you are not connected to this project";

    /// <summary>What the preview says with an empty line.</summary>
    internal const string Empty = "type to say something";

    /// <summary>
    /// The fewest rows the transcript keeps, however long the message being written is.
    /// </summary>
    /// <remarks>
    /// <b>A floor for the transcript rather than a ceiling for the box.</b> The box grows
    /// so a whole message can be read back, and rule 8.4's cap is eight thousand characters — which is more
    /// rows than any terminal has. So the box takes what it asks for until the transcript is down to this, and
    /// scrolls after that: a conversation somebody is writing into is still a conversation they are reading.
    /// </remarks>
    internal const int TranscriptFloor = 6;

    /// <summary>The fewest rows this region takes: a border, the preview's first row, and the box.</summary>
    internal const int LeastRows = 4;

    /// <summary>
    /// The most rows the box itself grows to before it scrolls.
    /// </summary>
    /// <remarks>
    /// A bound rather than none: a message may be eight thousand characters, which is
    /// more rows than any terminal has. Ten is a screenful of a paragraph with a conversation still above it.
    /// </remarks>
    internal const int MostBoxRows = 10;

    /// <summary>
    /// The most rows the preview takes before it counts the rest.
    /// </summary>
    /// <remarks>
    /// <b>Bounded so the preview cannot squeeze the box to nothing.</b> The preview names every
    /// recipient, so a large conversation would take every row and leave the box no cursor and no
    /// drawn text. The recipients are all named on the first line; what this bounds is the detail under it.
    /// </remarks>
    internal const int MostPreviewRows = 6;

    /// <summary>The character that turns the next Enter into a line break rather than a send.</summary>
    internal const string Breaking = @"\";

    private readonly Entry _field;
    private readonly Plain _preview;
    private readonly GuardedList _list;

    private IReadOnlyList<string> _offering = [];
    private int _rows = LeastRows;
    private int _wants = LeastRows;
    private int _box = -1;
    private int _listed = -1;

    internal LineRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));

        // **Stacked from the top rather than anchored to the bottom.** The box grows downward into whatever
        // the region was given, so the preview and the list keep their rows and the thing being typed takes
        // the rest — which is what lets the region ask for more rows and have them go to the message.
        _preview = new Plain
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 1,
            Text = Empty,
        };

        // Between the preview and the box, because that is where what it is completing is: a list below the box
        // would put the thing being changed above the thing describing it.
        _list = Lists.Filling(Colours.Conversations);
        _list.X = 1;
        _list.Width = Dim.Fill(2);
        _list.Height = 0;
        _list.Visible = false;
        _list.CanFocus = false;

        // **A paragraph rather than a line, and it wraps rather than scrolling sideways**, so a message
        // can be read back as it is written.
        _field = Entry.Speaking();

        // The preview is a function of what is written, so it is recomputed as it is written. Held instead, it
        // would describe a draft somebody had already edited -- and the whole promise of the preview is that
        // what it names is what Enter does.
        _field.Changed += () => Typing?.Invoke(this, EventArgs.Empty);

        // **Enter is taken on the box, because that is where it lands.**
        //
        // A focused view is offered a key before the surface's own handler is, so what reaches the shell is
        // only what this box declined: Tab arrives there because this box does not bind it, and Enter never
        // arrives at all because it does. An arm in the shell for Enter is an arm that never runs, so a test
        // must press the key itself: the `Accept()` seam raises the same event from the other side.
        //
        // `KeyDown` rather than `Accepting`, because this box adds a line on Enter and that is a binding
        // rather than an accept: the key has to be taken before the binding runs.
        _field.View.KeyDown += (_, key) =>
        {
            // **Shift+Enter is a line break, and so is Ctrl+Enter.** Taken
            // here rather than left to the shell's action, because a focused view is offered a key first and the field's
            // own Enter binding would otherwise read either chord as Enter.
            if (key == Key.Enter.WithShift || key == Key.Enter.WithCtrl)
            {
                key.Handled = true;
                Break();

                return;
            }

            // The break itself, which is the field's own Enter: let it through rather than reading it as a send.
            if (key != Key.Enter || _breaking)
            {
                return;
            }

            key.Handled = true;

            // **A trailing backslash makes it a line break instead**, which is the spelling that works in
            // every terminal. A chord is what a person reaches for, and several terminals deliver Ctrl+Enter,
            // Shift+Enter and a bare Enter as the same bytes — so the chord is offered beside it and this is
            // the one that cannot be taken away.
            if (Written.EndsWith(Breaking, StringComparison.Ordinal))
            {
                Written = Written[..^1] + "\n";

                return;
            }

            Sending?.Invoke(this, EventArgs.Empty);
        };

        Add(_preview);
        Add(_list);
        Add(_field.View);

        Place();
    }

    /// <summary>
    /// Gives the box the rows it needs, the list its own, and the preview whatever is left.
    /// </summary>
    /// <remarks>
    /// <b>The box is anchored to the bottom and sized absolutely.</b> Filling what the preview and
    /// the list had not taken would let a preview that grows with the recipients take the rows the box
    /// is drawn in. What somebody is typing into cannot be the thing that gives way.
    /// </remarks>
    private void Place()
    {
        var box = Math.Clamp(_field.Wants, 1, MostBoxRows);
        var list = _offering.Count;

        _wants = 2 + Math.Min(_preview.Text.Split('\n').Length, MostPreviewRows) + list + box;

        // **Nothing is reassigned unless a number changed**, because this runs on every keystroke and every
        // assignment here asks the toolkit to lay the region out again — with the cursor placed at the end of
        // it each time. That is what made the cursor flicker while somebody typed.
        if (box == _box && list == _listed)
        {
            return;
        }

        _box = box;
        _listed = list;

        _field.View.Y = Pos.AnchorEnd(box);
        _field.View.Height = box;

        _list.Y = Pos.AnchorEnd(box + list);
        _list.Height = list;

        _preview.Y = 0;
        _preview.Height = Dim.Fill(box + list);
    }

    /// <summary>Raised when Enter was pressed in the box.</summary>
    internal event EventHandler? Sending;

    /// <summary>Raised whenever what is written changed.</summary>
    internal event EventHandler? Typing;

    /// <summary>Raised when this region wants a different number of rows.</summary>
    /// <remarks>
    /// The heights alone, and never a rearrangement: a rearrangement ends by taking focus, and this fires while
    /// somebody is typing. That is the same reason the message line changes heights rather than arranging.
    /// </remarks>
    internal event EventHandler? Resized;

    /// <summary>What is written but not sent. Named to avoid hiding the toolkit's own Text.</summary>
    /// <remarks>
    /// <para>
    /// Read from the field rather than held beside it, so there is one copy of what somebody typed. Two would
    /// eventually disagree, and the one that is wrong would be the one that got sent.
    /// </para>
    /// <para>
    /// <b>For the loop's own thread</b>, which key handlers and dialogs run on. Work off the loop reads with
    /// <see cref="WrittenOnTheLoop"/> and changes it with <see cref="Rewrite"/>, because the toolkit's text view throws
    /// when it is read or changed while the loop draws it or takes a key (TheBoxIsWrittenOnTheLoopTests). The setter
    /// still goes through the loop, so a write from anywhere else is safe.
    /// </para>
    /// </remarks>
    internal string Written
    {
        get => _field.Text;

        set => Prompting.OnTheLoop(() => Write(value));
    }

    /// <summary>What is written, read on the thread that draws.</summary>
    /// <returns>The box's text, or empty if the loop did not reach the read within the wait.</returns>
    /// <remarks>
    /// For work off the loop. Reading the text walks the toolkit's text model, which the loop changes as the person types,
    /// so a read from the pool threw <c>Collection was modified</c> into the message line. Empty after the wait gives up,
    /// so do not decide anything that cannot be undone on an empty answer.
    /// </remarks>
    internal string WrittenOnTheLoop()
    {
        var written = string.Empty;

        Prompting.OnTheLoop(() => written = _field.Text);

        return written;
    }

    /// <summary>Changes what is written, reading and writing it in one turn of the loop.</summary>
    /// <param name="change">The new text from the text as it stands.</param>
    /// <remarks>
    /// For work off the loop. A read on the pool and a write on the loop would lose any key the person typed between the
    /// two, because the loop takes input before it runs what was handed to it. Writes only when the text changes: a
    /// driven test has no loop, so the write runs on the pool against the test's own draws (RefillRaceTests), and a box
    /// already empty is the common case there.
    /// </remarks>
    internal void Rewrite(Func<string, string> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Prompting.OnTheLoop(() =>
        {
            var now = _field.Text;
            var next = change(now);

            if (!string.Equals(now, next, StringComparison.Ordinal))
            {
                Write(next);
            }
        });
    }

    private void Write(string value)
    {
        _field.Text = value;

        // **And the cursor goes to the end of it.** Setting the text leaves the cursor where it was, so a
        // completion that grew `@ord` into `@orders@test` would leave the next thing typed inside the
        // name, and the chip would no longer be written the way it was recorded.
        _field.MoveEnd();
    }

    /// <summary>Whether the box has focus, so a printable key is somebody writing rather than an action.</summary>
    /// <remarks>
    /// <b>Every action on this surface is Alt plus a letter and menus open with F10, so a plain
    /// character is never an action.</b> This tells the shell that a printable key is somebody writing,
    /// which is the whole of a text field's safety. `Lists` records the same collision from the other
    /// side: type-to-find would eat the keys the status line advertises.
    /// </remarks>
    internal bool Editing => _field.View.HasFocus;

    /// <summary>Adds a line break where the cursor is.</summary>
    /// <remarks>
    /// <para>
    /// <b>Enter sends, so a line break needs a key of its own.</b> The chord is what a person reaches for and
    /// what the owner asked about by name; <c>\</c> then Enter is the one that works in a terminal that cannot
    /// tell a chord from a bare Enter, which several cannot.
    /// </para>
    /// <para>
    /// <b>The field makes a break by being sent Enter, and this region reads Enter as a send.</b> So the send stands
    /// aside while the break is being made, or every chord that starts a new line would send the message instead.
    /// </para>
    /// </remarks>
    internal void Break()
    {
        _breaking = true;

        try
        {
            _field.Break();
        }
        finally
        {
            _breaking = false;
        }
    }

    // Set only while `Break` hands the field its own Enter, so the send ignores that one.
    private bool _breaking;

    /// <summary>Presses Enter in the box, as a person does.</summary>
    /// <remarks>
    /// A seam for a driven test. The field takes Enter before this region sees it, so a test that called the
    /// shell's send directly would pass over the one thing worth checking -- that the key arrives at all.
    /// </remarks>
    internal void Accept() => Sending?.Invoke(this, EventArgs.Empty);

    /// <summary>What the preview is saying, for a test to read without the screen.</summary>
    internal string Preview => _preview.Text;

    /// <summary>What the completion list is showing, top to bottom. Empty when it is closed.</summary>
    internal IReadOnlyList<string> Offering => _offering;

    /// <summary>How many rows this region wants, given what it is showing.</summary>
    internal int Rows => _rows;

    /// <summary>How many rows the box itself was given once the screen was laid out.</summary>
    /// <remarks>
    /// <b>Read by a test, because the box having no rows is a state nothing else could see.</b> Every
    /// assertion on what was written can pass while nothing is on the screen.
    /// </remarks>
    internal int BoxRows => _field.View.Frame.Height;

    /// <summary>The box itself, so a test can click into it the way a person does.</summary>
    internal View Box => _field.View;

    /// <summary>How wide the box is, which is what its wrapping is measured against.</summary>
    internal int BoxWidth => _field.View.Viewport.Width;

    /// <summary>Puts a completion list on screen, or takes it off.</summary>
    /// <param name="completion">What is being completed, which may be closed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="completion"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Nothing matching closes the list rather than showing an empty one.</b> An empty list is a box saying
    /// "here are your options" over no options; a closed one says what is true, which is that this is text now.
    /// </para>
    /// <para>
    /// Each row carries what the thing is, not only its name. A command's one line is the whole reason a person
    /// can use a command they have not read the documentation for.
    /// </para>
    /// </remarks>
    internal void Offer(Completion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        _offering = completion.Open
            ? [.. completion.Matches.Select(m => Row(completion.Which, m))]
            : [];

        // **Nought rows when it is closed, not one**: a closed list that reserved a row would take one the
        // box needs. `Place` gives it exactly what it is showing.
        _list.Visible = _offering.Count > 0;

        // **Null rather than nought on an empty list**, which the toolkit refuses outright: a selection of zero
        // rows is not row zero.
        _list.Replace(_offering, _offering.Count == 0 ? null : completion.Selected);

        Measure();
    }

    /// <summary>Puts the plan for the current draft above the line.</summary>
    /// <param name="draft">The draft, or null when there is not one yet.</param>
    /// <param name="seated">Whether a seat is held.</param>
    /// <param name="note">Something to say above the plan, or null.</param>
    /// <remarks>
    /// <para>
    /// Every recipient is named rather than counted, because the case worth seeing is one line doing three
    /// different things — which a count would hide.
    /// </para>
    /// <para>
    /// <b>No seat and no draft yet are different facts.</b> A seated person whose conversation has not been
    /// read yet must not be told to take a seat they already have; compare reporting "none waiting" for
    /// "could not ask".
    /// </para>
    /// <para>
    /// The note is where a near miss is said (FR-033). It goes above the plan rather than into the message line
    /// because it is about what is written now and stops being true on the next keystroke, while the message line
    /// holds a record of what happened and is cleared only deliberately.
    /// </para>
    /// <para>
    /// <b>On the thread that draws.</b> Shown after a read, from the work queue, and measuring the region reads the
    /// box's text, which threw while the person typed (TheBoxIsWrittenOnTheLoopTests).
    /// </para>
    /// </remarks>
    internal void Show(Draft? draft, bool seated, string? note = null) =>
        Prompting.OnTheLoop(() => ShowHere(draft, seated, note));

    private void ShowHere(Draft? draft, bool seated, string? note)
    {
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(note))
        {
            lines.Add(Safe.Text(note));
        }

        if (!seated)
        {
            lines.Add(NoSeat);
        }
        else if (draft is null)
        {
            lines.Add(Empty);
        }
        else if (draft.Recipients.Count == 0)
        {
            lines.Add(Nobody);
        }
        else
        {
            // **Who it goes to, and nothing else.** A conversation never really stops, so a line that says what
            // Enter closes would describe bookkeeping as if it were the conversation.
            lines.Add("to: " + Safe.Text(string.Join(", ", draft.Recipients.Select(peer => peer.Value))));
        }

        // **One line each, with nothing added.** `Reports.Sentences` closes each part with a full stop, which is
        // right for a report and wrong after an agent's name. Each part is already neutralised.
        _preview.Text = string.Join('\n', lines);

        Measure();
    }

    /// <summary>Asks for the rows this region now needs, and says so only when the number changed.</summary>
    /// <remarks>
    /// Only on a change, because every keystroke calls this and a layout pass per keystroke is a surface that
    /// stutters under somebody typing.
    /// </remarks>
    private void Measure()
    {
        Place();

        var wanted = Math.Max(LeastRows, _wants);

        if (wanted == _rows)
        {
            return;
        }

        _rows = wanted;
        Resized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One row of the completion list.</summary>
    /// <param name="which">Which sigil is being completed.</param>
    /// <param name="match">The candidate.</param>
    /// <returns>What the row says.</returns>
    private static string Row(Sigil which, string match) => which == Sigil.Command
        ? "/" + match + Beside(match)
        : "@" + Safe.Text(match);

    /// <summary>What a command says about itself, beside its identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The clause, or nothing for a command that is not in the table.</returns>
    private static string Beside(string id) =>
        Commands.Table.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is { } found
            ? "  " + found.Says
            : string.Empty;
}
