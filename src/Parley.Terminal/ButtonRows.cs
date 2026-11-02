using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>A dialog's buttons along its foot, in as many rows as its width needs.</summary>
/// <remarks>
/// <para>
/// <b>Wrapped rather than cut off.</b> Every button's label names its key (<c>Name (Key)</c>), and four of them in one row
/// ran past the edge of a proposed roadmap at eighty columns: "Leave (Esc)" was not on screen, so a person could not see
/// how to leave. A button that does not fit starts a new row instead.
/// </para>
/// <para>
/// Each button's width is measured once, as the toolkit draws it, when the rows are made: a label never changes while
/// the dialog is open, and measuring here keeps the toolkit's brackets and shadow out of this file.
/// </para>
/// </remarks>
internal sealed class ButtonRows
{
    private readonly View _dialog;
    private readonly int[] _widths;
    private readonly int _rowHeight;

    /// <summary>Places the buttons; the caller adds them to the dialog.</summary>
    /// <param name="dialog">The dialog whose foot they sit along.</param>
    /// <param name="buttons">The buttons, in the order they read.</param>
    internal ButtonRows(View dialog, params Button[] buttons)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(buttons);

        _dialog = dialog;
        _widths = new int[buttons.Length];

        for (var at = 0; at < buttons.Length; at++)
        {
            var button = buttons[at];

            button.Layout(new Size(short.MaxValue, short.MaxValue));
            _widths[at] = button.Frame.Width;
            _rowHeight = Math.Max(_rowHeight, button.Frame.Height);

            var index = at;

            button.X = Pos.Func(_ => Place(index).X);
            button.Y = Pos.Func(_ => _dialog.Viewport.Height - ((Rows - Place(index).Row) * _rowHeight));
        }
    }

    /// <summary>The rows the buttons take at the dialog's width now.</summary>
    internal int Height => Rows * _rowHeight;

    /// <summary>How many rows the buttons take at the dialog's width now.</summary>
    private int Rows => _widths.Length == 0 ? 0 : Place(_widths.Length - 1).Row + 1;

    /// <summary>Where one button sits: the first column, and a gap between buttons, as one row always had.</summary>
    /// <param name="index">Which button.</param>
    /// <returns>Its column, and its row counted from the top of the buttons.</returns>
    private (int X, int Row) Place(int index)
    {
        var width = _dialog.Viewport.Width;
        var (x, row) = (1, 0);

        for (var at = 0; ; at++)
        {
            // A button that would run past the edge starts a row, unless it is the first in its row and fits nowhere.
            if (x > 1 && x + _widths[at] > width)
            {
                (x, row) = (1, row + 1);
            }

            if (at == index)
            {
                return (x, row);
            }

            x += _widths[at] + 1;
        }
    }
}
