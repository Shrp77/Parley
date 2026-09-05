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
    /// <returns>The list.</returns>
    internal static ListView Filling() =>
        new() { Width = Dim.Fill(), Height = Dim.Fill(), KeystrokeNavigator = null };
}
