using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Top;

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
/// <b>The owner asked for this against the composer.</b> A notification's text may be eight
/// thousand characters — SPEC.md rule 8.5's cap, and the one this surface is most used for — and
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

    /// <summary>The view itself.</summary>
    internal View View => (View?)_line ?? _paragraph!;

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

            var width = _paragraph.Viewport.Width > 0 ? _paragraph.Viewport.Width : 60;
            var rows = 0;

            foreach (var line in _paragraph.Text.Split('\n'))
            {
                rows += Math.Max(1, (int)Math.Ceiling(line.TrimEnd('\r').Length / (double)width));
            }

            return Math.Clamp(rows, FormDialog.Opening, FormDialog.Deepest);
        }
    }
}

#pragma warning restore CS0618
