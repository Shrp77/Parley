using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Parley.Terminal;

/// <summary>A key a view takes before any command, in one place or a layer.</summary>
/// <param name="Context">Where it is taken.</param>
/// <param name="Key">The key.</param>
/// <param name="Name">What it does, for the message that names a clash.</param>
/// <remarks>
/// A list moves its cursor; a box edits; a focused button presses. None is an action with a label, but a command bound
/// to one would be named on screen and never run. Declaring them here is what lets the check see them. A key the
/// surface or a dialog takes itself, rather than a view inside it, is a <see cref="KeyHandler{TTarget}"/> instead.
/// </remarks>
internal sealed record ReservedKey(KeyContext Context, Key Key, string Name)
{
    /// <summary>Every key a view takes through its own key bindings, as reservations in one place.</summary>
    /// <param name="context">The place the view is in.</param>
    /// <param name="build">Builds a view the way the place builds it; it is read and disposed of.</param>
    /// <param name="name">What the view is, for the message that names a clash.</param>
    /// <param name="passes">Keys the view binds and lets through here, each claimed by somebody else in the place.</param>
    /// <returns>The reservations.</returns>
    /// <remarks>
    /// <para>
    /// <b>Read from the toolkit's own bindings rather than copied out of them</b>, so an upgrade that binds one more key
    /// to a list claims it at once. A copy would be a second statement of the toolkit, right until it was not. Hot keys
    /// are read with the keys, because the toolkit offers a view its hot keys as well.
    /// </para>
    /// <para>
    /// <b>A binding to accepting or activating alone is not a reservation.</b> Every view binds Enter and Space that way,
    /// and the key is taken only where something answers the event; a place that has a handler reserves the key itself.
    /// </para>
    /// <para>
    /// <b><paramref name="passes"/> is a decision written down</b>: the box binds Enter to a new line and takes it first
    /// as sending, which a command names; a list in a dialog binds the page keys the dialog turns pages on first.
    /// Listing a key there and claiming it nowhere else in the place fails the test that probes the views, so a pass
    /// cannot quietly hide a key.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ReservedKey> TakenBy(KeyContext context, Func<View> build, string name, params Key[] passes)
    {
        ArgumentNullException.ThrowIfNull(build);

        using var view = build();

        return [.. view.KeyBindings.GetBindings().Concat(view.HotKeyBindings.GetBindings())
            .Where(binding => binding.Value.Commands.Any(command => command is not (Command.Accept or Command.Activate)))
            .Select(binding => binding.Key)
            .Where(key => !passes.Contains(key))
            .Select(key => new ReservedKey(context, key, name))];
    }

    /// <summary>The keys that move between a dialog's fields and buttons, as reservations in its place.</summary>
    /// <param name="context">The dialog's place.</param>
    /// <returns>The reservations.</returns>
    /// <remarks>
    /// The toolkit moves focus on Tab once the dialog's views have let it through, so a dialog's own command on Tab would
    /// run first and leave a person no way between fields without a mouse.
    /// </remarks>
    public static IReadOnlyList<ReservedKey> Moving(KeyContext context) =>
        [new(context, Key.Tab, "the next field"), new(context, Key.Tab.WithShift, "the previous field")];

    /// <summary>The keys that type a space, as reservations in a place somebody types in.</summary>
    /// <param name="context">The place.</param>
    /// <param name="into">What is typed into, for the message that names a clash.</param>
    /// <returns>The reservations.</returns>
    /// <remarks>
    /// The one printable key a command may have as a chord: Space acts in the room and in a list of ticks, so the check
    /// has to know where it is typing instead. No binding says so; a box takes it as text.
    /// </remarks>
    public static IReadOnlyList<ReservedKey> Typing(KeyContext context, string into) =>
        [new(context, Key.Space, "a space in " + into), new(context, Key.Space.WithShift, "a space in " + into)];

    /// <summary>The keys a focused button takes, as reservations in a dialog's place.</summary>
    /// <param name="context">The dialog's place.</param>
    /// <param name="enter">What Enter does there, which is more than the button when one is the default.</param>
    /// <returns>The reservations.</returns>
    /// <remarks>
    /// A button binds Enter and Space to accepting, which <see cref="TakenBy"/> leaves to the place to say, and every
    /// button here answers it.
    /// </remarks>
    public static IReadOnlyList<ReservedKey> Pressing(KeyContext context, string enter = "the focused button") =>
        [new(context, Key.Enter, enter), new(context, Key.Space, "the focused button")];
}

/// <summary>A key the surface or a dialog takes itself, with no label: moving between panes, quitting, the menus.</summary>
/// <typeparam name="TTarget">What it acts on.</typeparam>
/// <param name="Context">Where it is taken: a place, or a layer.</param>
/// <param name="Key">The key.</param>
/// <param name="Name">What it does, for the message that names a clash.</param>
/// <param name="Take">What it does; returns whether it took the key, so one that did nothing leaves it to the toolkit.</param>
/// <remarks>
/// <para>
/// <b>In the table rather than in a key handler</b>, so what the surface takes is declared where every other claim is,
/// checked against them, and read by the one call that answers a key. The surface's handler once compared keys itself,
/// and every way of comparing one there was a way to take a key the table had never seen.
/// </para>
/// <para>
/// <b>Not a command</b>: it is named on no status line or menu, which is what Tab, the quit chord and F10 are. A command
/// on its key in a place it is live in is refused, as on a reservation; a view's own reservation of the same key is not,
/// because the view takes the key only while it has focus and what it declines reaches this.
/// </para>
/// </remarks>
internal sealed record KeyHandler<TTarget>(KeyContext Context, Key Key, string Name, Func<TTarget, bool> Take);
