using Terminal.Gui.Input;

namespace Parley.Terminal;

/// <summary>Where on the surface a command can be live.</summary>
/// <remarks>
/// <para>
/// <b>A place is named, never inferred from what is on screen.</b> The check that no two commands share a key reads
/// only these, so it can run before anything is drawn: a clash that showed only once a roadmap was confirmed and a
/// message was cut short is exactly the one nobody stumbles on until a person does.
/// </para>
/// <para>
/// <b>Two of them are layers rather than places</b>: <see cref="MessageLine"/> and <see cref="Everywhere"/> belong to no
/// pane and are live on top of every one, which is why a layer's key is checked against every place it is live in.
/// </para>
/// </remarks>
internal enum KeyContext
{
    /// <summary>The projects list.</summary>
    Projects,

    /// <summary>A project's agents.</summary>
    Agents,

    /// <summary>A project's conversations.</summary>
    Conversations,

    /// <summary>A conversation's transcript.</summary>
    Transcript,

    /// <summary>The column beside a transcript saying who a line goes to.</summary>
    Room,

    /// <summary>The box a person writes in.</summary>
    Line,

    /// <summary>A project's confirmed roadmaps.</summary>
    Plans,

    /// <summary>A project's proposed roadmaps.</summary>
    Roadmaps,

    /// <summary>A project's settings.</summary>
    Configuration,

    /// <summary>A pane no command belongs to.</summary>
    Elsewhere,

    /// <summary>A layer: what can be done about the message line.</summary>
    MessageLine,

    /// <summary>A layer: what can be done wherever the cursor is.</summary>
    Everywhere,
}

/// <summary>A place on the surface, and the layers that are live in it as well.</summary>
/// <param name="Context">The place.</param>
/// <param name="Layers">The layers whose commands are live there too.</param>
internal sealed record KeyPlace(KeyContext Context, IReadOnlyList<KeyContext> Layers);

/// <summary>A key the surface takes before any command, in one place or a layer.</summary>
/// <param name="Context">Where it is taken.</param>
/// <param name="Key">The key.</param>
/// <param name="Name">What it does, for the message that names a clash.</param>
/// <remarks>
/// Tab, the quit chord and F10 move focus, leave or open the menus; they are not actions with a label, but a command
/// bound to one would be named on screen and never run. Declaring them here is what lets the check see them.
/// </remarks>
internal sealed record ReservedKey(KeyContext Context, Key Key, string Name);

/// <summary>One thing a person can do from the terminal: where, whether now, on which key, and what it runs.</summary>
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
/// </remarks>
internal sealed record KeyCommand(
    KeyContext Context,
    char Key,
    string Name,
    Func<Shell, Task> Run,
    Func<Shell, bool> When,
    string? Costs = null,
    Key? Chord = null,
    ActionCategory? Category = null,
    ActionGroup? Group = null,
    int Order = 0,
    Func<Shell, string>? Says = null)
{
    /// <summary>A condition that always holds, for a command live wherever its place is.</summary>
    public static Func<Shell, bool> Always { get; } = _ => true;

    /// <summary>How the key is written, wherever it is named.</summary>
    public string Stroke => Chord?.ToString() ?? Shell.Alt(Key);
}

/// <summary>Every command the terminal offers, checked once for clashes and then read by everything that names a key.</summary>
/// <remarks>
/// <para>
/// <b>One table that dispatch, the status line and the menus all read</b>, so none of them can disagree with what a
/// key does. Before it there were three lists searched in order, and a pane's letter silently beat the message line's:
/// the roadmaps pane and the message line both took Alt+e, the status line named both, and one never ran.
/// </para>
/// <para>
/// <b>A clash refuses to construct</b>, naming both commands and the place, so the terminal does not start with one.
/// <c>CommandTableTests</c> constructs the real table, so a push fails before anybody runs the tool.
/// </para>
/// </remarks>
internal sealed class CommandManager
{
    private readonly IReadOnlyList<KeyPlace> _places;
    private readonly IReadOnlyList<KeyCommand> _commands;
    private readonly IReadOnlyList<ReservedKey> _reserved;

    /// <summary>Checks the table and keeps it.</summary>
    /// <param name="places">Every place and the layers live in it.</param>
    /// <param name="commands">Every command.</param>
    /// <param name="reserved">The keys the surface takes before any command.</param>
    /// <exception cref="InvalidOperationException">Two claims share a key in one place, or a command can never be live, or a key is not one a person can press as an action.</exception>
    public CommandManager(
        IReadOnlyList<KeyPlace> places, IReadOnlyList<KeyCommand> commands, IReadOnlyList<ReservedKey> reserved)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(reserved);

        _places = places;
        _commands = commands;
        _reserved = reserved;

        Check();
    }

    /// <summary>Every place, as declared.</summary>
    public IReadOnlyList<KeyPlace> Places => _places;

    /// <summary>Every command, as declared.</summary>
    public IReadOnlyList<KeyCommand> Commands => _commands;

    /// <summary>Every reserved key, as declared.</summary>
    public IReadOnlyList<ReservedKey> Reserved => _reserved;

    /// <summary>The commands declared in one place or layer that are live now, as the screen offers them.</summary>
    /// <param name="context">The place or layer, alone.</param>
    /// <param name="shell">What the conditions read.</param>
    /// <returns>The actions, in the order they were declared.</returns>
    public IReadOnlyList<RegionAction> Live(KeyContext context, Shell shell) =>
        [.. _commands.Where(command => command.Context == context && command.When(shell)).Select(command => Offer(command, shell))];

    /// <summary>The live command a key runs in a place, looking in the place and every layer live in it.</summary>
    /// <param name="context">The place the cursor is in.</param>
    /// <param name="shell">What the conditions read.</param>
    /// <param name="key">What arrived.</param>
    /// <returns>The action, or <see langword="null"/> when the key runs nothing here.</returns>
    /// <remarks>
    /// Order does not matter, and that is what the check bought: at most one command in a place and its layers claims
    /// any key, so there is no precedence for a reader to know.
    /// </remarks>
    public RegionAction? Answering(KeyContext context, Shell shell, Key key)
    {
        var where = Layered(context);

        return _commands
            .Where(command => where.Contains(command.Context) && command.When(shell))
            .Select(command => Offer(command, shell))
            .FirstOrDefault(action => action.Answers(key));
    }

    /// <summary>How a key is compared, so Alt+E and Alt+e, or a letter and its Alt chord, are one key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its identity.</returns>
    private static string Identity(Key key) =>
        key.IsAlt && !key.IsCtrl && key.NoAlt.AsRune is { Value: > 0 } rune
            ? Identity(rune.ToString()[0])
            : key.ToString();

    /// <summary>How a letter pressed with Alt is compared.</summary>
    /// <param name="letter">The letter.</param>
    /// <returns>Its identity.</returns>
    private static string Identity(char letter) => Shell.Alt(char.ToLowerInvariant(letter));

    /// <summary>The live action a command offers.</summary>
    /// <param name="command">The command.</param>
    /// <param name="shell">What it acts on.</param>
    /// <returns>The action.</returns>
    private static RegionAction Offer(KeyCommand command, Shell shell) =>
        new(
            command.Key,
            command.Says?.Invoke(shell) ?? command.Name,
            () => command.Run(shell),
            command.Costs,
            command.Chord,
            command.Category,
            command.Group,
            command.Order);

    /// <summary>A place and every layer live in it.</summary>
    /// <param name="context">The place.</param>
    /// <returns>The contexts whose commands are live there.</returns>
    private IReadOnlyList<KeyContext> Layered(KeyContext context) =>
        _places.FirstOrDefault(place => place.Context == context) is { } found
            ? [found.Context, .. found.Layers]
            : [context];

    /// <summary>Refuses a table in which a key is claimed twice in one place, or a command can never be reached.</summary>
    private void Check()
    {
        var reachable = _places.SelectMany(place => (IEnumerable<KeyContext>)[place.Context, .. place.Layers]).ToHashSet();

        foreach (var command in _commands)
        {
            if (!reachable.Contains(command.Context))
            {
                throw new InvalidOperationException(
                    $"'{command.Name}' is registered in {command.Context}, which no place on the terminal is or layers.");
            }

            if ((command.Key == default) == (command.Chord is null))
            {
                throw new InvalidOperationException(
                    $"'{command.Name}' needs exactly one of a letter, pressed with Alt, or a chord.");
            }

            // A plain letter or digit is never an action: typing would run it.
            if (command.Chord is { } chord && !chord.IsAlt && !chord.IsCtrl && chord.AsRune.Value is var typed
                && typed < 128 && char.IsAsciiLetterOrDigit((char)typed))
            {
                throw new InvalidOperationException(
                    $"'{command.Name}' is bound to {chord}, a plain character; an action takes a modifier.");
            }
        }

        // Every clash, not the first: one key moved can collide in several places, and a fix made one message at a
        // time is a fix that moves the clash to the next place.
        var clashes = new List<string>();

        foreach (var place in _places)
        {
            var where = Layered(place.Context);

            var claims = _commands
                .Where(command => where.Contains(command.Context))
                .Select(command => (
                    Key: command.Chord is { } chord ? Identity(chord) : Identity(command.Key),
                    Said: $"'{command.Name}' ({command.Stroke}, {command.Context})"))
                .Concat(_reserved
                    .Where(reserved => where.Contains(reserved.Context))
                    .Select(reserved => (
                        Key: Identity(reserved.Key),
                        Said: $"'{reserved.Name}' ({reserved.Key}, reserved in {reserved.Context})")));

            clashes.AddRange(claims
                .GroupBy(claim => claim.Key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(clash => $"{clash.Key} is claimed more than once in {place.Context}: "
                    + string.Join(" and ", clash.Select(claim => claim.Said)) + "."));
        }

        if (clashes.Count > 0)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, clashes) + Environment.NewLine + "Give each command its own key in every place it can be live.");
        }
    }
}
