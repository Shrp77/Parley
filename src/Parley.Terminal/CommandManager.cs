using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

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
/// <para>
/// <b>Each dialog is a place of its own</b>, with no layer: a dialog is a session on top of the surface and takes every
/// key first, so nothing of the surface's is live in it.
/// </para>
/// <para>
/// <b>Every view the terminal opens as a session has one</b>, read-only ones included: the surface's panes, and each
/// dialog its own, declared by its <c>Places</c> and built in <see cref="CommandTables.BuildEvery"/>.
/// <c>KeysTakenBeforeTheTableTests</c> finds the sessions by reflection over this assembly and fails on one with no
/// place, or one it does not build and press, so a dialog added without a table fails a unit test.
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

    /// <summary>A dialog: one confirmed roadmap and its items.</summary>
    RoadmapDialog,

    /// <summary>A dialog: one proposed roadmap, to confirm, edit or decline.</summary>
    ProposalDialog,

    /// <summary>A dialog: a list of things to tick.</summary>
    TickDialog,

    /// <summary>A dialog: what one agent is responsible for.</summary>
    ResponsibilityDialog,

    /// <summary>A dialog: one line to type.</summary>
    TextDialog,

    /// <summary>A dialog: one choice from a list.</summary>
    ChoiceDialog,

    /// <summary>A dialog: several fields to fill in.</summary>
    FormDialog,

    /// <summary>A dialog: walking the machine's folders to a repository, or to a folder of them.</summary>
    BrowsingDialog,

    /// <summary>A dialog: the broker's log, followed as it is written.</summary>
    LogDialog,

    /// <summary>A dialog: a project's health, read-only.</summary>
    HealthDialog,

    /// <summary>A dialog: the last thing the message line said, whole, read-only.</summary>
    SayingDialog,
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
/// Tab, the quit chord and F10 move focus, leave or open the menus; a list moves its cursor; a box edits. None is an
/// action with a label, but a command bound to one would be named on screen and never run. Declaring them here is what
/// lets the check see them.
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
    /// to a list claims it at once. A copy would be a second statement of the toolkit, right until it was not.
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

        return [.. view.KeyBindings.GetBindings()
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

/// <summary>Helpers for writing a command table.</summary>
internal static class KeyCommand
{
    /// <summary>A condition that always holds, for a command live wherever its place is.</summary>
    /// <typeparam name="TTarget">What the condition reads.</typeparam>
    /// <param name="target">Unread.</param>
    /// <returns><see langword="true"/>.</returns>
    public static bool Always<TTarget>(TTarget target) => true;

    /// <summary>How the key of a command in a table is written, for a label that names it.</summary>
    /// <typeparam name="TTarget">What the table's commands act on.</typeparam>
    /// <param name="commands">The table.</param>
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
    Func<TTarget, string>? Says = null)
{
    /// <summary>How the key is written, wherever it is named.</summary>
    public string Stroke => Chord?.ToString() ?? Shell.Alt(Key);
}

/// <summary>A live command as one place offers it, and the place or layer it was declared in.</summary>
/// <param name="From">Where it was declared: the place itself, or one of its layers.</param>
/// <param name="Action">What it offers now.</param>
internal sealed record OfferedAction(KeyContext From, RegionAction Action);

/// <summary>
/// Every command one surface offers, checked once for clashes and then read by everything that names or answers a key.
/// </summary>
/// <typeparam name="TTarget">What the commands act on: the surface, or one dialog.</typeparam>
/// <remarks>
/// <para>
/// <b>One table that dispatch, the status line and the menus all read</b>, through <see cref="Offered"/>, so none of
/// them can disagree with what a key does. Before it there were three lists searched in order, and a pane's letter
/// silently beat the message line's: the roadmaps pane and the message line both took Alt+e, the status line named
/// both, and one never ran.
/// </para>
/// <para>
/// <b>A clash refuses to construct</b>, naming both claims and the place. <see cref="CommandTables"/> builds every
/// table before the terminal takes the console, so it does not start with one, and <c>CommandTableTests</c> builds them
/// too, so a push fails before anybody runs the tool.
/// </para>
/// </remarks>
internal sealed class CommandManager<TTarget>
{
    private readonly IReadOnlyList<KeyPlace> _places;
    private readonly IReadOnlyList<KeyCommand<TTarget>> _commands;
    private readonly IReadOnlyList<ReservedKey> _reserved;

    /// <summary>Checks the table and keeps it.</summary>
    /// <param name="places">Every place and the layers live in it.</param>
    /// <param name="commands">Every command.</param>
    /// <param name="reserved">The keys taken before any command.</param>
    /// <exception cref="InvalidOperationException">Two claims share a key in one place, or a command can never be live, or a key is not one a person can press as an action.</exception>
    public CommandManager(
        IReadOnlyList<KeyPlace> places, IReadOnlyList<KeyCommand<TTarget>> commands, IReadOnlyList<ReservedKey> reserved)
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
    public IReadOnlyList<KeyCommand<TTarget>> Commands => _commands;

    /// <summary>Every reserved key, as declared.</summary>
    public IReadOnlyList<ReservedKey> Reserved => _reserved;

    /// <summary>The commands declared in one place or layer that are live now, alone.</summary>
    /// <param name="context">The place or layer.</param>
    /// <param name="target">What the conditions read.</param>
    /// <returns>The actions, in the order they were declared.</returns>
    /// <remarks>For a list of one place's own commands, such as a row's menu. What a place offers is <see cref="Offered"/>.</remarks>
    public IReadOnlyList<RegionAction> Live(KeyContext context, TTarget target) =>
        [.. _commands.Where(command => command.Context == context && command.When(target)).Select(command => Offer(command, target))];

    /// <summary>Everything live in a place: its own commands, then each layer's, in the order the place lists them.</summary>
    /// <param name="context">The place the cursor is in.</param>
    /// <param name="target">What the conditions read.</param>
    /// <returns>The actions, each with where it was declared.</returns>
    /// <remarks>
    /// <b>The one read behind a key, the status line and the menus</b>, so a layer a place does not list is neither
    /// named nor answered there. Reading the layers one by one is how the screen came to name what a key did not run.
    /// </remarks>
    public IReadOnlyList<OfferedAction> Offered(KeyContext context, TTarget target) =>
        [.. Layered(context).SelectMany(from => _commands
            .Where(command => command.Context == from && command.When(target))
            .Select(command => new OfferedAction(from, Offer(command, target))))];

    /// <summary>The live command a key runs in a place, looking in the place and every layer live in it.</summary>
    /// <param name="context">The place the cursor is in.</param>
    /// <param name="target">What the conditions read.</param>
    /// <param name="key">What arrived.</param>
    /// <returns>The action, or <see langword="null"/> when the key runs nothing here.</returns>
    /// <remarks>
    /// Order does not matter, and that is what the check bought: at most one command in a place and its layers claims
    /// any key, so there is no precedence for a reader to know.
    /// </remarks>
    public RegionAction? Answering(KeyContext context, TTarget target, Key key) =>
        Offered(context, target).Select(offered => offered.Action).FirstOrDefault(action => action.Answers(key));

    /// <summary>Whether a key is claimed in a place, by a command or a reservation there or in a layer live there.</summary>
    /// <param name="context">The place.</param>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when something in the table holds it there.</returns>
    public bool Claims(KeyContext context, Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var where = Layered(context);
        var identity = Identity(key);

        return _commands.Any(command => where.Contains(command.Context) && Identity(command) == identity)
               || _reserved.Any(reserved => where.Contains(reserved.Context) && Identity(reserved.Key) == identity);
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

    /// <summary>How a command's key is compared.</summary>
    /// <param name="command">The command.</param>
    /// <returns>Its identity.</returns>
    private static string Identity(KeyCommand<TTarget> command) =>
        command.Chord is { } chord ? Identity(chord) : Identity(command.Key);

    /// <summary>The live action a command offers.</summary>
    /// <param name="command">The command.</param>
    /// <param name="target">What it acts on.</param>
    /// <returns>The action.</returns>
    private static RegionAction Offer(KeyCommand<TTarget> command, TTarget target) =>
        new(
            command.Key,
            command.Says?.Invoke(target) ?? command.Name,
            () => command.Run(target),
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
                throw Refusal(
                    "a command is registered in a place nothing declares",
                    $"'{command.Name}' is registered in {command.Context}, which no place on the terminal is or layers.");
            }

            if ((command.Key == default) == (command.Chord is null))
            {
                throw Refusal(
                    "a command has both a letter and a chord, or neither",
                    $"'{command.Name}' needs exactly one of a letter, pressed with Alt, or a chord.");
            }

            // A plain letter or digit is never an action: typing would run it.
            if (command.Chord is { } chord && !chord.IsAlt && !chord.IsCtrl && chord.AsRune.Value is var typed
                && typed < 128 && char.IsAsciiLetterOrDigit((char)typed))
            {
                throw Refusal(
                    "a command is bound to a plain character",
                    $"'{command.Name}' is bound to {chord}, a plain character; an action takes a modifier.");
            }
        }

        // Every clash, not the first: one key moved can collide in several places, and a fix made one message at a
        // time is a fix that moves the clash to the next place.
        var clashes = new List<(bool Reserved, string Said)>();

        foreach (var place in _places)
        {
            var where = Layered(place.Context);

            var claims = _commands
                .Where(command => where.Contains(command.Context))
                .Select(command => (
                    Key: Identity(command),
                    Reserved: false,
                    Said: $"'{command.Name}' ({command.Stroke}, {command.Context})"))
                .Concat(_reserved
                    .Where(reserved => where.Contains(reserved.Context))
                    .Select(reserved => (
                        Key: Identity(reserved.Key),
                        Reserved: true,
                        Said: $"'{reserved.Name}' ({reserved.Key}, reserved in {reserved.Context})")));

            // **A clash needs a command in it.** Two reservations on one key are two views that each take it when they
            // have focus, which costs nobody a command: a dialog's list and its paragraph both move on the arrows.
            clashes.AddRange(claims
                .GroupBy(claim => claim.Key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1 && group.Any(claim => !claim.Reserved))
                .Select(clash => (
                    Reserved: clash.Any(claim => claim.Reserved),
                    Said: $"{clash.Key} is claimed more than once in {place.Context}: "
                        + string.Join(" and ", clash.Select(claim => claim.Said)) + ".")));
        }

        if (clashes.Count > 0)
        {
            // Said as what the clashes are: a command on a key a view takes first is not two commands on one key.
            var summary = (clashes.Exists(clash => !clash.Reserved), clashes.Exists(clash => clash.Reserved)) switch
            {
                (true, true) => "two of its commands share a key, and a command is on a key taken before any command",
                (true, false) => "two of its commands share a key",
                _ => "a command is on a key taken before any command",
            };

            throw Refusal(
                summary,
                string.Join(Environment.NewLine, clashes.Select(clash => clash.Said)) + Environment.NewLine
                + "Give each command its own key in every place it can be live.");
        }
    }

    /// <summary>A refusal whose first line says what kind it is, and the rest which commands and where.</summary>
    /// <param name="summary">What is wrong, in words that read after "cannot start:".</param>
    /// <param name="detail">Every claim that is wrong.</param>
    /// <returns>The exception.</returns>
    /// <remarks>
    /// The first line is what <see cref="CommandTables.Refused"/> leads with, so it has to be true of every refusal: it
    /// once said two commands shared a key for a command bound to a plain character.
    /// </remarks>
    private static InvalidOperationException Refusal(string summary, string detail) =>
        new(summary + "." + Environment.NewLine + detail);
}

/// <summary>Every command table the terminal has, built together so a clash in any of them is found before it starts.</summary>
/// <remarks>
/// <para>
/// <b>Built before the toolkit takes the console</b>, from <c>TerminalCommand.RunAsync</c>, so a refusal is a plain
/// message on standard error and a non-zero exit, never a stack trace over a screen the toolkit had already cleared.
/// The surface and each dialog build their own table again when they are made; by then this has proved they can.
/// </para>
/// <para>
/// A table added and left out of <see cref="BuildEvery"/> is still checked whenever its dialog opens, but only then;
/// <c>CommandTableTests</c> counts the places to catch that, and <c>KeysTakenBeforeTheTableTests</c> fails on a dialog
/// whose place is not built here.
/// </para>
/// </remarks>
internal static class CommandTables
{
    /// <summary>What the terminal exits with when a table refuses: a fault in this build, not in how it was run.</summary>
    internal const int Refusal = 70;

    /// <summary>Builds every table, throwing the first refusal.</summary>
    /// <returns>The places every table declares, for a test to count.</returns>
    internal static IReadOnlyList<KeyContext> BuildEvery() =>
    [
        .. new CommandManager<Shell>(Shell.Places, Shell.Registered, Shell.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<PlanView>(PlanView.Places, PlanView.Registered, PlanView.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<ProposalView>(ProposalView.Places, ProposalView.Registered, ProposalView.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<TickDialog>(TickDialog.Places, TickDialog.Registered, TickDialog.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<ResponsibilityDialog>(ResponsibilityDialog.Places, ResponsibilityDialog.Registered, ResponsibilityDialog.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<TextDialog>(TextDialog.Places, TextDialog.Registered, TextDialog.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<ChoiceDialog>(ChoiceDialog.Places, ChoiceDialog.Registered, ChoiceDialog.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<FormDialog>(FormDialog.Places, FormDialog.Registered, FormDialog.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<Browsing>(Browsing.Places, Browsing.Registered, Browsing.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<LogView>(LogView.Places, LogView.Registered, LogView.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<Health>(Health.Places, Health.Registered, Health.Reserved).Places.Select(place => place.Context),
        .. new CommandManager<Saying>(Saying.Places, Saying.Registered, Saying.Reserved).Places.Select(place => place.Context),
    ];

    /// <summary>Builds every table, and says plainly why the terminal cannot start when one refuses.</summary>
    /// <param name="error">Where to say it.</param>
    /// <param name="build">What to build; every table unless a test says otherwise.</param>
    /// <returns><see langword="null"/> when every table was built, or the exit code.</returns>
    /// <remarks>
    /// The refusal names every clash and how to fix it, which is all a person can act on; a stack trace would bury that
    /// under frames of the toolkit's. Only <see cref="InvalidOperationException"/> is caught, which is what a table
    /// throws; anything else is a different fault and is left to be one.
    /// </remarks>
    internal static int? Refused(TextWriter error, Func<object>? build = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            _ = (build ?? BuildEvery)();

            return null;
        }
        catch (InvalidOperationException refused)
        {
            // A table's refusal leads with what kind it is, which is how this line ends.
            var lines = refused.Message.Split(Environment.NewLine, 2);

            error.WriteLine("parley-terminal cannot start: " + lines[0]);

            if (lines.Length > 1)
            {
                error.WriteLine(lines[1]);
            }

            return Refusal;
        }
    }
}
