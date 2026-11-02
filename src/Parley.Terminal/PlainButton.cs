using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// A button that never takes a hot key from its label.
/// </summary>
/// <remarks>
/// <para>
/// <b>The toolkit's button reads an underscore in its title as a hot key</b>, and binds the letter after it, plain and
/// with Alt, wherever the button is. Every key the terminal takes is declared in a command table, and a button's label
/// names its key as <c>Name (Key)</c>, so a hot key read from a label is a key no table claims. A label set after the
/// dialog is built, in a handler, would bind one where nothing presses it: review set a button's title to <c>_Done</c>
/// and it took d, D and Alt+D.
/// </para>
/// <para>
/// So hot keys are turned off where they are read, as <see cref="Plain"/> does for labels, and the terminal makes no
/// toolkit button. <c>KeysTakenBeforeTheTableTests</c> refuses one, and refuses turning a hot key back on.
/// </para>
/// </remarks>
internal sealed class PlainButton : Button
{
    internal PlainButton() => HotKeySpecifier = Plain.NoHotKey;
}
