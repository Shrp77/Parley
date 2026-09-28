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
internal readonly record struct Tickable(string Label, string Note, bool Ticked);

/// <summary>
/// A list where several rows are chosen rather than one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here because enrolling a folder is the one act on this surface that takes many answers.</b>
/// Every other dialog asks for one — a choice, a name, a form — and <see cref="ChoiceDialog"/>
/// returns the row the cursor is on. Enrolment has to offer thirty repositories and take the
/// twenty-one somebody wants, which is a different question and needs a different answer.
/// </para>
/// <para>
/// <b>Shown a page of ten at a time</b>, the owner's call of 2026-09-27 after scrolling a long list of
/// folders felt slow. A page is a list short enough to take in at once, the count line says which page
/// and how many, and the ticks belong to the rows rather than to the page, so moving between pages loses
/// nothing. PgDn and PgUp turn a page, as do the right and left arrows; down on a page's last row and up
/// on its first carry on into the next and the previous, so walking the whole list with one key still
/// works.
/// </para>
/// <para>
/// <b>Every key is taken on the list, before the list's own bindings see it.</b> They were taken in the
/// dialog's own handler, and on a screen the list has focus — and a focused view is offered a key before
/// its dialog is, 0.30.4's sentence. The list binds Enter for itself, so Enter never reached the dialog:
/// reported by the owner as four repositories ticked and Enter doing nothing. Space and the letters only
/// worked because the list happens not to bind them. The tests that passed throughout sent the key to the
/// dialog directly, which is the seam and not the screen.
/// </para>
/// <para>
/// <b>Space ticks, not Enter.</b> Enter is what every other dialog here finishes with, and a list
/// where Enter toggled would have no way to say "these ones, go" — so the two are kept apart and
/// the status line inside the dialog says which is which. `a` and `n` take all and none, the same
/// letters <see cref="Parley.Setup.RepositorySelection"/> uses for the same job on the command
/// line, so a person who knows one knows the other.
/// </para>
/// </remarks>
internal sealed class TickDialog : Dialog
{
    /// <summary>How many rows a page shows.</summary>
    internal const int PageSize = 10;

    private const int Edges = 5;

    private readonly ListView _rows;
    private readonly List<Tickable> _items;
    private readonly string _described;
    private readonly Plain _count;
    private ObservableCollection<string> _drawn = [];
    private int _page;

    /// <summary>Opens a dialog over a set of rows.</summary>
    /// <param name="title">What the dialog is called.</param>
    /// <param name="description">What is being decided, said before anything is offered.</param>
    /// <param name="items">The rows.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal TickDialog(string title, string description, IReadOnlyList<Tickable> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(description);

        Title = title;
        Width = Dim.Percent(80);

        _items = [.. items];
        _described = description;

        var wants = Prompting.Rows(description);

        // One row for the running count, one for the keys, and the edges. Both are inside the
        // dialog rather than on the shell's status line, because a modal takes the screen and the
        // line underneath it is describing a surface the person cannot reach.
        Height = Dim.Func(view => Math.Min(wants + Math.Min(_items.Count, PageSize) + Edges, Room(view)), this);

        var said = new Plain
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Func(view => Math.Max(1, Math.Min(wants, Content(view) - 3)), this),
        };

        said.TextFormatter.WordWrap = true;

        _rows = new ListView
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),

            // Whatever is left, never one row per item: the viewport has to be true or the toolkit
            // will not scroll, and a terminal shorter than a page still has to work.
            Height = Dim.Fill(2),
        };

        _rows.KeyDown += (_, key) =>
        {
            if (Handle(key))
            {
                key.Handled = true;
            }
        };

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
            Text = Pages > 1
                ? "Space: tick   a: all   n: none   PgUp/PgDn: page   Enter: go   Esc: cancel"
                : "Space: tick   a: all   n: none   Enter: go   Esc: cancel",
        };

        ShowPage(0, 0);

        Add(said, _rows, _count, keys);
    }

    /// <summary>Raised when the person has chosen, or declined to.</summary>
    /// <remarks>
    /// Null when cancelled, which is told apart from choosing nothing: a person who pressed Escape
    /// has not said "enrol none of them", they have said "not now".
    /// </remarks>
    internal event EventHandler<IReadOnlyList<Tickable>?>? Done;

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

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Reached when the list does not have focus. When it does, the list's own handler above has already
        // taken the key, and it does not arrive here.
        return Handle(key) || base.OnKeyDown(key);
    }

    /// <summary>Whether a key is that letter, plainly typed.</summary>
    /// <remarks>
    /// The same test the shell's own actions use: a letter with no modifier. Ctrl and Alt are
    /// excluded so that a chord meant for something else is not read as a plain letter here.
    /// </remarks>
    private static bool Typed(Key key, char character) =>
        !key.IsCtrl && !key.IsAlt && key.AsRune == new System.Text.Rune(character);

    private static int Room(View? view) => view?.App?.Screen.Height ?? int.MaxValue;

    private static int Content(View? view) =>
        view?.Viewport.Height is > 0 and var rows ? rows : int.MaxValue;

    /// <summary>How one row reads on screen.</summary>
    /// <remarks>
    /// The tick is text rather than a colour, for the reason every mark on this surface is: the
    /// tests read the screen with attributes stripped, and a state that exists only as a colour is
    /// one nothing can assert and a colour-blind reader cannot see.
    /// </remarks>
    private static string Row(Tickable item) =>
        $"[{(item.Ticked ? 'x' : ' ')}] {item.Label}{(item.Note.Length > 0 ? $"   {item.Note}" : string.Empty)}";

    /// <summary>Does what a key means here.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it meant anything; a key that did not is left to the list, which moves the cursor.</returns>
    private bool Handle(Key key)
    {
        var at = _rows.SelectedItem ?? 0;

        if (key == Key.Esc)
        {
            Done?.Invoke(this, null);

            return true;
        }

        if (key == Key.Enter)
        {
            Done?.Invoke(this, _items);

            return true;
        }

        if (key == Key.Space)
        {
            Toggle((_page * PageSize) + at);

            return true;
        }

        if (key == Key.PageDown || key == Key.CursorRight)
        {
            return Turn(+1, at);
        }

        if (key == Key.PageUp || key == Key.CursorLeft)
        {
            return Turn(-1, at);
        }

        // Carrying on past the end of a page, so one key still walks the whole list.
        if (key == Key.CursorDown && at == _drawn.Count - 1 && _page < Pages - 1)
        {
            ShowPage(_page + 1, 0);

            return true;
        }

        if (key == Key.CursorUp && at == 0 && _page > 0)
        {
            ShowPage(_page - 1, PageSize - 1);

            return true;
        }

        // Letters, and they have to be taken here: a ListView's own type-to-find would otherwise
        // eat them, which is the collision `Lists` already records between a list's search and the
        // letters a surface advertises. While this dialog is open the letters mean what it says.
        if (Typed(key, 'a'))
        {
            SetAll(true);

            return true;
        }

        if (Typed(key, 'n'))
        {
            SetAll(false);

            return true;
        }

        return false;
    }

    /// <summary>Turns a page, keeping the cursor on the same row of the page where it can.</summary>
    /// <param name="by">Which way.</param>
    /// <param name="at">The row the cursor is on.</param>
    /// <returns>Always <see langword="true"/>: at the first or last page the key is still this dialog's.</returns>
    private bool Turn(int by, int at)
    {
        var to = Math.Clamp(_page + by, 0, Pages - 1);

        if (to != _page)
        {
            ShowPage(to, at);
        }

        return true;
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
    }
}
