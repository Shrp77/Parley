using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

// **CS0618 is suppressed here, in one file, for one type.** Terminal.Gui 2.4.17 marks `TextView`
// superseded by the `EditorView` of a separate `tui-cs/Editor` package. It is the only multi-line
// editor in the toolkit this repository already depends on, and it works; taking the replacement
// means a new package, and `Directory.Packages.props` records at length what this toolkit's
// twenty-four already cost. That is the owner's decision to take, not one to make by accident
// while fixing a field.
//
// The suppression is a file of its own so it covers exactly the class that needs it. A
// project-wide `NoWarn` would silence the next deprecation as well, which is the failure mode the
// build's warnings-as-errors rule exists to prevent.
#pragma warning disable CS0618 // Type or member is obsolete

/// <summary>
/// The thing somebody types into: one line, or several.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two toolkit views behind one shape, rather than one view doing both jobs.</b> A
/// <see cref="TextField"/> is a line: Enter in it takes the form's default button, which is what
/// every screen built on <see cref="FormDialog"/> has always done and what a name, a path or a
/// project is. A <see cref="TextView"/> is a paragraph: Enter in it is a line break, so a form
/// holding one is finished from the button rather than from the field.
/// </para>
/// <para>
/// <b>The owner asked for this against the composer.</b> A request's text may be eight
/// thousand characters — SPEC.md rule 8.4's cap, and the one this surface is most used for — and
/// it was collected in a field one row tall, which shows about sixty of them at a time. Somebody
/// writing a paragraph could not see the paragraph.
/// </para>
/// <para>
/// <b>Which fields are long is declared rather than guessed.</b> Making every field several rows
/// deep would take Enter away from the forms that name things, where it is the whole interaction,
/// and would put a line break into a project name — a value that has to survive being written into
/// a manifest and read back as one token.
/// </para>
/// </remarks>
internal sealed class Entry
{
    private readonly TextField? _line;
    private readonly TextView? _paragraph;

    private Entry(TextField line)
    {
        _line = line;

        line.TextChanged += (_, _) => Changed?.Invoke();
    }

    private Entry(TextView paragraph)
    {
        _paragraph = paragraph;

        paragraph.ContentsChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>Raised when what is in it has changed.</summary>
    internal event Action? Changed;

    /// <summary>Builds the field a form field asks for.</summary>
    /// <param name="field">What is being asked for.</param>
    /// <returns>The field.</returns>
    internal static Entry For(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var opening = field.Correct is { } correct ? correct(field.Proposal) : field.Proposal;

        if (!field.Long)
        {
            return new Entry(new TextField { X = 1, Width = Dim.Fill(2), Text = opening });
        }

        var paragraph = new TextView
        {
            X = 1,
            Width = Dim.Fill(2),

            // Multiline first: the toolkit refuses word wrap on a single-line view, and
            // `EnterKeyAddsLine` sets multiline itself, so the order these are given in is the
            // difference between a wrapping field and one that scrolls sideways.
            Multiline = true,
            WordWrap = true,
            EnterKeyAddsLine = true,

            // Tab still leaves the field. A form is several fields and a button, and a field that
            // swallowed Tab would be one somebody could not get out of without a mouse.
            TabKeyAddsTab = false,
            Text = opening,
        };

        return new Entry(paragraph);
    }

    /// <summary>Builds the box a person speaks from in a conversation.</summary>
    /// <returns>The box.</returns>
    /// <remarks>
    /// <para>
    /// <b>A paragraph rather than a line, because a message is one.</b> The owner asked for it after writing a
    /// long one: a single-line field scrolls sideways, so what somebody had written slid out of sight a
    /// character at a time and the only way to read their own message back was to walk the cursor through it.
    /// Rule 8.4's cap is eight thousand characters and this is the box those are written in.
    /// </para>
    /// <para>
    /// <b>Here rather than in the line region, so the deprecation stays suppressed in one file.</b> That was
    /// the point of putting the suppression in a file of its own, and a second copy of it elsewhere would be
    /// the project-wide <c>NoWarn</c> this file exists to avoid, arrived at one class at a time.
    /// </para>
    /// <para>
    /// <b>Enter adds a line here and is never seen doing it.</b> The region takes Enter on this view before
    /// the binding runs and sends; what reaches the binding is the chord for a line break, forwarded as a
    /// plain Enter so the toolkit's own line handling does the work. Tab is left alone for the same reason it
    /// is in a form.
    /// </para>
    /// <para>
    /// <b>It shows where you are once there is more than fits</b>, which the owner asked for by name: a box
    /// that grows to a bound and then scrolls is a box that has hidden something, and a reader with no
    /// scrollbar cannot tell that from having written less than they thought.
    /// </para>
    /// </remarks>
    internal static Entry Speaking()
    {
        var box = new TextView
        {
            X = 1,
            Width = Dim.Fill(2),

            // Multiline first: the toolkit refuses word wrap on a single-line view.
            Multiline = true,
            WordWrap = true,
            EnterKeyAddsLine = true,
            TabKeyAddsTab = false,
        };

        box.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;

        return new Entry(box);
    }

    /// <summary>The view itself.</summary>
    internal View View => (View?)_line ?? _paragraph!;

    /// <summary>How many rows the text needs at the width it has, however many that is.</summary>
    /// <remarks>
    /// <see cref="Rows"/>'s measurement without a form's bounds on it. A conversation's box is given what it
    /// asks for up to whatever leaves the transcript standing, and that decision belongs to the surface that
    /// knows how tall the screen is rather than to a constant here.
    /// </remarks>
    internal int Wants => _paragraph is null ? 1 : Math.Max(1, Deep(_paragraph));

    /// <summary>Puts the cursor after everything written.</summary>
    internal void MoveEnd()
    {
        _line?.MoveEnd();
        _paragraph?.MoveEnd();
    }

    /// <summary>Adds a line break where the cursor is.</summary>
    /// <remarks>
    /// Forwarded as a plain Enter rather than an inserted character, so the toolkit does whatever it does
    /// about wrapping, history and the cursor. A newline written straight into the text would be this
    /// repository deciding what a line break means inside somebody else's editor.
    /// </remarks>
    internal void Break() => _paragraph?.NewKeyDownEvent(Key.Enter);

    /// <summary>The one-line field, or null when this is a paragraph.</summary>
    /// <remarks>
    /// Handed out for corrections alone, which apply to names and never to prose: a rule that
    /// lowercases what is typed has nothing to say about a sentence.
    /// </remarks>
    internal TextField? Short => _line;

    /// <summary>What is in it.</summary>
    internal string Text
    {
        get => _line?.Text ?? _paragraph!.Text;

        set
        {
            if (_line is not null)
            {
                _line.Text = value;
            }
            else
            {
                _paragraph!.Text = value;
            }
        }
    }

    /// <summary>How many rows it wants right now.</summary>
    /// <remarks>
    /// <para>
    /// Measured from the text rather than asked of the toolkit, because the answer is needed while
    /// deciding the layout that would give the toolkit something to measure. The width used is the
    /// field's own once it has been laid out and <see cref="Prompting.Rows"/>'s estimate before
    /// that — the same estimate the labels above it are sized with, and self-correcting on the
    /// first real layout.
    /// </para>
    /// <para>
    /// It counts the row the cursor would sit on after a trailing line break, so pressing Enter at
    /// the end of the text opens the row it puts the cursor on rather than one keystroke later.
    /// </para>
    /// </remarks>
    internal int Rows
    {
        get
        {
            if (_paragraph is null)
            {
                return 1;
            }

            return Math.Clamp(Deep(_paragraph), FormDialog.Opening, FormDialog.Deepest);
        }
    }

    /// <summary>How many rows a paragraph's text takes at the width it has.</summary>
    /// <param name="paragraph">The view.</param>
    /// <returns>The rows, at least one.</returns>
    /// <remarks>
    /// Measured from the text rather than asked of the toolkit, because the answer is needed while deciding
    /// the layout that would give the toolkit something to measure. The width used is the view's own once it
    /// has been laid out and an estimate before that, self-correcting on the first real layout.
    /// </remarks>
    private static int Deep(TextView paragraph)
    {
        var width = paragraph.Viewport.Width > 0 ? paragraph.Viewport.Width : 60;
        var rows = 0;

        foreach (var line in paragraph.Text.Split('\n'))
        {
            rows += Math.Max(1, (int)Math.Ceiling(line.TrimEnd('\r').Length / (double)width));
        }

        return Math.Max(1, rows);
    }
}

#pragma warning restore CS0618
