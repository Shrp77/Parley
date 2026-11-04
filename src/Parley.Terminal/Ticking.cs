using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>One row a person may tick.</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Note">
/// Anything about it worth knowing before ticking — that it is a worktree, that it already answers
/// somewhere. Empty when there is nothing to say.
/// </param>
/// <param name="Ticked">Whether it starts ticked.</param>
/// <param name="Owns">What it says it owns, or empty. A column of its own rather than part of the note.</param>
internal readonly record struct Tickable(string Label, string Note, bool Ticked, string Owns = "");

/// <summary>
/// A list where several rows are chosen rather than one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here because enrolling a folder and picking entries to move take many answers.</b>
/// Every other dialog asks for one — a choice, a name, a form — and <see cref="ChoiceDialog"/>
/// returns the row the cursor is on. Enrolment has to offer thirty repositories and take the
/// twenty-one somebody wants, which is a different question and needs a different answer.
/// </para>
/// <para>
/// <b>Shown a page of ten at a time.</b> A page is a list short enough to take in at once, the count line says which page
/// and how many, and the ticks belong to the rows rather than to the page, so moving between pages loses
/// nothing. PgDn and PgUp turn a page, as do the right and left arrows; down on a page's last row and up
/// on its first carry on into the next and the previous, so walking the whole list with one key still
/// works.
/// </para>
/// <para>
/// <b>Every key is taken on the list, before the list's own bindings see it.</b> On a screen the list has
/// focus, and a focused view is offered a key before its dialog is. The list binds Enter for itself, so Enter
/// would never reach the dialog's own handler. A test must send the key to the list, not the dialog, because
/// the dialog is the seam and not the screen.
/// </para>
/// <para>
/// <b>Space ticks, not Enter.</b> Enter is what every other dialog here finishes with, and a list
/// where Enter toggled would have no way to say "these ones, go" — so the two are kept apart and
/// the status line inside the dialog says which is which. Alt+a and Alt+n take all and none: the same
/// letters <see cref="Parley.Setup.RepositorySelection"/> uses on the command line, with the Alt every action
/// here carries, so a plain character is never an action.
/// </para>
/// <para>
/// <b>The rows are a grid</b>, so "already an agent" sits at the same column on every row. Each
/// column is as wide as its widest cell across <i>every</i> page, not the one on screen, so turning a page
/// does not move the columns under the reader.
/// </para>
/// </remarks>
internal sealed class TickDialog : TableDialog<TickDialog>, IKeyTable<TickDialog>
{
    /// <summary>How many rows a page shows.</summary>
    internal const int PageSize = 10;

    private const int Edges = 5;

    /// <summary>The space between two columns.</summary>
    private const string Gutter = "   ";

    /// <summary>How many rows the row under the cursor gets when it is drawn whole.</summary>
    private const int WholeRows = 4;

    /// <summary>What the tick column is as wide as, so the heading lines up with the names under it.</summary>
    private const string Box = "[ ] ";

    private readonly ListView _rows;
    private readonly int _labelWidth;
    private readonly int _noteWidth;
    private readonly bool _hasOwns;
    private readonly List<Tickable> _items;
    private readonly string _described;
    private readonly Plain _count;
    private readonly Plain _whole;
    private readonly bool _showsWhole;
    private ObservableCollection<string> _drawn = [];
    private int _page;

    /// <summary>Opens a dialog over a set of rows.</summary>
    /// <param name="title">What the dialog is called.</param>
    /// <param name="description">What is being decided, said before anything is offered.</param>
    /// <param name="items">The rows.</param>
    /// <param name="showsWholeRow">
    /// Whether the row under the cursor is also drawn whole, wrapped, under the list. For rows longer than a dialog is wide,
    /// because the arrows that would scroll sideways turn the page here.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal TickDialog(string title, string description, IReadOnlyList<Tickable> items, bool showsWholeRow = false)
    {
        HotKeySpecifier = Plain.NoHotKey;
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(description);

        Title = title;
        Width = Dim.Percent(80);

        _items = [.. items];
        _described = description;
        _showsWhole = showsWholeRow;

        var wholeRows = showsWholeRow ? WholeRows : 0;

        // A column is at least as wide as its heading, or a short column would push the next heading
        // out of line with what is under it. A column nobody has anything in is left out entirely.
        var notes = _items.Max(item => (int?)item.Note.Length) ?? 0;

        _labelWidth = Math.Max(_items.Max(item => (int?)item.Label.Length) ?? 0, "Agent".Length);
        _noteWidth = notes > 0 ? Math.Max(notes, "Note".Length) : 0;
        _hasOwns = _items.Any(item => item.Owns.Length > 0);

        var wants = Prompting.Rows(description);

        // A heading only when there is more than one column: over a list of bare names it would say
        // nothing the names do not.
        var hasColumns = _noteWidth > 0 || _hasOwns;
        var headed = hasColumns ? 1 : 0;

        // One row for the running count, one for the keys, and the edges. Both are inside the
        // dialog rather than on the shell's status line, because a modal takes the screen and the
        // line underneath it is describing a surface the person cannot reach.
        Height = Dim.Func(
            view => Math.Min(wants + headed + Math.Min(_items.Count, PageSize) + wholeRows + Edges, Room(view)),
            this);

        var said = new Plain
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Func(view => Math.Max(1, Math.Min(wants, Content(view) - 3)), this),
        };

        said.TextFormatter.WordWrap = true;

        var heading = new Plain
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),
            Height = headed,
            Text = hasColumns ? Heading : string.Empty,
            Visible = hasColumns,
        };

        _rows = new ListView
        {
            HotKeySpecifier = Plain.NoHotKey,
            X = 1,
            Y = Pos.Bottom(heading),
            Width = Dim.Fill(2),

            // Whatever is left, never one row per item: the viewport has to be true or the toolkit
            // will not scroll, and a terminal shorter than a page still has to work.
            Height = Dim.Fill(2 + wholeRows),
        };

        KeyRouting.TableFirst(_rows, this);

        _whole = new Plain
        {
            X = 1,
            Y = Pos.AnchorEnd(2 + wholeRows),
            Width = Dim.Fill(2),
            Height = wholeRows,
            Visible = showsWholeRow,
        };

        _whole.TextFormatter.WordWrap = true;
        _rows.ValueChanged += (_, _) => ShowWholeRow();

        _count = new Plain
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(2),
        };

        var keys = new Plain
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = KeysLine(paged: Pages > 1),
        };

        ShowPage(0, 0);

        Add(said, heading, _rows, _whole, _count, keys);
    }

    /// <summary>Raised when the person has chosen, or declined to.</summary>
    /// <remarks>
    /// Null when cancelled, which is told apart from choosing nothing: a person who pressed Escape
    /// has not said "enrol none of them", they have said "not now".
    /// </remarks>
    internal event EventHandler<IReadOnlyList<Tickable>?>? Done;

    /// <summary>What the line under the list says of the row under the cursor, or empty.</summary>
    internal string WholeRow => _whole.Text;

    /// <summary>What is being decided, as the dialog states it.</summary>
    internal string Described => _described;

    /// <summary>The rows, as they now stand, across every page.</summary>
    internal IReadOnlyList<Tickable> Items => _items;

    /// <summary>The list, so a test can move the cursor. It holds the page on screen.</summary>
    internal ListView Rows => _rows;

    /// <summary>Which page is on screen, counting from zero.</summary>
    internal int Page => _page;

    /// <summary>How many pages there are.</summary>
    internal int Pages => Math.Max(1, (_items.Count + PageSize - 1) / PageSize);

    /// <summary>The row naming the columns, laid out as the rows under it are.</summary>
    internal string Heading => Cells(new string(' ', Box.Length), "Agent", "Note", "Owns");

    /// <summary>What the count line says.</summary>
    internal string Counted =>
        $"{_items.Count(item => item.Ticked)} of {_items.Count} chosen."
        + (Pages > 1 ? $"   Page {_page + 1} of {Pages}." : string.Empty);

    /// <summary>Ticks or unticks one row.</summary>
    /// <param name="index">Which row, counted across every page.</param>
    internal void Toggle(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        _items[index] = _items[index] with { Ticked = !_items[index].Ticked };

        var onPage = index - (_page * PageSize);

        if (onPage >= 0 && onPage < _drawn.Count)
        {
            _drawn[onPage] = Row(_items[index]);
        }

        _count.Text = Counted;
    }

    /// <summary>Ticks every row, or none of them, on every page.</summary>
    /// <param name="ticked">Which way.</param>
    internal void SetAll(bool ticked)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i] = _items[i] with { Ticked = ticked };
        }

        ShowPage(_page, _rows.SelectedItem ?? 0);
    }

    private static int Room(View? view) => view?.App?.Screen.Height ?? int.MaxValue;

    private static int Content(View? view) =>
        view?.Viewport.Height is > 0 and var rows ? rows : int.MaxValue;

    /// <summary>How one row reads on screen.</summary>
    /// <remarks>
    /// The tick is text rather than a colour, for the reason every mark on this surface is: the
    /// tests read the screen with attributes stripped, and a state that exists only as a colour is
    /// one nothing can assert and a colour-blind reader cannot see.
    /// </remarks>
    private string Row(Tickable item) =>
        Cells($"[{(item.Ticked ? 'x' : ' ')}] ", item.Label, item.Note, item.Owns);

    /// <summary>Lays one row out in the grid's columns.</summary>
    /// <remarks>
    /// Padded, never cut: a column is as wide as its widest cell, and the last one runs on, so nothing a
    /// person decides on is hidden to make a row fit. The trailing space is trimmed so an empty last column
    /// leaves no padding behind.
    /// </remarks>
    private string Cells(string tick, string label, string note, string owns)
    {
        var line = tick + label.PadRight(_labelWidth);

        if (_noteWidth > 0)
        {
            line += Gutter + note.PadRight(_noteWidth);
        }

        if (_hasOwns && owns.Length > 0)
        {
            line += Gutter + owns;
        }

        return line.TrimEnd();
    }

    /// <summary>The row of the page the cursor is on.</summary>
    private int At => _rows.SelectedItem ?? 0;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// What a key does here, as the line along the bottom says. The arrows down and up carry on past the end of a page
    /// only at its edge, so one key still walks the whole list; anywhere else they are not live and the list moves its
    /// cursor, which is why the list's own binding of them is passed. Alt plus a letter, like every action on this
    /// surface, so a plain character is never an action and the list's own type-to-find is left alone.
    /// </para>
    /// <para>
    /// The list is offered the table's answer first, so the page keys and the arrows it binds are passed: a command
    /// claims each, and the arrows reach the list whenever theirs is not live.
    /// </para>
    /// </remarks>
    static CommandManager<TickDialog> IKeyTable<TickDialog>.BuildTable() => new([new(KeyContext.TickDialog, [])], Commands, Reserved);

    /// <summary>What a key does here.</summary>
    private static IReadOnlyList<KeyCommand<TickDialog>> Commands =>
    [
        new(KeyContext.TickDialog, default, CancelName, KeyCommand.Doing<TickDialog>(dialog => dialog.Done?.Invoke(dialog, null)), KeyCommand.Always, Chord: Key.Esc),
        new(KeyContext.TickDialog, default, GoName, KeyCommand.Doing<TickDialog>(dialog => dialog.Done?.Invoke(dialog, dialog._items)), KeyCommand.Always, Chord: Key.Enter),
        new(KeyContext.TickDialog, default, TickName, KeyCommand.Doing<TickDialog>(dialog => dialog.Toggle((dialog._page * PageSize) + dialog.At)), KeyCommand.Always, Chord: Key.Space),
        new(KeyContext.TickDialog, default, NextName, KeyCommand.Doing<TickDialog>(dialog => dialog.Turn(+1, dialog.At)), KeyCommand.Always, Chord: Key.PageDown),
        new(KeyContext.TickDialog, default, NextName, KeyCommand.Doing<TickDialog>(dialog => dialog.Turn(+1, dialog.At)), KeyCommand.Always, Chord: Key.CursorRight),
        new(KeyContext.TickDialog, default, PreviousName, KeyCommand.Doing<TickDialog>(dialog => dialog.Turn(-1, dialog.At)), KeyCommand.Always, Chord: Key.PageUp),
        new(KeyContext.TickDialog, default, PreviousName, KeyCommand.Doing<TickDialog>(dialog => dialog.Turn(-1, dialog.At)), KeyCommand.Always, Chord: Key.CursorLeft),
        new(
            KeyContext.TickDialog,
            default,
            "On to the next page",
            KeyCommand.Doing<TickDialog>(dialog => dialog.ShowPage(dialog._page + 1, 0)),
            dialog => dialog.At == dialog._drawn.Count - 1 && dialog._page < dialog.Pages - 1,
            Chord: Key.CursorDown),
        new(
            KeyContext.TickDialog,
            default,
            "Back to the previous page",
            KeyCommand.Doing<TickDialog>(dialog => dialog.ShowPage(dialog._page - 1, PageSize - 1)),
            dialog => dialog.At == 0 && dialog._page > 0,
            Chord: Key.CursorUp),
        new(KeyContext.TickDialog, 'a', AllName, KeyCommand.Doing<TickDialog>(dialog => dialog.SetAll(true)), KeyCommand.Always),
        new(KeyContext.TickDialog, 'n', NoneName, KeyCommand.Doing<TickDialog>(dialog => dialog.SetAll(false)), KeyCommand.Always),
    ];

    /// <summary>What the list takes when no command here does.</summary>
    private static IReadOnlyList<ReservedKey> Reserved =>
    [
        .. ReservedKey.Moving(KeyContext.TickDialog),
        .. ReservedKey.TakenBy(
            KeyContext.TickDialog,
            () => new ListView { HotKeySpecifier = Plain.NoHotKey },
            "the rows",
            Key.PageDown,
            Key.PageUp,
            Key.CursorDown,
            Key.CursorUp),
    ];

    /// <summary>The line along the bottom: each key and what it does, every key read from the table.</summary>
    /// <param name="paged">Whether there is more than one page to turn.</param>
    /// <returns>The line.</returns>
    /// <remarks>Read from the table so a key moved there cannot leave this line naming the old one.</remarks>
    internal static string KeysLine(bool paged)
    {
        string Says(string name, string what) => KeyCommand.Stroke(Commands, name) + ": " + what;

        return string.Join("   ", (IEnumerable<string>)
        [
            Says(TickName, "tick"),
            Says(AllName, "all"),
            Says(NoneName, "none"),
            .. paged ? [KeyCommand.Stroke(Commands, PreviousName) + "/" + Says(NextName, "page")] : Array.Empty<string>(),
            Says(GoName, "go"),
            Says(CancelName, "cancel"),
        ]);
    }

    private const string CancelName = "Cancel";

    private const string GoName = "Go";

    private const string TickName = "Tick";

    private const string NextName = "Next page";

    private const string PreviousName = "Previous page";

    private const string AllName = "All";

    private const string NoneName = "None";

    /// <summary>Turns a page, keeping the cursor on the same row of the page where it can.</summary>
    /// <param name="by">Which way.</param>
    /// <param name="at">The row the cursor is on.</param>
    /// <remarks>At the first or last page nothing moves, and the key is still this dialog's.</remarks>
    private void Turn(int by, int at)
    {
        var to = Math.Clamp(_page + by, 0, Pages - 1);

        if (to != _page)
        {
            ShowPage(to, at);
        }
    }

    /// <summary>Puts a page on screen.</summary>
    /// <param name="page">Which page.</param>
    /// <param name="row">The row of it to put the cursor on, clamped to what the page holds.</param>
    private void ShowPage(int page, int row)
    {
        _page = Math.Clamp(page, 0, Pages - 1);

        var first = _page * PageSize;

        _drawn = new ObservableCollection<string>(
            _items.Skip(first).Take(PageSize).Select(Row));

        _rows.SetSource(_drawn);
        _rows.SelectedItem = _drawn.Count > 0 ? Math.Clamp(row, 0, _drawn.Count - 1) : null;
        _count.Text = Counted;
        ShowWholeRow();
    }

    /// <summary>Draws the row under the cursor whole, when this dialog does that.</summary>
    private void ShowWholeRow()
    {
        var at = (_page * PageSize) + At;

        _whole.Text = _showsWhole && at < _items.Count ? _items[at].Label : string.Empty;
    }
}
