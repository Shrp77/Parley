using Terminal.Gui.Input;

namespace Parley.Terminal;

/// <summary>Helpers for writing a command table.</summary>
/// <remarks>Static helpers beside the record so a table reads as a list of commands and nothing else.</remarks>
internal static class KeyCommand
{
    /// <summary>A condition that always holds, for a command live wherever its place is.</summary>
    /// <typeparam name="TTarget">What the condition reads.</typeparam>
    /// <param name="target">Unread.</param>
    /// <returns><see langword="true"/>.</returns>
    public static bool Always<TTarget>(TTarget target) => true;

    /// <summary>How the key of a command in a table is written, for a label that names it.</summary>
    /// <typeparam name="TTarget">What the table's commands act on.</typeparam>
    /// <param name="commands">The table's commands.</param>
    /// <param name="name">The command's name; the first declared with it, where several share one.</param>
    /// <returns>Its stroke.</returns>
    /// <remarks>
    /// Read from the table rather than written into a label beside it, so a letter moved in the table moves on the
    /// button too. A name the table does not hold throws, which is the dialog failing to construct in every test that
    /// builds it.
    /// </remarks>
    public static string Stroke<TTarget>(IReadOnlyList<KeyCommand<TTarget>> commands, string name)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return commands.First(command => string.Equals(command.Name, name, StringComparison.Ordinal)).Stroke;
    }

    /// <summary>A button's label: a command's name and, in brackets, the key the table gives it.</summary>
    /// <typeparam name="TTarget">What the table's commands act on.</typeparam>
    /// <param name="commands">The table's commands.</param>
    /// <param name="name">The command's name.</param>
    /// <returns>The label, such as <c>Edit roadmap (Alt+e)</c>.</returns>
    /// <remarks>
    /// <b>One format for every button that names a key</b>, so a person reads a key the same way on every dialog. The
    /// bracketed form is the one the roadmap's buttons and its Enter button used; the proposal's buttons put the key
    /// after the name with no brackets, which read as part of the name.
    /// </remarks>
    public static string Label<TTarget>(IReadOnlyList<KeyCommand<TTarget>> commands, string name) =>
        Label(name, Stroke(commands, name));

    /// <summary>A button's label for a key that is not a command of its own, such as a list's Enter.</summary>
    /// <param name="name">What the button does.</param>
    /// <param name="stroke">The key, as it is written.</param>
    /// <returns>The label.</returns>
    /// <remarks>The same format as <see cref="Label{TTarget}"/>, which is the point of it being here.</remarks>
    public static string Label(string name, string stroke) => $"{name} ({stroke})";

    /// <summary>A command's work, when it is done at once.</summary>
    /// <typeparam name="TTarget">What it acts on.</typeparam>
    /// <param name="act">The work.</param>
    /// <returns>The work, as a command runs it.</returns>
    public static Func<TTarget, Task> Doing<TTarget>(Action<TTarget> act) =>
        target =>
        {
            act(target);

            return Task.CompletedTask;
        };
}

/// <summary>One thing a person can do: where, whether now, on which key, and what it runs.</summary>
/// <typeparam name="TTarget">What it acts on: the surface, or the dialog it belongs to.</typeparam>
/// <param name="Context">Where it can be live: a place, or a layer.</param>
/// <param name="Key">Its letter, pressed with Alt; <see langword="default"/> when it has a chord.</param>
/// <param name="Name">What it is called, and what a clash names it by.</param>
/// <param name="Run">What it does.</param>
/// <param name="When">Whether it is live now, in its place.</param>
/// <param name="Costs">What it costs, said in the menu.</param>
/// <param name="Chord">Its key when that is not Alt and a letter.</param>
/// <param name="Category">What it is about, when not what its place is about.</param>
/// <param name="Group">The run of related commands it belongs to.</param>
/// <param name="Order">Where it sits inside its run.</param>
/// <param name="Says">What it is called right now, when that changes with what it acts on.</param>
/// <param name="KeepsKey">Whether its key is taken, doing nothing, while it is not live.</param>
/// <remarks>
/// <para>
/// <b>Where and whether are two parameters on purpose.</b> <see cref="Context"/> is static and is all the startup
/// check reads; <see cref="When"/> is the selection, the seat and the message, read only once a key arrives or a line
/// is drawn. A command is therefore taken to claim its key everywhere it could ever be live, which is stricter than
/// what is live at any one moment and is the point: two commands that are never live together today are one changed
/// condition away from sharing a key.
/// </para>
/// <para>
/// <b><see cref="When"/> has no default</b>, so a command is live only where somebody said it is.
/// </para>
/// <para>
/// <b><see cref="KeepsKey"/> is for Escape on the surface.</b> Going back is live only where there is somewhere to go,
/// and an Escape passed on from the top reaches the toolkit, which ends the program on it. The status line tells people
/// to press Escape to go back, so one press too many is an ordinary mistake and has to cost nothing.
/// </para>
/// </remarks>
internal sealed record KeyCommand<TTarget>(
    KeyContext Context,
    char Key,
    string Name,
    Func<TTarget, Task> Run,
    Func<TTarget, bool> When,
    string? Costs = null,
    Key? Chord = null,
    ActionCategory? Category = null,
    ActionGroup? Group = null,
    int Order = 0,
    Func<TTarget, string>? Says = null,
    bool KeepsKey = false)
{
    /// <summary>How the key is written, wherever it is named.</summary>
    public string Stroke => Chord?.ToString() ?? Shell.Alt(Key);

    /// <summary>Whether a keystroke is this command's key, live or not.</summary>
    /// <param name="key">What arrived.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    /// <remarks>What <see cref="RegionAction.Answers"/> asks of a live action, asked of the declaration.</remarks>
    public bool Answers(Key key) => Chord is { } chord ? key == chord : Shell.Typed(key, Key);
}
