using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Parley.Cli.Tests.Top;

/// <summary>
/// What a list is showing, and the colour it draws each row in.
/// </summary>
/// <remarks>
/// <para>
/// One copy, for the reason <c>Loopback</c> and <c>Driving</c> are each one copy: five test files
/// had grown their own <c>Rows</c> within a day of one another, and two had grown a second helper
/// to read a row's colour. This repository has consolidated the same shape twice before and
/// written down why — a duplicated helper does not fail when it drifts, it answers differently,
/// and the copy that drifted is the one somebody trusts.
/// </para>
/// <para>
/// Both read what is on screen rather than the model behind it, which is the whole point: a test
/// that asked a region for its rows would pass while the list showed something else.
/// </para>
/// </remarks>
internal static class Drawn
{
    /// <summary>Everything a list is showing, in order.</summary>
    /// <param name="list">The list.</param>
    /// <returns>The rows, top to bottom.</returns>
    internal static List<string> Rows(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var source = list.Source;

        return [.. Enumerable
            .Range(0, source?.Count ?? 0)
            .Select(at => source!.ToList()[at]?.ToString() ?? string.Empty)];
    }

    /// <summary>
    /// The colour a list gives each of its rows, in order.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <returns>One colour per row, null where the row is left in the terminal's own.</returns>
    /// <remarks>
    /// Asked of the list rather than read off the screen. A region built outside an application
    /// has never been drawn, and building the whole surface to find out what colour one row is
    /// would make every colour test a layout test as well.
    /// </remarks>
    internal static List<Color?> Tints(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);

        return [.. Enumerable.Range(0, list.Source?.Count ?? 0).Select(at => Tint(list, at))];
    }

    /// <summary>The colour a list gives one row, or null when it gives it none.</summary>
    /// <param name="list">The list.</param>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null.</returns>
    internal static Color? Tint(ListView list, int row)
    {
        ArgumentNullException.ThrowIfNull(list);

        var arguments = new ListViewRowEventArgs(row);

        list.OnRowRender(arguments);

        return arguments.RowAttribute?.Foreground;
    }
}
