namespace Parley.Terminal;

/// <summary>A place in the transcript's drawn rows: which row, and which character in it.</summary>
/// <param name="Row">The row, counted from the first row of the whole transcript and not from the top of the screen.</param>
/// <param name="Column">The character in the row.</param>
internal readonly record struct Cell(int Row, int Column) : IComparable<Cell>
{
    /// <inheritdoc/>
    public int CompareTo(Cell other) => Row != other.Row ? Row.CompareTo(other.Row) : Column.CompareTo(other.Column);

    /// <summary>Whether this comes before another.</summary>
    /// <param name="left">One cell.</param>
    /// <param name="right">Another.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is earlier.</returns>
    public static bool operator <(Cell left, Cell right) => left.CompareTo(right) < 0;

    /// <summary>Whether this comes after another.</summary>
    /// <param name="left">One cell.</param>
    /// <param name="right">Another.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is later.</returns>
    public static bool operator >(Cell left, Cell right) => left.CompareTo(right) > 0;
}

/// <summary>
/// What somebody has dragged across in the transcript, from where they pressed to where they are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kept in the transcript's own coordinates and not the screen's</b>, so scrolling while the button is held
/// extends the selection rather than moving it, and so a selection of forty rows is one range and not forty.
/// Both ends are included: the cell the drag started on and the cell it is on now are both selected, which is what
/// a pointer on a character grid means.
/// </para>
/// <para>
/// <b>What is copied is the text and not the screen.</b> Every body row carries a four-column indent that is this
/// surface's structure rather than anybody's words, so a selection that reaches into it does not take it away with
/// it — the same reason copying one message leaves it behind. A row that was wrapped to fit is a row here, so a
/// selection across one is copied with a line break at the wrap: the transcript does not remember which breaks the
/// author wrote and which the width made.
/// </para>
/// <para>
/// Characters are counted as characters. A row holding a double-width one is highlighted a column to the left of
/// where it is drawn from that character onward, and copied correctly; the highlight is the only thing that is off.
/// </para>
/// </remarks>
internal sealed class TextSelection
{
    /// <summary>Starts a selection on a single cell.</summary>
    /// <param name="at">Where the button was pressed.</param>
    internal TextSelection(Cell at)
    {
        Anchor = at;
        Caret = at;
    }

    /// <summary>Where the drag began.</summary>
    internal Cell Anchor { get; }

    /// <summary>Where the drag is now.</summary>
    internal Cell Caret { get; set; }

    /// <summary>Whether nothing but the first cell is selected, which is what a click is.</summary>
    internal bool IsClick => Anchor == Caret;

    /// <summary>The earlier end.</summary>
    internal Cell Start => Anchor < Caret ? Anchor : Caret;

    /// <summary>The later end.</summary>
    internal Cell End => Anchor < Caret ? Caret : Anchor;

    /// <summary>Which characters of a row are selected.</summary>
    /// <param name="row">The row.</param>
    /// <param name="length">How many characters it has.</param>
    /// <param name="from">The first selected character.</param>
    /// <param name="to">One past the last.</param>
    /// <returns><see langword="true"/> when anything in the row is selected.</returns>
    internal bool Covers(int row, int length, out int from, out int to)
    {
        from = 0;
        to = 0;

        if (row < Start.Row || row > End.Row)
        {
            return false;
        }

        from = row == Start.Row ? Math.Min(Start.Column, length) : 0;
        to = row == End.Row ? Math.Min(End.Column + 1, length) : length;

        return to > from;
    }

    /// <summary>The selected text.</summary>
    /// <param name="rows">The transcript's drawn rows.</param>
    /// <param name="indent">How many columns of every indented row are structure.</param>
    /// <returns>The text, one line per row, with nothing trailing.</returns>
    internal string Text(IReadOnlyList<string> rows, int indent)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var lines = new List<string>();
        var margin = new string(' ', indent);

        for (var row = Start.Row; row <= End.Row && row < rows.Count; row++)
        {
            var text = rows[row];

            if (!Covers(row, text.Length, out var from, out var to))
            {
                // A row with nothing selected in it, between two that have, is a blank line the person dragged across.
                lines.Add(string.Empty);

                continue;
            }

            if (text.StartsWith(margin, StringComparison.Ordinal))
            {
                from = Math.Max(from, indent);
            }

            lines.Add(to > from ? text[from..to].TrimEnd() : string.Empty);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }
}
