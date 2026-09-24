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
/// <b>The sizing is <see cref="ChoiceDialog"/>'s, deliberately copied rather than approximated.</b>
/// That dialog carries two scars in its own comments: a list told it is taller than its dialog
/// believes every row is visible and never scrolls, so the cursor walks off the bottom and the
/// highlight goes with it; and a description sized from what the list left over drew nothing at all
/// once there were twenty rows. Both were found against a real deployment with nineteen agents in
/// it, which is the size this dialog is for.
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
    private readonly ListView _rows;
    private readonly ObservableCollection<string> _drawn;
    private readonly List<Tickable> _items;
    private readonly string _described;
    private readonly Plain _count;

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
        Height = Dim.Func(view => Math.Min(wants + _items.Count + Edges, Room(view)), this);

        var said = new Plain
        {
            Text = description,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Dim.Func(view => Math.Max(1, Math.Min(wants, Content(view) - 3)), this),
        };

        said.TextFormatter.WordWrap = true;

        _drawn = new ObservableCollection<string>(_items.Select(Row));

        _rows = new ListView
        {
            X = 1,
            Y = Pos.Bottom(said),
            Width = Dim.Fill(2),

            // Whatever is left, never one row per item: the viewport has to be true or the toolkit
            // will not scroll, and this dialog exists for lists longer than the screen.
            Height = Dim.Fill(2),
        };

        _rows.SetSource(_drawn);
        _rows.SelectedItem = 0;

        _count = new Plain
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(2),
            Text = Counted,
        };

        var keys = new Plain
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(2),
            Text = "Space: tick   a: all   n: none   Enter: go   Esc: cancel",
        };

        Add(said, _rows, _count, keys);
    }

    /// <summary>Whether a key is that letter, plainly typed.</summary>
    /// <remarks>
    /// The same test the shell's own actions use: a letter with no modifier. Ctrl and Alt are
    /// excluded so that a chord meant for something else is not read as a plain letter here.
    /// </remarks>
    private static bool Typed(Key key, char character) =>
        !key.IsCtrl && !key.IsAlt && key.AsRune == new System.Text.Rune(character);

    private const int Edges = 5;

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

    private string Counted =>
        $"{_items.Count(item => item.Ticked)} of {_items.Count} chosen.";

    /// <summary>Raised when the person has chosen, or declined to.</summary>
    /// <remarks>
    /// Null when cancelled, which is told apart from choosing nothing: a person who pressed Escape
    /// has not said "enrol none of them", they have said "not now".
    /// </remarks>
    internal event EventHandler<IReadOnlyList<Tickable>?>? Done;

    /// <summary>What is being decided, as the dialog states it.</summary>
    internal string Described => _described;

    /// <summary>The rows, as they now stand.</summary>
    internal IReadOnlyList<Tickable> Items => _items;

    /// <summary>The list, so a test can move the cursor.</summary>
    internal ListView Rows => _rows;

    /// <summary>Ticks or unticks one row.</summary>
    /// <param name="index">Which row.</param>
    internal void Toggle(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        _items[index] = _items[index] with { Ticked = !_items[index].Ticked };
        _drawn[index] = Row(_items[index]);
        _count.Text = Counted;
    }

    /// <summary>Ticks every row, or none of them.</summary>
    /// <param name="ticked">Which way.</param>
    internal void SetAll(bool ticked)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i] = _items[i] with { Ticked = ticked };
            _drawn[i] = Row(_items[i]);
        }

        _count.Text = Counted;
    }

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

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
            Toggle(_rows.SelectedItem ?? -1);

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

        return base.OnKeyDown(key);
    }
}
