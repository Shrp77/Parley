using System.Text;
using Terminal.Gui.Input;

namespace Parley.Terminal;

/// <summary>A live command as one place offers it, and the place or layer it was declared in.</summary>
/// <param name="From">Where it was declared: the place itself, or one of its layers.</param>
/// <param name="Action">What it offers now.</param>
/// <remarks>The place travels with the action so a menu can group by it without asking the table again.</remarks>
internal sealed record OfferedAction(KeyContext From, RegionAction Action);

/// <summary>
/// Every command one surface offers, and every key it takes, checked once for clashes and then read by everything that
/// names or answers a key.
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
/// <b><see cref="Take"/> is the only way a key is answered.</b> The surface's and every dialog's key handler is that one
/// call and nothing else, so a key nobody declared here cannot be taken by either. Each time the handlers compared keys
/// of their own, review found a way to take one the table had never seen.
/// </para>
/// <para>
/// <b>A clash refuses to construct</b>, naming every claim and the place. <see cref="CommandTables"/> builds every
/// table before the terminal takes the console, so it does not start with one, and <c>CommandTableTests</c> builds them
/// too, so a push fails before anybody runs the tool.
/// </para>
/// </remarks>
internal sealed class CommandManager<TTarget>
{
    private readonly IReadOnlyList<KeyPlace> _places;
    private readonly IReadOnlyList<KeyCommand<TTarget>> _commands;
    private readonly IReadOnlyList<ReservedKey> _reserved;
    private readonly IReadOnlyList<KeyHandler<TTarget>> _handlers;

    /// <summary>Checks the table and keeps it.</summary>
    /// <param name="places">Every place and the layers live in it.</param>
    /// <param name="commands">Every command.</param>
    /// <param name="reserved">The keys views take before any command.</param>
    /// <param name="handlers">The keys the surface or the dialog takes itself, with no label.</param>
    /// <exception cref="InvalidOperationException">Two claims share a key in one place, or a command or handler can never be live, or a key is not one a person can press as an action.</exception>
    public CommandManager(
        IReadOnlyList<KeyPlace> places,
        IReadOnlyList<KeyCommand<TTarget>> commands,
        IReadOnlyList<ReservedKey> reserved,
        IReadOnlyList<KeyHandler<TTarget>>? handlers = null)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(reserved);

        _places = places;
        _commands = commands;
        _reserved = reserved;
        _handlers = handlers ?? [];

        Check();
    }

    /// <summary>Every place, as declared.</summary>
    public IReadOnlyList<KeyPlace> Places => _places;

    /// <summary>Every command, as declared.</summary>
    public IReadOnlyList<KeyCommand<TTarget>> Commands => _commands;

    /// <summary>Every reserved key, as declared.</summary>
    public IReadOnlyList<ReservedKey> Reserved => _reserved;

    /// <summary>Every key taken with no label, as declared.</summary>
    public IReadOnlyList<KeyHandler<TTarget>> Handlers => _handlers;

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

    /// <summary>Answers a key in a place: runs the live command on it, or the handler that takes it.</summary>
    /// <param name="context">The place the cursor is in.</param>
    /// <param name="target">What the commands and handlers act on.</param>
    /// <param name="key">What arrived.</param>
    /// <returns>Whether the key was taken; a key that was not is left to the toolkit.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every key handler is this call.</b> Nothing else on the surface compares a key, so what a key does is what this
    /// table declares, and <c>KeysTakenBeforeTheTableTests</c> holds every handler in the assembly to it by reflection.
    /// </para>
    /// <para>
    /// The command is asked before the handler only to read the same way every time: the check refused any key a
    /// command and a handler both claim in one place, so at most one of them answers.
    /// </para>
    /// </remarks>
    public bool Take(KeyContext context, TTarget target, Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Answering(context, target, key) is { } answering)
        {
            _ = answering.Run();

            return true;
        }

        var where = Layered(context);

        if (_handlers.FirstOrDefault(handler => where.Contains(handler.Context) && key == handler.Key) is { } taking)
        {
            return taking.Take(target);
        }

        return _commands.Any(command => command.KeepsKey && where.Contains(command.Context) && command.Answers(key));
    }

    /// <summary>A button's label for one of this table's commands: its name and its key.</summary>
    /// <param name="name">The command's name.</param>
    /// <returns>The label.</returns>
    /// <remarks>Read from the table, so a key moved here moves on the button.</remarks>
    public string Label(string name) => KeyCommand.Label(_commands, name);

    /// <summary>Whether a key is claimed in a place, by a command, a handler or a reservation there or in a layer live there.</summary>
    /// <param name="context">The place.</param>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when something in the table holds it there.</returns>
    public bool Claims(KeyContext context, Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var where = Layered(context);
        var identity = Identity(key);

        return Names(context, key)
               || _handlers.Any(handler => where.Contains(handler.Context) && Identity(handler.Key) == identity)
               || _reserved.Any(reserved => where.Contains(reserved.Context) && Identity(reserved.Key) == identity);
    }

    /// <summary>Whether a command is on a key in a place, there or in a layer live there, live now or not.</summary>
    /// <param name="context">The place.</param>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the key is named on screen as a command somewhere it can be live in the place.</returns>
    /// <remarks>
    /// Apart from <see cref="Claims"/> because a key a view takes before any command is claimed by a command only on
    /// purpose: the command never runs from the key, so the view has to do what the command says.
    /// </remarks>
    public bool Names(KeyContext context, Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var where = Layered(context);
        var identity = Identity(key);

        return _commands.Any(command => where.Contains(command.Context) && Identity(command) == identity);
    }

    /// <summary>Whether a key is a printable character other than a space, pressed with neither Alt nor Ctrl.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when typing it would type a character.</returns>
    /// <remarks>
    /// No command or handler may be one: typing it would run whatever it was bound to. Space is the one printable key that
    /// may be an action unmodified, and only where nothing types; <see cref="ReservedKey.Typing"/> holds it there. A named
    /// key, such as Enter, Escape, a function key or a page key, types nothing and may be an action where a place says so.
    /// </remarks>
    internal static bool Printable(Key key) =>
        !key.IsAlt && !key.IsCtrl && key.AsRune is { Value: > 32 } rune && !Rune.IsControl(rune);

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

    /// <summary>What a claim on a key is, for deciding what a shared key costs.</summary>
    private enum Claim
    {
        /// <summary>A command, named on screen.</summary>
        Command,

        /// <summary>A key the surface or the dialog takes itself.</summary>
        Handler,

        /// <summary>A key a view takes while it has focus.</summary>
        Reserved,
    }

    /// <summary>Refuses a table in which a key is claimed twice in one place, or a claim can never be reached.</summary>
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

            // A plain character is never an action: typing would run it.
            if (command.Chord is { } chord && Printable(chord))
            {
                throw Refusal(
                    "a command is bound to a plain character",
                    $"'{command.Name}' is bound to {chord}, a plain character; an action takes a modifier.");
            }
        }

        foreach (var handler in _handlers)
        {
            if (!reachable.Contains(handler.Context))
            {
                throw Refusal(
                    "a key the surface takes is in a place nothing declares",
                    $"'{handler.Name}' is taken in {handler.Context}, which no place on the terminal is or layers.");
            }

            if (Printable(handler.Key))
            {
                throw Refusal(
                    "a key the surface takes is a plain character",
                    $"'{handler.Name}' is taken on {handler.Key}, a plain character; typing it would run it.");
            }
        }

        // Every clash, not the first: one key moved can collide in several places, and a fix made one message at a
        // time is a fix that moves the clash to the next place.
        var clashes = new List<(string Kind, string Said)>();

        foreach (var place in _places)
        {
            var where = Layered(place.Context);

            var claims = _commands
                .Where(command => where.Contains(command.Context))
                .Select(command => (
                    Key: Identity(command),
                    Claim: Claim.Command,
                    Said: $"'{command.Name}' ({command.Stroke}, {command.Context})"))
                .Concat(_handlers
                    .Where(handler => where.Contains(handler.Context))
                    .Select(handler => (
                        Key: Identity(handler.Key),
                        Claim: Claim.Handler,
                        Said: $"'{handler.Name}' ({handler.Key}, taken in {handler.Context})")))
                .Concat(_reserved
                    .Where(reserved => where.Contains(reserved.Context))
                    .Select(reserved => (
                        Key: Identity(reserved.Key),
                        Claim: Claim.Reserved,
                        Said: $"'{reserved.Name}' ({reserved.Key}, reserved in {reserved.Context})")));

            foreach (var group in claims.GroupBy(claim => claim.Key, StringComparer.Ordinal))
            {
                var commands = group.Count(claim => claim.Claim == Claim.Command);
                var handlers = group.Count(claim => claim.Claim == Claim.Handler);
                var others = group.Count() - commands;

                // **A clash needs a command or two handlers in it.** Two reservations on one key are two views that each
                // take it when they have focus, which costs nobody a command: a dialog's list and its paragraph both
                // move on the arrows. A handler beside a reservation is the same: the view takes the key while it has
                // focus, and what it declines reaches the handler.
                var kind = (commands, others, handlers) switch
                {
                    (> 0, > 0, _) => TakenFirst,
                    (> 1, _, _) => Shared,
                    (_, _, > 1) => TakenTwice,
                    _ => null,
                };

                if (kind is not null)
                {
                    clashes.Add((
                        kind,
                        $"{group.Key} is claimed more than once in {place.Context}: "
                        + string.Join(" and ", group.Select(claim => claim.Said)) + "."));
                }
            }
        }

        if (clashes.Count > 0)
        {
            // Said as what the clashes are: a command on a key a view takes first is not two commands on one key.
            var kinds = new[] { Shared, TakenFirst, TakenTwice }.Where(kind => clashes.Exists(clash => clash.Kind == kind)).ToList();
            var summary = kinds.Count == 1 ? kinds[0] : string.Join(", ", kinds.SkipLast(1)) + ", and " + kinds[^1];

            throw Refusal(
                summary,
                string.Join(Environment.NewLine, clashes.Select(clash => clash.Said)) + Environment.NewLine
                + "Give each command its own key in every place it can be live.");
        }
    }

    /// <summary>What a clash of two commands is called.</summary>
    private const string Shared = "two of its commands share a key";

    /// <summary>What a command on a key something else takes is called.</summary>
    private const string TakenFirst = "a command is on a key taken before any command";

    /// <summary>What two handlers on one key are called.</summary>
    private const string TakenTwice = "two keys the surface takes itself share a key";

    /// <summary>A refusal whose first line says what kind it is, and the rest which claims and where.</summary>
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
