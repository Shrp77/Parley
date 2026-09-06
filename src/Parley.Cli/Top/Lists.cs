using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

/// <summary>
/// The one shape of list this surface shows things in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keystroke navigation is off, and that is the whole reason this exists.</b> A
/// <see cref="ListView"/> searches its own items as you type, and it does that by consuming every
/// printable key before the window above it sees one. This surface binds printable keys — <c>c</c>
/// creates a project, <c>r</c> removes one, <c>?</c> lists what a region can do — so with the
/// navigator on, every letter the status line advertises was swallowed by whichever list had
/// focus. Nothing on screen said so: the letters were listed, pressing them did nothing at all,
/// and the empty projects region said <i>Press c to create one</i> under a <c>c</c> that could
/// not work.
/// </para>
/// <para>
/// The suite did not catch it because every test of a key checked the negative — that <c>?</c>
/// does <i>not</i> open a menu over a dialog, that no action steals a letter the surface has
/// already spoken for. <see cref="Terminal.Gui.Input.Key.Tab"/> was the one key checked
/// positively, and Tab is the one printable-key-adjacent thing a list does not eat. A key tested
/// only in the negative is a key nobody has pressed.
/// </para>
/// <para>
/// Type-to-find is a real loss and it is the right one to take. It is undiscoverable — nothing
/// announces it — while the letters are written across the status line as a promise, and a
/// surface where the advertised keys work beats one where an unadvertised convenience does.
/// </para>
/// </remarks>
internal static class Lists
{
    /// <summary>A list that fills what it is put in, and leaves the keyboard alone.</summary>
    /// <param name="accent">
    /// The colour of the region it sits in, or null to take whatever it is put inside.
    /// </param>
    /// <returns>The list.</returns>
    /// <remarks>
    /// Null is for a list inside a dialog. A dialog is drawn on a ground of the toolkit's choosing,
    /// and a list that insisted on the terminal's own background would be a rectangle of the wrong
    /// colour inside it.
    /// </remarks>
    internal static ListView Filling(Color? accent = null)
    {
        var list = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), KeystrokeNavigator = null };

        if (accent is { } colour)
        {
            list.SetScheme(Colours.Rows(colour));
        }

        return list;
    }

    /// <summary>
    /// Colours the rows of a list from what each row says.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="tint">The colour a row earns, or null when it earns none.</param>
    /// <remarks>
    /// <para>
    /// <b>The highlighted row is never tinted, and that is not a detail.</b> A row attribute set
    /// here replaces the one the toolkit would have drawn, highlight included — so tinting the
    /// selected row would take away the only mark saying which row a key is about to act on, in
    /// the two regions where a key retires an agent or destroys a conversation. The toolkit's own
    /// highlight is left to win.
    /// </para>
    /// <para>
    /// The colour is asked for by row number rather than fixed at the time the list is filled,
    /// because a list is refilled on every read and a parallel array of colours would be one more
    /// thing that can fall out of step with the rows it describes.
    /// </para>
    /// </remarks>
    internal static void Tint(ListView list, Func<int, Color?> tint)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(tint);

        list.RowRender += (_, drawing) =>
        {
            if (list.SelectedItem == drawing.Row && list.HasFocus)
            {
                return;
            }

            if (tint(drawing.Row) is { } colour)
            {
                drawing.RowAttribute = Colours.Row(colour);
            }
        };
    }
}
