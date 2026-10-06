using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>
/// The transcript's list, which lets a reader drag across any of it and copy what they covered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mouse reporting is on for this surface, so the terminal's own selection is not available</b> and a drag
/// arrives here as events instead. Without this, taking a few lines out of a conversation meant copying all of
/// it with Alt+C or holding Shift in the hope that the terminal honours it — and Shift-dragging copies the screen,
/// borders and the room's column included, which is exactly what a person did not mean to take.
/// </para>
/// <para>
/// <b>The press starts a selection and grabs the mouse</b>, so the drag keeps arriving when the pointer leaves
/// the list, and a pointer above or below the rows scrolls them: a selection of more than a screen is the
/// ordinary case in a conversation. A press without a drag is a click, and clicking leaves the selection empty, which
/// is how a reader dismisses one.
/// </para>
/// <para>
/// The press itself does not select a row; the click that follows a release does, as it always has, so a
/// plain click behaves as it did and only a drag is new.
/// </para>
/// </remarks>
internal sealed class TranscriptList : GuardedList
{
    private TextSelection? _selection;

    private bool _dragging;

    /// <summary>The rows as they are drawn, which a selection is a range of.</summary>
    internal Func<IReadOnlyList<string>> Rows { get; set; } = () => [];

    /// <summary>How many columns of an indented row are structure and not words.</summary>
    internal int Indent { get; set; }

    /// <summary>Raised when what is selected changes, so the surface can say what Ctrl+C would copy.</summary>
    internal event EventHandler? SelectionChanged;

    /// <summary>Whether anything is selected.</summary>
    internal bool HasSelection => _selection is { IsClick: false };

    /// <summary>The selection as it stands, or null.</summary>
    internal TextSelection? Selection => HasSelection ? _selection : null;

    /// <summary>The selected text, or null when nothing is selected.</summary>
    /// <returns>What is covered.</returns>
    internal string? SelectedText() =>
        Selection is { } held ? held.Text(Rows(), Indent) : null;

    /// <summary>Starts a selection, as a press does.</summary>
    /// <param name="at">Where.</param>
    /// <remarks>Separate from the mouse so a test can make a selection without a terminal that reports a drag.</remarks>
    internal void Begin(Cell at)
    {
        var had = _selection is not null;

        _selection = new TextSelection(at);
        _dragging = true;

        if (had)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        SetNeedsDraw();
    }

    /// <summary>Extends the selection to a cell, as a drag does.</summary>
    /// <param name="to">Where the pointer is now.</param>
    internal void Extend(Cell to)
    {
        if (!_dragging || _selection is not { } held || to == held.Caret)
        {
            return;
        }

        held.Caret = to;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    /// <summary>Ends the drag, as a release does.</summary>
    /// <returns><see langword="true"/> when something was selected.</returns>
    internal bool Finish()
    {
        _dragging = false;

        if (_selection is { IsClick: true })
        {
            // A click, not a drag: nothing is selected.
            _selection = null;
            SetNeedsDraw();

            return false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();

        return _selection is not null;
    }

    /// <summary>Drops the selection.</summary>
    internal void ClearSelection()
    {
        if (_selection is null)
        {
            return;
        }

        _selection = null;
        _dragging = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    /// <summary>Keeps a selection across a redraw if the rows it covers are the same rows, and drops it if not.</summary>
    /// <param name="before">The rows before.</param>
    /// <param name="after">The rows after.</param>
    /// <remarks>
    /// A transcript grows only at its end, so a selection above the end survives every follow. A fold, a resize that
    /// re-wraps, or another conversation moves what row N is, and a highlight left on different words is worse than none.
    /// </remarks>
    internal void Keep(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (_selection is not { } held || _dragging)
        {
            return;
        }

        for (var row = held.Start.Row; row <= held.End.Row; row++)
        {
            if (row >= before.Count || row >= after.Count
                || !string.Equals(before[row], after[row], StringComparison.Ordinal))
            {
                ClearSelection();

                return;
            }
        }
    }

    /// <summary>Which cell a position in the list is over, held inside the rows there are.</summary>
    /// <param name="position">The position, relative to the list's viewport; it may be outside it while a drag is held.</param>
    /// <returns>The cell.</returns>
    internal Cell CellAt(Point position)
    {
        var rows = Rows();
        var row = Math.Clamp(Viewport.Y + position.Y, 0, Math.Max(0, rows.Count - 1));
        var length = row < rows.Count ? rows[row].Length : 0;

        return new Cell(row, Math.Clamp(Viewport.X + position.X, 0, Math.Max(0, length - 1)));
    }

    /// <inheritdoc/>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);

        if (mouse.Position is not { } where)
        {
            return base.OnMouseEvent(mouse);
        }

        var left = mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed);
        var moving = mouse.Flags.HasFlag(MouseFlags.PositionReport);

        if (left && !moving && !_dragging)
        {
            // A new press: whatever was selected is let go, and this is where a drag may begin.
            Begin(CellAt(where));
            App?.Mouse.GrabMouse(this);
            SetFocus();

            return true;
        }

        if (_dragging && (moving || left))
        {
            Scroll(where);
            Extend(CellAt(where));

            return true;
        }

        if (_dragging && mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
        {
            App?.Mouse.UngrabMouse();

            // A click goes on to select the row as it always has; a drag is the whole of the event.
            return Finish();
        }

        return base.OnMouseEvent(mouse);
    }

    /// <summary>Scrolls a row when a held drag is above or below the rows, which is how a selection longer than a screen is made.</summary>
    /// <param name="where">The pointer, relative to the viewport.</param>
    private void Scroll(Point where)
    {
        var view = Viewport;
        var rows = Rows().Count;

        if (view.Height <= 0)
        {
            return;
        }

        var y = where.Y < 0
            ? Math.Max(0, view.Y - 1)
            : where.Y >= view.Height ? Math.Min(Math.Max(0, rows - view.Height), view.Y + 1) : view.Y;

        if (y != view.Y)
        {
            Viewport = view with { Y = y };
        }
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var handled = base.OnDrawingContent(context);

        if (Selection is not { } held)
        {
            return handled;
        }

        var rows = Rows();
        var view = Viewport;

        SetAttribute(Colours.Selected);

        for (var y = 0; y < view.Height; y++)
        {
            var row = view.Y + y;

            if (row >= rows.Count)
            {
                break;
            }

            if (!held.Covers(row, rows[row].Length, out var from, out var to) || to <= view.X)
            {
                continue;
            }

            var first = Math.Max(from, view.X);

            Move(first - view.X, y);
            AddStr(rows[row][first..to]);
        }

        return handled;
    }
}
