using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>Every place the terminal listens to a view's keys itself, rather than through a command table.</summary>
/// <remarks>
/// <para>
/// <b>One file, and nothing else in the assembly subscribes to a key.</b> A focused view is offered a key before the
/// dialog or the surface around it, so a few keys have to be met on the view. Each way of doing that is a method here,
/// and <c>KeysTakenBeforeTheTableTests</c> reads the whole assembly's code by reflection and fails on a key event
/// subscribed to anywhere else, or on a method here it does not name. Bindings are held the same way: none is added to
/// the application, and one is added to a view only by a pane or dialog the test presses.
/// </para>
/// <para>
/// <b>Two take no key of their own.</b> <see cref="TableFirst"/> hands a view's keys to its dialog's table, and
/// <see cref="Touching"/> notes that a key arrived without reading it. <b>Two compare keys themselves</b>, because what
/// they do belongs to the editor or the menu and to no place: what Enter means inside the box (<see cref="Box"/>), and
/// Left and Right across the menus (<see cref="Walking"/>). The box is pressed with its pane like every other view, so
/// a key added there that its place does not claim fails a unit test too.
/// </para>
/// </remarks>
internal static class KeyRouting
{
    /// <summary>Offers a view's keys to its dialog's table before the view's own bindings see them.</summary>
    /// <param name="view">The view: a list or a paragraph that has focus inside the dialog.</param>
    /// <param name="dialog">The dialog whose table answers.</param>
    /// <remarks>
    /// Without it a key the dialog binds is eaten by the view that has focus: a list binds Enter for itself, and a
    /// paragraph takes the arrows, so a dialog's own Enter or a letter on its buttons would never reach the dialog.
    /// </remarks>
    internal static void TableFirst(View view, TableDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(dialog);

        view.KeyDown += (_, key) =>
        {
            if (dialog.Take(key))
            {
                key.Handled = true;
            }
        };
    }

    /// <summary>Calls something for every key the application receives, before any view sees it, taking none.</summary>
    /// <param name="keyboard">The application's keyboard.</param>
    /// <param name="touched">What to call.</param>
    /// <returns>What lets the keyboard go, when the surface is disposed.</returns>
    /// <remarks>
    /// A key the focused view takes never reaches the surface's own handler, so without this writing an answer would mark
    /// the person away while they typed it. The key itself is never read, so nothing here can take one.
    /// </remarks>
    internal static IDisposable Touching(IKeyboard keyboard, Action touched)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(touched);

        void Any(object? sender, Key key) => touched();

        keyboard.KeyDown += Any;

        return new Releasing(() => keyboard.KeyDown -= Any);
    }

    /// <summary>What Enter and its chords mean inside the box a person writes in.</summary>
    /// <param name="box">The box.</param>
    /// <param name="breaking">Whether the box is adding a line break of its own, which is its own Enter and let through.</param>
    /// <param name="newLine">Adds a line break.</param>
    /// <param name="enter">What a bare Enter does: sends, or makes a trailing backslash a line break.</param>
    /// <remarks>
    /// <para>
    /// <b>Taken on the box, because that is where it lands.</b> A focused view is offered a key before the surface is,
    /// so Enter never reaches the surface while the box has focus. <c>KeyDown</c> rather than <c>Accepting</c>, because
    /// the box adds a line on Enter and that is a binding rather than an accept: the key has to be taken before the
    /// binding runs.
    /// </para>
    /// <para>
    /// <b>Shift+Enter is a line break, and so is Ctrl+Enter</b>, taken here because the box's own Enter binding would
    /// otherwise read either chord as Enter. The surface's table names Enter and Shift+Enter as commands and reserves
    /// Ctrl+Enter in the box's place, so the status line says what these do and no command can be put on them.
    /// </para>
    /// </remarks>
    internal static void Box(View box, Func<bool> breaking, Action newLine, Action enter)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(breaking);
        ArgumentNullException.ThrowIfNull(newLine);
        ArgumentNullException.ThrowIfNull(enter);

        box.KeyDown += (_, key) =>
        {
            if (key == Key.Enter.WithShift || key == Key.Enter.WithCtrl)
            {
                key.Handled = true;
                newLine();

                return;
            }

            // The break itself, which is the box's own Enter: let it through rather than reading it as a send.
            if (key != Key.Enter || breaking())
            {
                return;
            }

            key.Handled = true;
            enter();
        };
    }

    /// <summary>Left and Right on an open menu go to the neighbouring one.</summary>
    /// <param name="menu">The open menu.</param>
    /// <param name="walk">Opens the menu that many to the right, or to the left when negative.</param>
    /// <remarks>
    /// F10 opens only the first menu, and a bar a person can enter and not cross is half a bar. Taken on the menu's own
    /// key event, which runs before its bindings do. A menu is not a session the surface's table has a place for: it is
    /// the toolkit's popover, open only while somebody is choosing from it.
    /// </remarks>
    internal static void Walking(View menu, Action<int> walk)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(walk);

        menu.KeyDown += (_, key) =>
        {
            if (key == Key.CursorRight || key == Key.CursorLeft)
            {
                key.Handled = true;
                walk(key == Key.CursorRight ? 1 : -1);
            }
        };
    }

    /// <summary>Lets a subscription go once, however many times it is disposed.</summary>
    /// <param name="release">What lets it go.</param>
    private sealed class Releasing(Action release) : IDisposable
    {
        private Action? _release = release;

        /// <inheritdoc/>
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
