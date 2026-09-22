using Parley.Client.Seating;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The one line a person speaks from, and what it says Enter will do.
/// </summary>
/// <remarks>
/// <para>
/// <b>One box, and the protocol has four acts.</b> Which one a line becomes is derived per recipient from who
/// owes whom (<see cref="Pairing"/>) rather than chosen before the words: a person knows what they want to say,
/// and Request, Clarify and Result are bookkeeping about turns. The preview above the line is what makes that
/// safe — it names every recipient and what each will get, so nothing is recorded that was not shown.
/// </para>
/// <para>
/// <b>And it names the cost.</b> SPEC.md §5.5 makes a plural ask N exchanges, each owing a Result, and until now
/// this surface let somebody find that out in `parley flags` the next day. Counted in exchanges rather than
/// messages because obligations are what a person is deciding about.
/// </para>
/// <para>
/// <b>The seat is read at the send and never captured here.</b> 0.18.9 found three call sites that took the seat
/// when a form opened and went on speaking under a name the book had released; the rule since is that the send
/// asks.
/// </para>
/// <para>
/// <b>The completion list is drawn between the preview and the box, and the box keeps focus while it shows.</b>
/// That is FR-032, and it is what makes Enter answerable without looking at the screen: what Enter does is decided
/// by what is active, and the list is never active. Up and down move its selection, Tab completes it, Enter sends
/// — which is the owner's own description of it, and the reason a wrong guess was corrected twice while this was
/// being specified.
/// </para>
/// </remarks>
internal sealed class LineRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Say something";

    /// <summary>What the preview says where there is nobody to send to.</summary>
    internal const string Nobody = "nobody is in this conversation yet";

    /// <summary>What the preview says before a seat has been taken.</summary>
    internal const string NoSeat = "take a seat to say something here";

    /// <summary>What the preview says with an empty line.</summary>
    internal const string Empty = "type to say something";

    /// <summary>
    /// The most rows this region takes, however much there is to say.
    /// </summary>
    /// <remarks>
    /// A floor for the transcript rather than a ceiling for the preview: the transcript is what somebody opened a
    /// conversation to read, and a line addressing nineteen agents must not push it off the screen. Past this the
    /// preview scrolls within its own rows.
    /// </remarks>
    internal const int MostRows = 12;

    /// <summary>The fewest rows this region takes: a border, the preview's first row, and the box.</summary>
    internal const int LeastRows = 4;

    private readonly TextField _field;
    private readonly Plain _preview;
    private readonly GuardedList _list;

    private IReadOnlyList<string> _offering = [];
    private int _rows = LeastRows;

    internal LineRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));

        _preview = new Plain
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            Text = Empty,
        };

        // Between the preview and the box, because that is where what it is completing is: a list below the box
        // would put the thing being changed above the thing describing it.
        _list = Lists.Filling(Colours.Conversations);
        _list.X = 1;
        _list.Y = Pos.AnchorEnd(2);
        _list.Width = Dim.Fill(2);
        _list.Height = 1;
        _list.Visible = false;
        _list.CanFocus = false;

        // The line sits against the bottom so the preview above it grows upward as recipients are added,
        // rather than pushing the thing somebody is typing off its own region.
        _field = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
        };

        // **Enter reaches the field before any handler on this region**, which is how the first version of
        // the conversations list quietly did nothing. The field's own accept is the one thing that runs, and
        // it is the primary action of the active control (FR-031): in the box, that is sending.
        _field.Accepting += (_, args) =>
        {
            args.Handled = true;
            Sending?.Invoke(this, EventArgs.Empty);
        };

        // The preview is a function of what is written, so it is recomputed as it is written. Held instead, it
        // would describe a draft somebody had already edited -- and the whole promise of the preview is that
        // what it names is what Enter does.
        _field.TextChanged += (_, _) => Typing?.Invoke(this, EventArgs.Empty);

        Add(_preview);
        Add(_list);
        Add(_field);
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
    /// Read from the field rather than held beside it, so there is one copy of what somebody typed. Two would
    /// eventually disagree, and the one that is wrong would be the one that got sent.
    /// </remarks>
    internal string Written
    {
        get => _field.Text;

        set
        {
            _field.Text = value;

            // **And the cursor goes to the end of it.** Setting the text leaves the cursor where it was, so a
            // completion that grew `@ord` into `@orders@test` left it at the fourth character — and the next
            // thing typed landed inside the name. The message went out as "@ordthe build is greeners@test",
            // with the chip no longer written the way it was recorded and so no longer taken back out.
            _field.MoveEnd();
        }
    }

    /// <summary>Whether the box has focus, so a printable key is somebody writing rather than an action.</summary>
    /// <remarks>
    /// <b>Every action on this surface is a letter, which makes this the whole of a text field's safety.</b> The
    /// shell takes a region's action key in its own key handler, so without this a person typing a question mark
    /// into an ask would get the actions menu and a person typing <c>x</c> would clear the message line. `Lists`
    /// records the same collision from the other side: a list's type-to-find was eating every letter the status
    /// line advertised.
    /// </remarks>
    internal bool Editing => _field.HasFocus;

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

        _list.Visible = _offering.Count > 0;
        _list.Height = Math.Max(_offering.Count, 1);

        // **Null rather than nought on an empty list**, which the toolkit refuses outright: a selection of zero
        // rows is not row zero. Closing the list is the commonest thing that happens to it, so this threw on
        // the first keystroke of every test in the file.
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
    /// <b>No seat and no draft yet are different facts, and were one for an afternoon.</b> A single nullable
    /// draft made them the same, so a seated person whose conversation had not been read yet was told to take
    /// a seat they already had. The same class of mistake as reporting "none waiting" for "could not ask".
    /// </para>
    /// <para>
    /// The note is where a near miss is said (FR-033). It goes above the plan rather than into the message line
    /// because it is about what is written now and stops being true on the next keystroke, while the message line
    /// holds a record of what happened and is cleared only deliberately.
    /// </para>
    /// </remarks>
    internal void Show(Draft? draft, bool seated, string? note = null)
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
            var plan = draft.Plan();

            lines.Add("to: " + Safe.Text(string.Join(", ", plan.Select(a => a.Peer.Value))));

            foreach (var addressed in plan)
            {
                lines.Add("  " + Safe.Text(addressed.Peer.Value) + "  " + Says(addressed));
            }

            lines.Add(Counted(draft.Costs()));
        }

        _preview.Text = Reports.Sentences(lines);

        Measure();
    }

    /// <summary>Asks for the rows this region now needs, and says so only when the number changed.</summary>
    /// <remarks>
    /// Only on a change, because every keystroke calls this and a layout pass per keystroke is a surface that
    /// stutters under somebody typing.
    /// </remarks>
    private void Measure()
    {
        var preview = _preview.Text.Split('\n').Length;
        var wanted = Math.Clamp(3 + preview + _offering.Count, LeastRows, MostRows);

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

    /// <summary>What one recipient will get, and how to take the other side where there is one.</summary>
    /// <param name="addressed">The recipient and its act.</param>
    /// <returns>The clause.</returns>
    /// <remarks>
    /// The alternative is named only where it applies. An action advertised where it would do nothing is the
    /// same fault as a key named on a status line and bound to nothing.
    /// </remarks>
    private static string Says(Addressed addressed) => addressed.Act switch
    {
        Act.Answer => $"answer — closes the exchange ({Shell.OtherKey}: ask back instead)",
        Act.AskBack => $"asks them back — the turn goes to them ({Shell.OtherKey}: answer instead)",
        Act.Clarify => "clarifies your open request",
        _ => "new request — they will owe a result",
    };

    /// <summary>The cost of a send, in the units the protocol counts.</summary>
    /// <param name="cost">What the draft would do.</param>
    /// <returns>The sentence.</returns>
    private static string Counted(Cost cost)
    {
        var parts = new List<string>();

        if (cost.Opens > 0)
        {
            parts.Add($"{cost.Opens} exchange{(cost.Opens == 1 ? string.Empty : "s")} opened, each owing a result");
        }

        if (cost.Closes > 0)
        {
            parts.Add($"{cost.Closes} closed");
        }

        if (cost.HandsBack > 0)
        {
            parts.Add($"{cost.HandsBack} handed back");
        }

        return parts.Count == 0 ? Empty : string.Join(" · ", parts);
    }
}
