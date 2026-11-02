using System.Reflection;
using System.Reflection.Emit;
using Parley.Client;
using Parley.Client.Roadmaps;
using PairState = Parley.Client.Seating.PairState;
using Turn = Parley.Client.Seating.Turn;
using Parley.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every key the terminal takes is taken through a command table, and claimed there in the place that takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three rounds of review each found a key taken outside the table</b> that the tests could not see, because they
/// guarded hand-written key handlers by reading their source: a comparison written another way, then a helper the scan
/// accepted by name and never read. So nothing here reads source. Every key handler in the assembly is found by
/// reflection and must be one of the two that only ask a table; every subscription to a key event is found in the
/// compiled code and must be one of the few in <see cref="KeyRouting"/>; every binding, and every handler for accepting,
/// activating or a hot key, added in code must be added while a pressed place is built, and none on the application; and
/// every view that can be opened as a session must build a table.
/// </para>
/// <para>
/// <b>And the places are pressed, not assumed.</b> Review found the room and the conversations taking Enter through
/// their lists' own accept, so a command added on Enter there was named on the status line and never ran. Each pane, as
/// a real surface builds and wires it, and each dialog is built in memory and every key it could take is put to its
/// views: what they bind as keys and as hot keys, what their accept and activate handlers answer, and what their own key
/// handlers take when it is pressed. Every key found must be claimed in that place; one a command names must be one the
/// view hands to the table, or a decision in <see cref="TakenAsNamed"/>; and a plain character taken by a view that does
/// not type is a failure whatever claims it.
/// </para>
/// </remarks>
[Unit]
public sealed class KeysTakenBeforeTheTableTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    /// <summary>A view's accept and activate handlers, read because the toolkit offers no other way to ask.</summary>
    private static readonly FieldInfo Accepting = typeof(View).GetField("Accepting", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo Activating = typeof(View).GetField("Activating", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly CommandManager<Shell> Surface = CommandTables.Of<Shell>();

    private static readonly Assembly TheTerminal = typeof(Shell).Assembly;

    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>A type's own instance constructors, so a type initialiser is never called as one.</summary>
    private const BindingFlags Made = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Every pane and dialog, built as the terminal builds it, and what its place claims.</summary>
    /// <returns>Each place, a view standing in it, whether the place claims a key, and whether a command there names it.</returns>
    /// <remarks>
    /// <para>
    /// <b>The panes are a real surface's</b>, built by <see cref="Parley.Terminal.Shell"/> and wired by it, so a binding or
    /// an accept handler the surface adds to a pane's view is pressed with the pane. Review found the panes built alone,
    /// as each region builds itself, and a binding the surface added to the agents list passed every test.
    /// </para>
    /// <para>
    /// <b>The surface's queued work is waited for</b> after every key pressed at a pane, so a read a key queued has
    /// landed, and done whatever it does, before the next key is pressed.
    /// </para>
    /// <para>
    /// <b>Every list is given rows</b>, so a command bound to a list can act when it is pressed: an empty list answers
    /// moving down with nothing, and a key bound to it there looks untaken.
    /// </para>
    /// <para>
    /// Each view is disposed of once the caller has moved past it, the panes with their surface, so a caller never
    /// disposes of what it is handed.
    /// </para>
    /// </remarks>
    private static IEnumerable<(KeyContext Place, View View, Func<Key, bool> Claims, Func<Key, bool> Names)> Everywhere()
    {
        (Func<Key, bool>, Func<Key, bool>) Shell(KeyContext place) => (key => Surface.Claims(place, key), key => Surface.Names(place, key));

        using (var directory = new BrokerDirectory([], _ => throw new InvalidOperationException("no broker is asked")))
        using (var shell = new Shell(directory))
        {
            Shown(shell);
            Settle(shell);

            (KeyContext, View)[] panes =
            [
                (KeyContext.Projects, shell.Projects),
                (KeyContext.Agents, shell.Agents),
                (KeyContext.Conversations, shell.Conversations),
                (KeyContext.Transcript, shell.Transcript),
                (KeyContext.Room, shell.Room),
                (KeyContext.Line, shell.Line),
                (KeyContext.Plans, shell.Plans),
                (KeyContext.Roadmaps, shell.Roadmaps),
                (KeyContext.Configuration, shell.Configuration),
            ];

            foreach (var (place, pane) in panes)
            {
                var (claims, names) = Shell(place);

                yield return (place, Filled(pane), claims, names);
            }
        }

        foreach (var (place, build, (claims, names)) in Dialogs())
        {
            using var dialog = build();

            if (dialog is null)
            {
                continue;
            }

            // Initialised as opening it initialises it, so what a dialog does when it opens is pressed too.
            dialog.BeginInit();
            dialog.EndInit();

            yield return (place, Filled(dialog), claims, names);
        }
    }

    /// <summary>Shows every pane a row, as the surface's refresh does, so what a pane does when it is shown is pressed.</summary>
    /// <param name="shell">The surface.</param>
    /// <remarks>
    /// Review bound a key in a pane's <c>Show</c>, which runs on every refresh, and the probe pressed each pane only as
    /// its constructor left it. The walk refuses a binding outside construction; this presses what showing does anyway.
    /// </remarks>
    private static void Shown(Shell shell)
    {
        var at = DateTimeOffset.UnixEpoch;
        var person = AgentName.Create("arvind@test");

        shell.Projects.Show(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            new Dictionary<string, ProjectHealth>(),
            null);
        shell.Agents.Show(
            [new AgentRecord(Orders, "test", AgentTier.Full, "/src/thing", null, null, false, AgentStatus.Active, "the thing", AgentPresence.Startable)],
            Parley.Configuration.NodeCatalogue.Empty,
            null);
        shell.Conversations.Show([new ConversationSummary("release", [Orders, person], 1, at, at, HasOpenExchange: true)], null);
        shell.Transcript.Show(
            "release",
            "alpha",
            new ConversationExport(
                [new Message(1, 1, MessageType.Request, person, Orders, "some-model-9", "{\"q\":\"why\"}", "release", at, null)],
                [new ExchangeSnapshot(1, ExchangeState.Open, at, null)]),
            null);
        shell.Room.Show([new PairState(Orders, 1, Turn.Mine)], [], seated: true, person);
        shell.Line.Show(null, seated: true);
        shell.Plans.Show([new RoadmapSummary(1, 1, "Ship it", 1, 1, 0)]);
        shell.Roadmaps.Show(
            [new RoadmapProposal(1, Orders, null, "Ship it", new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]), 10, ProposalState.Waiting, at, null)]);
        shell.Configuration.Show([AutoAccepting.AsSetting(on: true)]);
    }

    /// <summary>Waits for every read the surface has queued, so what it does when it lands is done before the next look.</summary>
    /// <param name="shell">The surface.</param>
    /// <remarks>
    /// Nothing queued reaches a broker: the directory refuses to be asked, and the queue says a failure rather than
    /// throwing it, so this only waits.
    /// </remarks>
    private static void Settle(Shell shell)
    {
        // Again until nothing new was queued while waiting: a read can queue another when it lands.
        for (var work = shell.Settled; ; work = shell.Settled)
        {
            Assert.True(work.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "the surface's queued work never finished");

            if (work == shell.Settled)
            {
                return;
            }
        }
    }

    /// <summary>Gives every empty list in a view rows, so a key bound to one has something to act on.</summary>
    /// <param name="root">The pane or dialog.</param>
    /// <returns>The same view.</returns>
    private static View Filled(View root)
    {
        foreach (var list in Inside(root).OfType<ListView>().Where(list => list.Source is not { Count: > 0 }))
        {
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(["orders", "billing", "inventory"]));
        }

        // A surface focuses its first pane when it is built, and nothing pressed here may have focus: a key pressed at a
        // view would be passed on to it. **Cleared from the top**, the surface included: a view that still has focus with
        // nothing focused inside it hands focus back to the view it last gave it to, the next time anything asks. Clearing
        // only the pane left the surface holding focus, and on Linux and macOS, one run in several, the projects pane
        // had it back before the first key, or after Shift+Enter, while Windows passed.
        var top = root;

        while (top.SuperView is { } above)
        {
            top = above;
        }

        foreach (var view in Inside(top).Where(view => view.HasFocus).ToList())
        {
            view.HasFocus = false;
        }

        Assert.DoesNotContain(Inside(top), view => view.HasFocus);

        return root;
    }

    /// <summary>Every dialog in every shape, and how to build it as the terminal does.</summary>
    /// <returns>
    /// Each dialog's place, how to build it in one shape (null when its constructor refuses that shape), and whether the
    /// place claims a key and whether a command names it.
    /// </returns>
    private static IEnumerable<(KeyContext Place, Func<View?> Build, (Func<Key, bool> Claims, Func<Key, bool> Names) Table)> Dialogs()
    {
        foreach (var (place, type, table) in DialogPlaces())
        {
            foreach (var (constructor, arguments) in Shapes(type))
            {
                yield return (place, () => Built(constructor, arguments), table);
            }
        }
    }

    /// <summary>Every dialog's type, its place, and what its table claims and names there.</summary>
    /// <returns>The dialogs.</returns>
    private static (KeyContext Place, Type Dialog, (Func<Key, bool> Claims, Func<Key, bool> Names) Table)[] DialogPlaces()
    {
        (KeyContext, Type, (Func<Key, bool>, Func<Key, bool>)) Dialog<TDialog>(KeyContext place)
            where TDialog : IKeyTable<TDialog>
        {
            var table = CommandTables.Of<TDialog>();

            return (place, typeof(TDialog), (key => table.Claims(place, key), key => table.Names(place, key)));
        }

        return
        [
            Dialog<PlanView>(KeyContext.RoadmapDialog),
            Dialog<ProposalView>(KeyContext.ProposalDialog),
            Dialog<TickDialog>(KeyContext.TickDialog),
            Dialog<ResponsibilityDialog>(KeyContext.ResponsibilityDialog),
            Dialog<TextDialog>(KeyContext.TextDialog),
            Dialog<ChoiceDialog>(KeyContext.ChoiceDialog),
            Dialog<FormDialog>(KeyContext.FormDialog),
            Dialog<Browsing>(KeyContext.BrowsingDialog),
            Dialog<LogView>(KeyContext.LogDialog),
            Dialog<Health>(KeyContext.HealthDialog),
            Dialog<Saying>(KeyContext.SayingDialog),
        ];
    }

    /// <summary>Every shape a dialog can be built in: each of its constructors, with each value its own arguments can take.</summary>
    /// <param name="dialog">The dialog's type.</param>
    /// <returns>Each constructor, with one set of arguments.</returns>
    /// <remarks>
    /// <para>
    /// <b>Found from the constructors, not chosen.</b> Review bound a key behind <c>if (!canSave)</c> in a dialog the
    /// probe built only able to save, though the terminal opens it unable to whenever the agent is on another machine.
    /// So every constructor, private ones included, is built with both values of each flag, every value of each choice,
    /// and both null and a value for each argument that may be null, in every combination.
    /// </para>
    /// <para>
    /// An argument's contents are not varied: a list's rows, a roadmap's items. A binding construction chose by them
    /// would have to be under a branch or a loop, which the walk refuses.
    /// </para>
    /// </remarks>
    private static IEnumerable<(ConstructorInfo Constructor, object?[] Arguments)> Shapes(Type dialog)
    {
        var nullability = new NullabilityInfoContext();

        foreach (var constructor in dialog.GetConstructors(Made))
        {
            IEnumerable<object?[]> shapes = [[]];

            foreach (var parameter in constructor.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                var mayBeNull = type != parameter.ParameterType || nullability.Create(parameter).WriteState == NullabilityState.Nullable;

                object?[] values = type == typeof(bool) ? [false, true]
                    : type.IsEnum ? [.. Enum.GetValues(type).Cast<object>()]
                    : [Sample(constructor, parameter, type)];

                if (mayBeNull)
                {
                    values = [null, .. values];
                }

                shapes = [.. shapes.SelectMany(shape => values.Select(value => (object?[])[.. shape, value]))];
            }

            foreach (var shape in shapes)
            {
                yield return (constructor, shape);
            }
        }
    }

    /// <summary>A value for a dialog's argument that is neither a flag nor a choice.</summary>
    /// <param name="constructor">The constructor.</param>
    /// <param name="parameter">The argument.</param>
    /// <param name="type">Its type, not nullable.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// Every string is the test's own build folder, which is small and always there: the folder picker reads the folder
    /// it opens, and to every other dialog a path is words. A dialog with an argument of a type not here fails, so a new
    /// one is pressed the day it is written.
    /// </remarks>
    private static object Sample(ConstructorInfo constructor, ParameterInfo parameter, Type type)
    {
        var at = DateTimeOffset.UnixEpoch;
        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);

        return (parameter.Name, type) switch
        {
            ("width", _) => 40,
            (_, var t) when t == typeof(int) => 1,
            (_, var t) when t == typeof(string) => AppContext.BaseDirectory,
            (_, var t) when t == typeof(Roadmap) => new Roadmap(1, 1, "Ship it", default, null, 1, []),
            (_, var t) when t == typeof(RoadmapStanding) => new RoadmapStanding([], NextKind.Complete, null, 0),
            (_, var t) when t == typeof(RoadmapProposal) => new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, at, null),
            (_, var t) when t == typeof(IReadOnlyList<Tickable>) => (IReadOnlyList<Tickable>)[new Tickable("orders", string.Empty, false)],
            (_, var t) when t == typeof(IReadOnlyList<string>) => (IReadOnlyList<string>)["Work that needs you", "  Nothing is flagged."],
            (_, var t) when t == typeof(IReadOnlyList<FormField>) => (IReadOnlyList<FormField>)[new FormField("Short:"), new FormField("Long:", Long: true)],

            // A correction and a check on what is typed are the same type once compiled, so they are told apart by name.
            ("correct", var t) when t == typeof(Func<string, string>) => (Func<string, string>)(text => text),
            (_, var t) when t == typeof(Func<string, string?>) => (Func<string, string?>)(_ => null),
            _ => throw new InvalidOperationException(
                $"{constructor.DeclaringType!.Name}'s argument {parameter.Name} is a {type.Name}, which {nameof(Sample)} has no value for: add one, so the dialog is pressed"),
        };
    }

    /// <summary>Builds a dialog in one shape.</summary>
    /// <param name="constructor">The constructor.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The dialog, or null when the constructor refuses the arguments, so the terminal could never open that shape.</returns>
    private static View? Built(ConstructorInfo constructor, object?[] arguments)
    {
        try
        {
            return (View)constructor.Invoke(arguments);
        }
        catch (TargetInvocationException refused) when (refused.InnerException is ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every view the terminal can open as a session: each concrete runnable type in its assembly.</summary>
    /// <returns>The types.</returns>
    /// <remarks>
    /// Found, not listed, so a dialog added next month is in it the day it is written. A session takes every key first,
    /// so one with no table is a set of keys no table has ever seen.
    /// </remarks>
    private static IReadOnlyList<Type> Sessions() =>
    [
        .. TheTerminal.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IRunnable).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal),
    ];

    [Fact]
    public void Every_view_opened_as_a_session_builds_a_table_is_listed_and_is_pressed()
    {
        var built = CommandTables.BuildEvery();
        var pressed = new HashSet<Type>();

        foreach (var (_, root, _, _) in Everywhere())
        {
            pressed.Add(root.GetType());
        }

        var problems = new List<string>();

        foreach (var session in Sessions())
        {
            if (!session.GetInterfaces().Any(table => Of(table, typeof(IKeyTable<>), session)))
            {
                problems.Add($"{session.Name} is opened as a session and builds no table: implement IKeyTable<{session.Name}>");
            }

            if (!CommandTables.Targets.Contains(session))
            {
                problems.Add($"{session.Name} is not listed in CommandTables, so its table is not built before the tool starts");
            }

            // The surface is pressed one pane at a time, as each pane's own view; every dialog is pressed whole, and is a
            // table dialog of its own type, so its key handler is the sealed one.
            if (session != typeof(Shell))
            {
                if (!Of(session.BaseType, typeof(TableDialog<>), session))
                {
                    problems.Add($"{session.Name} derives from {session.BaseType?.Name}, not TableDialog<{session.Name}>");
                }

                if (!pressed.Contains(session))
                {
                    problems.Add($"{session.Name} is never built and pressed in {nameof(Everywhere)}");
                }
            }
        }

        // Every constructor of every dialog builds in some shape, so none is skipped because each shape it was given was
        // refused.
        foreach (var (_, dialog, _) in DialogPlaces())
        {
            foreach (var constructor in dialog.GetConstructors(Made))
            {
                var made = Shapes(dialog).Where(shape => shape.Constructor == constructor).Count(shape =>
                {
                    using var view = Built(shape.Constructor, shape.Arguments);

                    return view is not null;
                });

                if (made == 0)
                {
                    problems.Add($"{dialog.Name}({string.Join(", ", constructor.GetParameters().Select(parameter => parameter.Name))}) is built in no shape");
                }
            }
        }

        // Only a table dialog of its own type derives from the dialog base, so no dialog can answer a key some other way.
        problems.AddRange(TheTerminal.GetTypes()
            .Where(type => typeof(TableDialog).IsAssignableFrom(type) && type != typeof(TableDialog) && type != typeof(TableDialog<>))
            .Where(type => type.IsAbstract || !Of(type.BaseType, typeof(TableDialog<>), type))
            .Select(type => $"{type.Name} derives from the dialog base other than as TableDialog<{type.Name}>"));

        // Non-vacuity: the surface and every dialog the review named were found as sessions, and every place is built.
        Assert.Contains(typeof(Shell), Sessions());
        Assert.Contains(typeof(PlanView), Sessions());
        Assert.Contains(typeof(Saying), Sessions());
        Assert.True(Sessions().Count >= CommandTables.Targets.Count, $"found {Sessions().Count} sessions");
        Assert.Equal(built.Count, built.Distinct().Count());

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Whether a type is a generic definition closed over one type.</summary>
    /// <param name="type">The type, or null.</param>
    /// <param name="definition">The generic definition.</param>
    /// <param name="over">Its one argument.</param>
    /// <returns><see langword="true"/> when <paramref name="type"/> is <paramref name="definition"/> of <paramref name="over"/>.</returns>
    /// <remarks>Compared rather than built, because building one over a type that breaks its constraint throws.</remarks>
    private static bool Of(Type? type, Type definition, Type over) =>
        type is { IsGenericType: true } && type.GetGenericTypeDefinition() == definition && type.GetGenericArguments()[0] == over;

    [Fact]
    public void No_key_handler_in_the_terminal_does_anything_but_ask_its_table()
    {
        // Every override in the assembly that is handed a key: OnKeyDown, OnKeyDownNotHandled, OnKeyUp, the dialog base's
        // Take, and any the toolkit adds. Only the dialog base's sealed pair and the surface's fixed handler may exist.
        var overrides = TheTerminal.GetTypes()
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => method.GetBaseDefinition() != method && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Key)))
            .Select(Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Shell.OnKeyDown", "TableDialog.OnKeyDown", "TableDialog`1.Take"], overrides);

        // Sealed, so no dialog can put its own handler in front of the table.
        Assert.True(typeof(TableDialog).GetMethod("OnKeyDown", Declared)!.IsFinal);
        Assert.True(typeof(TableDialog<>).GetMethod("Take", Declared)!.IsFinal);

        // And each does nothing but what its body is for: no comparison, no helper handed the key, no second table.
        // Review's probes were `if (Handle(key))` in the surface's handler, with Handle comparing the key itself, and a
        // comparison inside a dialog's helper; each is a call this list does not name.
        Assert.Equal(
            ["ArgumentNullException.ThrowIfNull", "CommandManager`1.Take", "Shell.Touched", "Shell.get_Context", "View.OnKeyDown"],
            Called(typeof(Shell).GetMethod("OnKeyDown", Declared)!));
        Assert.Equal(
            ["ArgumentNullException.ThrowIfNull", "TableDialog.Take", "View.OnKeyDown"],
            Called(typeof(TableDialog).GetMethod("OnKeyDown", Declared)!));
        Assert.Equal(["CommandManager`1.Take"], Called(typeof(TableDialog<>).GetMethod("Take", Declared)!));
    }

    [Fact]
    public void Nothing_overrides_what_a_view_does_with_a_command()
    {
        // A view answers a command, such as accepting on Enter, through a handler or through an override of the toolkit's
        // hook for it. Review overrode the agents list's accepting hook, the list took Enter, which a command names, and
        // every test passed: the probe looked only for handlers. So no hook a command reaches is overridden in the
        // terminal unless it is decided here, and the probe counts an override as answering.
        var overrides = TheTerminal.GetTypes()
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => method.GetBaseDefinition() is var root && root != method && root.DeclaringType!.Assembly == typeof(View).Assembly && HandlesACommand(root))
            .Select(Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(OverridesOfCommands.Select(decided => decided.Method).Order(StringComparer.Ordinal), overrides);

        // Non-vacuity: the hooks review's probe overrode, and the others a key can reach, are seen as such.
        foreach (var hook in new[] { "OnAccepting", "OnActivating", "OnHandlingHotKey", "OnCommandNotBound", "OnAccepted", "OnActivated", "OnHotKeyCommand" })
        {
            Assert.True(HandlesACommand(typeof(View).GetMethod(hook, Declared)!), hook);
        }

        Assert.False(HandlesACommand(typeof(View).GetMethods(Declared).First(method => method.Name == "OnDrawingContent")));

        using var answering = new OverridingAccept();

        Assert.True(Answers(answering, Accepting));
        Assert.False(Answers(answering, Activating));
    }

    /// <summary>The overrides of a toolkit hook a command reaches, each with why it does what its command says.</summary>
    /// <remarks>None yet. The probe counts each as answering its command, so a key bound to it is pressed as taken.</remarks>
    private static readonly (string Method, string Why)[] OverridesOfCommands = [];

    /// <summary>Whether a toolkit hook is reached by a command, which a key can be bound to.</summary>
    /// <param name="hook">The hook as the toolkit declares it.</param>
    /// <returns>
    /// <see langword="true"/> for one handed a command or what a command carries, or named for a command, accepting,
    /// activating or a hot key.
    /// </returns>
    private static bool HandlesACommand(MethodInfo hook) =>
        hook.GetParameters().Any(parameter =>
            typeof(CommandEventArgs).IsAssignableFrom(parameter.ParameterType)
            || typeof(ICommandContext).IsAssignableFrom(parameter.ParameterType)
            || parameter.ParameterType == typeof(Command)
            || (parameter.ParameterType != typeof(Key) && parameter.ParameterType.GetProperties().Any(property => property.PropertyType == typeof(Key))))
        || new[] { "Command", "HotKey", "Accept", "Activat" }.Any(word => hook.Name.Contains(word, StringComparison.Ordinal));

    /// <summary>The toolkit's type a terminal view is made from.</summary>
    /// <param name="type">The terminal's type.</param>
    /// <returns>Its nearest base in the toolkit.</returns>
    private static Type Toolkits(Type type)
    {
        while (type.Assembly != typeof(View).Assembly)
        {
            type = type.BaseType!;
        }

        return type;
    }

    /// <summary>A view that answers accepting with an override and no handler, as review's probe made the agents list.</summary>
    private sealed class OverridingAccept : View
    {
        /// <inheritdoc/>
        protected override bool OnAccepting(CommandEventArgs args) => true;
    }

    [Fact]
    public void No_view_in_a_place_has_a_hot_key_or_reads_one_from_its_title()
    {
        // Every view each place builds, in every shape, as the probe presses it: no hot key, and none read from a title
        // set later. Review set a button's title to `_Done` in a handler, and the button took d, D and Alt+D.
        var found = new List<string>();

        foreach (var (place, root, _, _) in Everywhere())
        {
            // A view of a type the terminal cannot name, such as a text field's own pop-up, is the toolkit's part of a view
            // the terminal made, and nothing in the terminal gives it a title.
            foreach (var view in Inside(root).Where(view => view.GetType() is { IsPublic: true } or { IsNestedPublic: true } || view.GetType().Assembly != typeof(View).Assembly)
                .Where(view => view.HotKey != Key.Empty || view.HotKeySpecifier != Plain.NoHotKey))
            {
                found.Add($"{place}: {view.GetType().Name} \"{view.Title}\" has hot key {view.HotKey}, read after {view.HotKeySpecifier}");
            }
        }

        Assert.True(found.Count == 0, string.Join("\n", found.Distinct(StringComparer.Ordinal)));

        // The terminal's own views made from a toolkit view that reads one turn it off as they are made, so a title set
        // later is drawn as it is spelled and binds nothing.
        var own = TheTerminal.GetTypes()
            .Where(type => typeof(View).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Made, Type.EmptyTypes) is not null)
            .Where(type => ReadsHotKeys(Toolkits(type)))
            .ToList();

        Assert.Contains(typeof(PlainButton), own);
        Assert.Contains(typeof(Plain), own);
        Assert.Contains(typeof(GuardedList), own);

        foreach (var type in own)
        {
            using var view = (View)Activator.CreateInstance(type, nonPublic: true)!;

            view.Title = "_Done";
            view.Text = "_Done";

            Assert.Equal(Key.Empty, view.HotKey);
            Assert.Empty(view.HotKeyBindings.GetBindings());
        }

        // Non-vacuity: the toolkit's own button, titled the same way, does take the key.
        using var toolkit = new Button { Title = "_Done" };

        Assert.True(ReadsHotKeys(typeof(Button)));
        Assert.NotEqual(Key.Empty, toolkit.HotKey);
        Assert.NotEmpty(toolkit.HotKeyBindings.GetBindings());
    }

    [Fact]
    public void Nothing_subscribes_to_a_key_but_the_few_ways_in_key_routing()
    {
        // Found in the compiled code, so a subscription written in any spelling, in a lambda, a local function or a
        // constructor, is found the same way.
        var subscribing = TheTerminal.GetTypes()
            .SelectMany(Bodies)
            .Where(body => Calls(body).Any(SubscribesToAKey))
            .Select(Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["KeyRouting.Box", "KeyRouting.TableFirst", "KeyRouting.Touching", "KeyRouting.Walking"], subscribing);

        // Non-vacuity: the reader sees calls at all, and sees the toolkit's key event as one.
        Assert.Contains(Calls(typeof(KeyRouting).GetMethod(nameof(KeyRouting.TableFirst), Declared)!), SubscribesToAKey);
        Assert.Contains(Calls(typeof(Shell).GetMethod("OnKeyDown", Declared)!), called => called.Name == "Touched");
    }

    [Fact]
    public void Nothing_binds_a_key_for_the_whole_application_and_views_bind_only_where_they_are_pressed()
    {
        // The application's own bindings answer a key before any view or table sees it, and nothing presses them, so
        // nothing reaches them. The hook that marks the person here subscribes to the keyboard, which the test on
        // subscriptions holds, and does nothing else with it.
        // The same goes for the bindings every view of a kind starts with, a shortcut's key bound for the application, a
        // key every view answers, and hot keys: handed out by a view to what is inside it, or read from a title, which
        // a button's label set later would turn into keys nothing pressed.
        var application = TheTerminal.GetTypes()
            .SelectMany(Bodies)
            .SelectMany(body => Instructions(body)
                .Where(instruction => BindsBeyondAPlace(instruction.Method, instruction.After, instruction.Bare))
                .Select(instruction => $"{Where(body)}: {instruction.Method.DeclaringType!.Name}.{instruction.Method.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(application.Count == 0, "a key bound beyond any place, or a hot key read from a title:\n" + string.Join("\n", application));

        // **A binding, or a handler for what a key does, is added only while a pressed pane or dialog is built.** The
        // probe presses each place as building it, showing it a row and opening it leave it, and nothing else: a binding
        // the surface added to a pane's view, one a pane added when it was shown again, or one a dialog added in an
        // override the toolkit calls later would be a key no claim had to answer for. So such a call may be made only
        // in the constructor (field initialisers included) of a pane or dialog the probe presses, or in a method of the
        // same type that only that construction calls.
        var pressed = Everywhere().Select(place => place.View.GetType()).ToHashSet();
        var construction = Construction(pressed);
        var binding = Binding(construction);
        var outside = new List<string>();
        var conditional = new List<string>();
        var menuLines = 0;

        foreach (var body in TheTerminal.GetTypes().SelectMany(Bodies))
        {
            // **And unconditionally.** The probe builds each dialog in the shapes its constructor's own arguments can
            // take, but not every value an argument's contents could have, so a binding inside construction may not be
            // skipped, repeated or chosen by a branch. Review bound a key behind `if (!canSave)`, and the probe built the
            // dialog only able to save.
            if (construction.Contains(body))
            {
                var guarded = Conditional(body);

                conditional.AddRange(Instructions(body)
                    .Where(instruction => instruction.Invoked && (Binds(instruction.Method) || TakesAKey(instruction.Method) || binding.Contains(Definition(instruction.Method))))
                    .Where(instruction => guarded.Any(span => span.From <= instruction.At && instruction.At < span.To))
                    .Select(instruction => $"{Where(body)}: {instruction.Method.DeclaringType!.Name}.{instruction.Method.Name}"));
            }

            foreach (var called in Calls(body).Where(called => Binds(called) || TakesAKey(called)))
            {
                if (construction.Contains(body))
                {
                    continue;
                }

                // **One call is named, and what it makes is checked instead**: the menu's lines are built by the
                // toolkit's constructor that takes a key, and given none, because a bound key would be answered by a menu
                // nobody has opened. Only that constructor, and only there: any other binding in the same method is not.
                if (Name(body) == "MenuBarView.Views" && called is ConstructorInfo { DeclaringType: var line } && line == typeof(global::Terminal.Gui.Views.MenuItem))
                {
                    menuLines++;

                    continue;
                }

                outside.Add($"{Where(body)}: {called.DeclaringType!.Name}.{called.Name}");
            }
        }

        Assert.True(menuLines > 0, "the menu's lines are no longer built by a call the walk sees; drop the exception");
        Assert.True(outside.Count == 0, "a key bound, or what a key does handled, outside the construction of a place the probe presses:\n" + string.Join("\n", outside.Distinct().Order(StringComparer.Ordinal)));
        Assert.True(conditional.Count == 0, "a key bound, or what a key does handled, on only some paths through construction:\n" + string.Join("\n", conditional.Distinct().Order(StringComparer.Ordinal)));

        // And the lines the bar builds carry no key: no key, no key binding and no hot-key binding beyond what a line the
        // toolkit builds with no key carries, in the line or anything inside it.
        var lines = MenuLines();
        using var bare = new global::Terminal.Gui.Views.MenuItem("Open", "Alt+O", () => { });

        foreach (var view in Inside(bare))
        {
            view.HotKeySpecifier = Plain.NoHotKey;
        }

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.True(line is { Key: null } || line.Key == Key.Empty, $"a menu line is bound to {line.Key}");
            Assert.Empty(Inside(line).SelectMany(view => view.HotKeyBindings.GetBindings()));
            Assert.Equal(KeysOf(bare), KeysOf(line));
        });

        // Non-vacuity: the calls review's probes made are seen as what they are, construction is where it is meant to be
        // and nowhere else, and the panes are among the places.
        var review = typeof(KeysTakenBeforeTheTableTests).GetMethod(nameof(BindingAsReviewDid), Declared)!;
        var probes = Calls(review);
        var beyond = Instructions(review).Where(instruction => BindsBeyondAPlace(instruction.Method, instruction.After, instruction.Bare)).Select(instruction => instruction.Method.Name).ToList();
        var spans = Conditional(review);

        Assert.Contains(probes, Binds);
        Assert.Contains(probes, TakesAKey);
        Assert.Equal(
            [".ctor", ".ctor", "get_DefaultKeyBindings", "get_KeyBindings", "set_AssignHotKeys", "set_BindKeyToApplication", "set_DefaultKey", "set_HotKeySpecifier"],
            beyond.Order(StringComparer.Ordinal));
        // Two makings are bare, the shortcut and the first button; the second button turns its hot keys off first.
        Assert.Equal(3, Calls(review).Count(called => called.Name == "set_HotKeySpecifier"));
        Assert.Equal(3, Calls(review).Count(called => called is ConstructorInfo { DeclaringType: var made } && typeof(View).IsAssignableFrom(made)));
        Assert.Contains(Instructions(review), instruction => instruction.Method.Name == "Add" && spans.Any(span => span.From <= instruction.At && instruction.At < span.To));
        Assert.Contains(Instructions(review), instruction => instruction.Method.Name == "Add" && !spans.Any(span => span.From <= instruction.At && instruction.At < span.To));
        Assert.Contains(construction, body => body is ConstructorInfo && body.DeclaringType == typeof(AgentsRegion));
        Assert.DoesNotContain(typeof(AgentsRegion).GetMethod(nameof(AgentsRegion.Show), Declared)!, construction);
        Assert.DoesNotContain(construction, body => body.DeclaringType == typeof(Shell));
        Assert.Contains(typeof(AgentsRegion), pressed);
        Assert.DoesNotContain(typeof(Shell), pressed);
    }

    /// <summary>The keys a view and everything inside it bind, with what each does, in one comparable order.</summary>
    /// <param name="root">The view.</param>
    /// <returns>Each binding as text.</returns>
    private static IReadOnlyList<string> KeysOf(View root) =>
    [
        .. Inside(root)
            .SelectMany(view => view.KeyBindings.GetBindings())
            .Select(binding => $"{binding.Key} {string.Join(",", binding.Value.Commands)}")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Every method that runs only while a pressed pane or dialog is built.</summary>
    /// <param name="pressed">The panes and dialogs the probe presses.</param>
    /// <returns>Their constructors, and the methods of the same type that only those call.</returns>
    /// <remarks>
    /// <para>
    /// A method counts when it is declared on the pressed type itself, is not virtual, and every call to it in the
    /// assembly is made from construction: a helper that builds a button. Taking a method's address is not a call, so
    /// a lambda written in a constructor is not construction: it runs when its event does, after the probe has looked.
    /// </para>
    /// <para>
    /// An override such as <c>EndInit</c> is virtual and so never counts, and a method the surface calls, such as a
    /// pane's <c>Show</c>, has a call from outside. Review put a binding in each and every test passed.
    /// </para>
    /// <para>
    /// <b>Construction is pressed in every shape its constructor's own arguments give it</b> (<see cref="Shapes"/>), and
    /// a binding in it, or a call to a method of construction that binds, may not sit on only some paths through it
    /// (<see cref="Conditional"/>): a branch on an argument's contents chooses a shape the probe never built. Review
    /// bound a key behind <c>if (!canSave)</c>, and the probe built the dialog only able to save.
    /// </para>
    /// </remarks>
    private static HashSet<MethodBase> Construction(IReadOnlySet<Type> pressed)
    {
        var callers = new Dictionary<MethodBase, List<MethodBase>>();
        var addressed = new HashSet<MethodBase>();

        foreach (var body in TheTerminal.GetTypes().SelectMany(Bodies))
        {
            foreach (var (called, invoked, _, _, _) in Instructions(body))
            {
                var key = Definition(called);

                if (!invoked)
                {
                    addressed.Add(key);
                }
                else if (callers.TryGetValue(key, out var list))
                {
                    list.Add(body);
                }
                else
                {
                    callers[key] = [body];
                }
            }
        }

        var construction = pressed.SelectMany(type => type.GetConstructors(Declared)).Cast<MethodBase>().ToHashSet();

        for (var grew = true; grew;)
        {
            grew = false;

            foreach (var method in pressed.SelectMany(type => type.GetMethods(Declared)))
            {
                if (!construction.Contains(method)
                    && !method.IsVirtual
                    && !addressed.Contains(method)
                    && callers.TryGetValue(method, out var from)
                    && from.All(construction.Contains))
                {
                    construction.Add(method);
                    grew = true;
                }
            }
        }

        return construction;
    }

    /// <summary>The methods of construction that bind a key, or call one that does.</summary>
    /// <param name="construction">Every method of construction.</param>
    /// <returns>Those that bind, directly or through another.</returns>
    private static HashSet<MethodBase> Binding(HashSet<MethodBase> construction)
    {
        var binding = new HashSet<MethodBase>();

        for (var grew = true; grew;)
        {
            grew = false;

            foreach (var method in construction.Where(method => !binding.Contains(method)))
            {
                if (Calls(method).Any(called => Binds(called) || TakesAKey(called) || binding.Contains(Definition(called))))
                {
                    binding.Add(method);
                    grew = true;
                }
            }
        }

        return binding;
    }

    /// <summary>A method as it is declared, so a call to a generic one is matched to its definition.</summary>
    /// <param name="method">The method called.</param>
    /// <returns>Its definition.</returns>
    private static MethodBase Definition(MethodBase method) =>
        method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : method;

    /// <summary>Whether a call adds a handler for what a key does to a view: accepting, activating or a hot key.</summary>
    /// <param name="called">The method called.</param>
    /// <returns>
    /// <see langword="true"/> for subscribing to any event that hands over a command's context: <c>Accepting</c>,
    /// <c>Activating</c>, <c>HandlingHotKey</c>, <c>CommandNotBound</c> and their past tenses. A handler on a key event
    /// itself is held by the test on subscriptions.
    /// </returns>
    private static bool TakesAKey(MethodBase called) =>
        called.Name.StartsWith("add_", StringComparison.Ordinal)
        && called.GetParameters() is [{ ParameterType: { IsGenericType: true } handler }]
        && handler.GetGenericArguments() is [var args]
        && (typeof(CommandEventArgs).IsAssignableFrom(args) || args == typeof(EventArgs<ICommandContext>));

    /// <summary>A menu's lines as the bar builds them.</summary>
    /// <returns>The lines that act.</returns>
    private static IReadOnlyList<global::Terminal.Gui.Views.MenuItem> MenuLines()
    {
        using var bar = new MenuBarView(() => [], (_, _) => { });
        var open = new RegionAction('o', "Open", () => Task.CompletedTask);
        MenuItem[] items = [new("Open", Shell.Alt('o'), open), MenuItem.Separator, new("Quit", "Ctrl+Q", open)];

        return [.. bar.Views(items).OfType<global::Terminal.Gui.Views.MenuItem>()];
    }

    /// <summary>
    /// The bindings lens 3's runs 144 to 146 added and no test saw, compiled here so the walk can be shown to see them.
    /// </summary>
    /// <param name="view">A view.</param>
    /// <param name="keyboard">The application's keyboard.</param>
    /// <param name="button">A button.</param>
    /// <param name="canSave">An argument construction could branch on.</param>
    /// <remarks>Never called.</remarks>
    private static void BindingAsReviewDid(View view, IKeyboard keyboard, Button button, bool canSave)
    {
        view.HotKeyBindings.Add(Key.Z.WithCtrl, Command.Down);
        keyboard.KeyBindings.Add(Key.F2, Command.Quit);
        view.Accepting += (_, args) => args.Handled = true;
        view.Add(new Shortcut(Key.F11, "x", () => { }) { BindKeyToApplication = true });
        ListView.DefaultKeyBindings![Command.Down] = new PlatformKeyBinding { All = [Key.J] };
        PopoverMenu.DefaultKey = Key.J;
        view.AssignHotKeys = true;
        button.HotKeySpecifier = (System.Text.Rune)'_';
        button.HotKeySpecifier = Plain.NoHotKey;
        view.Add(new Button());
        view.Add(new Button { HotKeySpecifier = Plain.NoHotKey, Title = "Done" });

        if (!canSave)
        {
            view.KeyBindings.Add(Key.F5, Command.Accept);
        }
    }

    /// <summary>Whether a call binds a key beyond any one place, or lets a title become a hot key.</summary>
    /// <param name="called">The method called.</param>
    /// <param name="after">The static field the instruction before the call loads, if it loads one.</param>
    /// <param name="bare">
    /// Whether the call makes a new object and does not turn its hot keys off first, rather than being a constructor's
    /// call to its base.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for anything on the keyboard but subscribing to its events; the static application's
    /// keyboard or bindings; an application binding added through a view's or a shortcut's; any static toolkit member
    /// over key bindings (the defaults every view of a kind starts with, the application's); a static toolkit key (the
    /// one that opens every menu); a view handing out hot keys to what is inside it; setting where a hot key is read
    /// from a title to anything but <see cref="Plain.NoHotKey"/>; and making a toolkit view that reads one by default.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Refused anywhere, construction included, because the probe presses one place at a time and none of these stays in
    /// one: a default binding is every list's, and a shortcut bound for the application answers before any view.
    /// </para>
    /// <para>
    /// <b>No view reads a hot key from its title</b>, so setting a title or text later can make no key. Review set a
    /// button's title to <c>_Done</c> in a handler and it took d, D and Alt+D; a title can be set from anywhere, and
    /// which view a setter is called on is not in the call, so the hot key is turned off where it is read instead. The
    /// terminal makes its own buttons and labels, <see cref="PlainButton"/> and <see cref="Plain"/>, which turn it off
    /// when they are made.
    /// </para>
    /// </remarks>
    private static bool BindsBeyondAPlace(MethodBase called, FieldInfo? after, bool bare) =>
        called.DeclaringType is { } type
        && ((typeof(IKeyboard).IsAssignableFrom(type) && !called.Name.StartsWith("add_", StringComparison.Ordinal) && !called.Name.StartsWith("remove_", StringComparison.Ordinal))
            || (type == typeof(Application) && called.Name is "get_Keyboard" or "get_KeyBindings")
            || (BindingsOfKeys(type) && called.Name == "AddApp")
            || (typeof(Shortcut).IsAssignableFrom(type) && called.Name == "set_BindKeyToApplication")
            || (type.Assembly == typeof(View).Assembly && called.IsStatic && called.Name.Contains("KeyBinding", StringComparison.Ordinal))
            || (type.Assembly == typeof(View).Assembly && called.IsStatic && called.Name.StartsWith("set_", StringComparison.Ordinal) && called.GetParameters() is [{ ParameterType: var key }] && key == typeof(Key))
            || (typeof(View).IsAssignableFrom(type) && called.Name is "set_AssignHotKeys" or "AssignHotKeysToSubViews" or "AssignHotKeyToView")
            || (type.Assembly == typeof(View).Assembly && called.Name == "set_HotKeySpecifier" && after != NoHotKey)
            || (bare && type.Assembly == typeof(View).Assembly && typeof(View).IsAssignableFrom(type) && ReadsHotKeys(type)));

    private static readonly FieldInfo NoHotKey = typeof(Plain).GetField(nameof(Plain.NoHotKey), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Whether a view reads a hot key from its title unless it is told not to.</summary>
    /// <param name="type">The view's type.</param>
    /// <returns>
    /// <see langword="true"/> when one made with no arguments reads one, or, for a type that cannot be made so, when a
    /// type between it and <see cref="View"/> declares where one is read from.
    /// </returns>
    /// <remarks>
    /// Asked of a view rather than read from the toolkit's documentation, which says a view reads none by default: its
    /// lists and text fields do, and their titles could be keys.
    /// </remarks>
    private static bool ReadsHotKeys(Type type) =>
        HotKeyReaders.GetOrAdd(type, static type =>
        {
            if (!type.IsAbstract && !type.ContainsGenericParameters && type.GetConstructor(Type.EmptyTypes) is { } made)
            {
                using var view = (View)made.Invoke(null);

                return view.HotKeySpecifier != Plain.NoHotKey;
            }

            for (var at = type; at is not null && at != typeof(View); at = at.BaseType)
            {
                if (at.GetProperty(nameof(View.HotKeySpecifier), Declared) is not null)
                {
                    return true;
                }
            }

            return false;
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> HotKeyReaders = new();

    /// <summary>Whether a call adds a key to a view, or changes what a key or a command bound to a view does.</summary>
    /// <param name="called">The method called.</param>
    /// <returns>
    /// <see langword="true"/> for adding or replacing a key or hot-key binding, setting a key on a toolkit type (a hot
    /// key, a shortcut's key, a whole set of bindings), giving a view a command, or making a view that takes a key.
    /// </returns>
    private static bool Binds(MethodBase called) =>
        called.DeclaringType is { } type
        && ((BindingsOfKeys(type) && called.Name is "Add" or "AddApp" or "Replace" or "ReplaceCommands")
            || (type.Assembly == typeof(View).Assembly
                && called.Name.StartsWith("set_", StringComparison.Ordinal)
                && called.GetParameters() is [{ ParameterType: var set }]
                && (set == typeof(Key) || set == typeof(KeyBindings)))
            || (typeof(View).IsAssignableFrom(type) && called.Name == "AddCommand")
            || (typeof(View).IsAssignableFrom(type) && called.IsConstructor && called.GetParameters().Any(parameter => parameter.ParameterType == typeof(Key))));

    /// <summary>Whether a type is a set of key bindings.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> for the toolkit's key bindings, as declared or through the base they share with the mouse's.</returns>
    private static bool BindingsOfKeys(Type type) =>
        typeof(KeyBindings).IsAssignableFrom(type)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CommandBindingsBase<,>) && type.GetGenericArguments()[0] == typeof(Key));

    /// <summary>A type outside any compiler-made or nested one, so a lambda's code is its declaring type's.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The outermost declaring type.</returns>
    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    [Fact]
    public void Every_key_a_pane_or_dialog_takes_is_claimed_in_its_place()
    {
        var missing = new List<string>();
        var named = new List<string>();
        var decided = new HashSet<string>(StringComparer.Ordinal);
        var plain = new List<string>();
        var visited = new HashSet<KeyContext>();

        foreach (var (place, root, claims, names) in Everywhere())
        {
            visited.Add(place);

            foreach (var (key, by) in Taken(root))
            {
                // A plain character is never an action: where nothing types, a view that takes one is a key a person
                // types by accident, and no claim makes it otherwise.
                if (CommandManager<Shell>.Printable(key) && !Types(by))
                {
                    plain.Add($"{place}: {key} (taken by {by.GetType().Name})");
                }
                else if (!claims(key))
                {
                    missing.Add($"{place}: {key} (taken by {by.GetType().Name})");
                }
                else if (names(key) && by is not TableDialog && !HandsKeysToItsTable(by))
                {
                    // A command named on a key a view takes first never runs from it, so the view has to do what the
                    // command says. That is a decision, and each is written down in TakenAsNamed.
                    if (TakenAsNamed.Any(decided => decided.Place == place && decided.Key == key))
                    {
                        decided.Add($"{place}: {key}");
                    }
                    else
                    {
                        named.Add($"{place}: {key} (taken by {by.GetType().Name})");
                    }
                }
            }
        }

        // Non-vacuity: every place a table builds was stood in, but the one for panes nothing belongs to, and the
        // handlers this rests on are where it looks for them. Read from the tables rather than the enum, so a place a
        // table builds is pressed whether or not anybody remembered to name it here.
        Assert.Equal(CommandTables.BuildEvery().Except([KeyContext.Elsewhere]).Order(), visited.Order());
        Assert.NotNull(Accepting);
        Assert.NotNull(Activating);

        Assert.True(plain.Count == 0, "a plain character taken where nothing types:\n" + string.Join("\n", plain.Distinct()));
        Assert.True(missing.Count == 0, "taken before any command, and claimed nowhere in the place:\n" + string.Join("\n", missing.Distinct()));
        Assert.True(
            named.Count == 0,
            "named by a command that never runs, because a view takes the key first:\n" + string.Join("\n", named.Distinct()));

        // And every decision is still one: a key no view takes any more is a decision about nothing.
        Assert.Equal(TakenAsNamed.Select(taken => $"{taken.Place}: {taken.Key}").Order(StringComparer.Ordinal), decided.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_keys_found_include_the_ones_review_found_missing()
    {
        // Non-vacuity for the probe itself: the room's and the conversations' Enter, taken by their lists' accept, and the
        // box's Ctrl+Enter, taken by its own key handler, all reach the list of keys taken.
        using var room = new RoomRegion();
        using var conversations = new ConversationsRegion();
        using var line = new LineRegion();

        Assert.Contains(Taken(room), taken => taken.Key == Key.Enter);
        Assert.Contains(Taken(conversations), taken => taken.Key == Key.Enter);
        Assert.Contains(Taken(line), taken => taken.Key == Key.Enter.WithCtrl);

        // And a key bound to accepting with nothing answering it is not: the agents list leaves Enter to the surface.
        using var agents = new AgentsRegion();

        Assert.DoesNotContain(Taken(agents), taken => taken.Key == Key.Enter);

        // A dialog's list that hands its keys to the table is seen doing so, so what it takes runs a command.
        using var ticks = new TickDialog("Choose", "Which.", [new Tickable("orders", string.Empty, false)]);

        Assert.Contains(Inside(ticks), HandsKeysToItsTable);
        Assert.DoesNotContain(Inside(agents), HandsKeysToItsTable);

        // Plain characters are pressed: a view that takes one is seen taking it, and the surface's lists take none.
        using var taking = new View { CanFocus = true };
        taking.KeyDown += (_, key) => key.Handled = key == new Key('j');
        using var filling = Lists.Filling();
        filling.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(["orders", "billing"]));

        Assert.Contains(Taken(taking), taken => taken.Key == new Key('j'));
        Assert.False(Types(taking));
        Assert.DoesNotContain(Taken(filling), taken => CommandManager<Shell>.Printable(taken.Key));
        Assert.False(Types(filling));
    }

    /// <summary>
    /// The keys a pane's view takes before the surface's table, where a command names the key and the view does what it
    /// says.
    /// </summary>
    /// <remarks>
    /// A command on a key a view takes first never runs from the key: the status line names it, and the view acts. That
    /// is right only where the view does what the command says, which no test can read, so each is decided here. Review
    /// added an accept handler to the agents list from the surface, and Enter, named as the agent's menu, did whatever the
    /// handler did.
    /// </remarks>
    private static readonly (KeyContext Place, Key Key, string Why)[] TakenAsNamed =
    [
        (KeyContext.Line, Key.Enter, "the box sends on its own key handler, which the command named sending runs too"),
        (KeyContext.Line, Key.Enter.WithShift, "the box breaks the line on its own key handler, as the command says"),
        (KeyContext.Plans, Key.Enter, "the list's accept opens the roadmap under the cursor, which is what the command does"),
        (KeyContext.Roadmaps, Key.Enter, "the list's accept reviews the proposal under the cursor, which is what the command does"),
    ];

    /// <summary>A view's key handlers, read because the toolkit offers no other way to ask.</summary>
    private static readonly FieldInfo KeyDown = typeof(View).GetField("KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Whether a view offers its keys to its dialog's table before its own bindings see them.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> when <see cref="KeyRouting.TableFirst"/> was called on it.</returns>
    /// <remarks>Such a view takes a key a command names only by running the command.</remarks>
    private static bool HandsKeysToItsTable(View view) =>
        KeyDown.GetValue(view) is Delegate handlers
        && handlers.GetInvocationList().Any(handler => handler.Method.Name.StartsWith("<" + nameof(KeyRouting.TableFirst) + ">", StringComparison.Ordinal));

    /// <summary>Whether a view is one somebody types into, where a plain character is text rather than an action.</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> for one of the toolkit's editors, or a list that finds a row as it is typed.</returns>
    /// <remarks>
    /// An editor takes every character as text, read-only included: a paragraph somebody reads swallows a letter typed
    /// at it, which runs nothing. The box is the toolkit's deprecated text view, which the terminal still builds
    /// (Entry.cs says why).
    /// </remarks>
#pragma warning disable CS0618
    private static bool Types(View view) => view switch
    {
        TextField or TextView => true,
        ListView list => list.KeystrokeNavigator is not null,
        _ => false,
    };
#pragma warning restore CS0618

    /// <summary>Every key a view or anything inside it takes, and which view takes it.</summary>
    /// <param name="root">The pane or dialog.</param>
    /// <returns>The keys.</returns>
    private static IReadOnlyList<(Key Key, View By)> Taken(View root)
    {
        var taken = new List<(Key, View)>();
        var defaults = Inside(root).OfType<Button>().Any(button => button.IsDefault);

        // Pressed one view at a time, so nothing focused inside may route a key onward: a view out of an application
        // has no focus, and a key that reached a box's copy or paste would reach the machine's clipboard.
        Assert.DoesNotContain(Inside(root), view => view.HasFocus);

        // A view that cannot have focus is never offered a key: the room's completion list, a dialog's heading. Its hot
        // keys are, because the toolkit offers a hot key to every view inside the one pressed, focused or not.
        foreach (var view in Inside(root))
        {
            foreach (var (key, binding) in Bindings(view, focusable: view == root || view.CanFocus))
            {
                var commands = binding.Commands;

                // Accepting or activating alone is taken only where something answers it, and accepting by a default
                // button wherever it is pressed in the dialog: what nothing answers goes on up to it, whatever the key.
                var answered = commands.All(command => command is Command.Accept or Command.Activate)
                    ? (commands.Contains(Command.Accept) && (Answers(view, Accepting) || defaults))
                      || (commands.Contains(Command.Activate) && Answers(view, Activating))
                    : true;

                if (answered)
                {
                    taken.Add((key, view));
                }
            }

            // What a view's own key handler takes, found by pressing it. A key bound by this view, or by one inside it
            // that a key pressed here can be passed on to, is never pressed: it is decided above, a binding to copy or
            // paste would reach the machine's clipboard, and activating a view gives it focus. A plain character is not
            // pressed at a view that types, where it is text.
            var bound = Inside(view)
                .SelectMany(inside => Bindings(inside, focusable: true))
                .Select(binding => binding.Key)
                .ToHashSet();

            if (!(view == root || view.CanFocus))
            {
                continue;
            }

            foreach (var key in Candidates().Where(key => !bound.Contains(key) && !(Types(view) && CommandManager<Shell>.Printable(key))))
            {
                // A fresh key every time: a key carries whether it was handled, and one made from a handled key with
                // Shift added arrives handled already.
                if (view.NewKeyDownEvent(new Key(key.KeyCode)))
                {
                    taken.Add((key, view));
                }

                if (root.SuperView is Shell shell)
                {
                    Settle(shell);
                }

                // Still nothing focused, or a later press would go somewhere other than the view it was pressed at.
                Assert.False(Inside(root).Any(inside => inside.HasFocus), $"{key} at {view.GetType().Name} gave a view focus");
            }
        }

        return taken;
    }

    /// <summary>Whether a view answers accepting or activating: a handler is subscribed, or the terminal overrode the hook.</summary>
    /// <param name="view">The view.</param>
    /// <param name="handler">The event, <see cref="Accepting"/> or <see cref="Activating"/>.</param>
    /// <returns><see langword="true"/> when something answers it.</returns>
    /// <remarks>
    /// An override counts because review answered accepting with one and the probe, reading only handlers, saw nothing.
    /// The toolkit's own overrides are what its views do with a key, which the bindings already say.
    /// </remarks>
    private static bool Answers(View view, FieldInfo handler)
    {
        if (handler.GetValue(view) is not null)
        {
            return true;
        }

        var hook = handler == Accepting ? "OnAccepting" : "OnActivating";

        for (var type = view.GetType(); type is not null && type.Assembly != typeof(View).Assembly; type = type.BaseType)
        {
            if (type.GetMethod(hook, Declared) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a view binds: its hot keys, and its keys when it can be offered one.</summary>
    /// <param name="view">The view.</param>
    /// <param name="focusable">Whether the view is offered its own keys: it is the one pressed, or it can have focus.</param>
    /// <returns>The bindings.</returns>
    /// <remarks>
    /// Hot keys are read as well as keys, because review bound a key to a list as a hot key and nothing that read only
    /// the keys saw it.
    /// </remarks>
    private static IEnumerable<KeyValuePair<Key, KeyBinding>> Bindings(View view, bool focusable) =>
        focusable ? [.. view.KeyBindings.GetBindings(), .. view.HotKeyBindings.GetBindings()] : view.HotKeyBindings.GetBindings();

    private static IEnumerable<View> Inside(View root) => [root, .. root.SubViews.SelectMany(Inside)];

    /// <summary>Every key a person could press: the named keys and their chords, and every plain character.</summary>
    /// <remarks>
    /// Tab is not pressed: it moves focus between views, which every place reserves, and pressed at a view in memory it
    /// would give a view focus and send every later key somewhere else.
    /// </remarks>
    private static IEnumerable<Key> Candidates()
    {
        Key[] named =
        [
            Key.Enter, Key.Space, Key.Esc, Key.Backspace, Key.Delete, Key.InsertChar, Key.Home, Key.End, Key.PageUp,
            Key.PageDown, Key.CursorUp, Key.CursorDown, Key.CursorLeft, Key.CursorRight,
            Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
        ];

        foreach (var key in named)
        {
            yield return key;
            yield return key.WithShift;
            yield return key.WithCtrl;
            yield return key.WithAlt;
        }

        foreach (var letter in "abcdefghijklmnopqrstuvwxyz0123456789?")
        {
            var key = new Key(letter);

            yield return key.WithAlt;

            if (char.IsAsciiLetter(letter))
            {
                yield return key.WithCtrl;
            }
        }

        // **Plain characters too**, which review found were never pressed: a plain letter could become a dialog's action
        // and pass every test. Capitals and a few marks as well as the letters and digits.
        foreach (var character in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789?/.,;-")
        {
            yield return new Key(character);
        }
    }

    /// <summary>A method as the assertions name it: its declaring type, outside any compiler-made one, and the method it is written in.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The name.</returns>
    private static string Name(MethodBase method)
    {
        var type = method.DeclaringType!;
        string? within = null;

        while (type.IsNested && type.Name.Contains('<', StringComparison.Ordinal))
        {
            within ??= WrittenIn(type.Name);
            type = type.DeclaringType!;
        }

        // A lambda, a local function or an iterator is named for the method it is written in: `<Touching>b__0` is
        // Touching's, and the MoveNext of `<Views>d__5` is Views'.
        return type.Name + "." + (WrittenIn(method.Name) ?? within ?? method.Name);
    }

    /// <summary>Where a call is written, as a failure names it.</summary>
    /// <param name="method">The method the call is in.</param>
    /// <returns>Its <see cref="Name"/>, said to be a lambda or a local function when it is one.</returns>
    /// <remarks>
    /// A lambda written in a constructor is named for the constructor, and "AgentsRegion..ctor" alone read as though
    /// construction were refused: it is the lambda, which runs when its event does, that is.
    /// </remarks>
    private static string Where(MethodBase method)
    {
        var name = Name(method);

        // An async lambda's code is in a state machine named for it, so the types it is nested in are read too.
        var written = string.Concat(method.Name, method.DeclaringType!.FullName);

        return written.Contains(">b__", StringComparison.Ordinal) ? $"a lambda in {name}"
            : written.Contains(">g__", StringComparison.Ordinal) ? $"a local function in {name}"
            : name;
    }

    /// <summary>The method a compiler-made name was made for.</summary>
    /// <param name="name">A type's or a method's name.</param>
    /// <returns>The method's name, or <see langword="null"/> when the name was not made for one.</returns>
    private static string? WrittenIn(string name) =>
        name is ['<', .. var rest] && rest.IndexOf('>', StringComparison.Ordinal) is > 0 and var end ? rest[..end] : null;

    /// <summary>The method bodies a type declares, constructors included.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The methods.</returns>
    private static IEnumerable<MethodBase> Bodies(Type type) =>
        [.. type.GetMethods(Declared), .. type.GetConstructors(Declared)];

    /// <summary>Whether a call adds a handler to an event that hands over a key.</summary>
    /// <param name="called">The method called.</param>
    /// <returns><see langword="true"/> for <c>KeyDown +=</c>, <c>KeyUp +=</c> and any other such event.</returns>
    private static bool SubscribesToAKey(MethodBase called) =>
        called.Name.StartsWith("add_", StringComparison.Ordinal)
        && called.GetParameters() is [{ ParameterType: { IsGenericType: true } handler }]
        && handler.GetGenericArguments().Contains(typeof(Key));

    /// <summary>The methods a method calls, by name, each once and in order.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The names.</returns>
    private static IReadOnlyList<string> Called(MethodBase method) =>
        [.. Calls(method).Select(called => called.DeclaringType!.Name + "." + called.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The single-byte and two-byte opcodes, by value, for walking a method's code.</summary>
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Every method a method's compiled code calls, or takes the address of.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The methods.</returns>
    /// <remarks>
    /// Walks the instructions rather than looking for a byte, so an operand is never read as an instruction. A token that
    /// does not resolve fails the test: a call this cannot name is one it cannot vouch for.
    /// </remarks>
    private static IReadOnlyList<MethodBase> Calls(MethodBase method) => [.. Instructions(method).Select(instruction => instruction.Method)];

    /// <summary>Every method a method's compiled code calls or takes the address of, and which it does.</summary>
    /// <param name="method">The method.</param>
    /// <returns>
    /// Each method, with <see langword="true"/> for a call and <see langword="false"/> for taking its address, where in
    /// the code it is, the static field the instruction just before it loads, if it loads one, and whether it makes a
    /// new object and does not turn its hot keys off first (<c>new T { HotKeySpecifier = Plain.NoHotKey, ... }</c>,
    /// whose next instructions are a copy of the new object, a load of <see cref="Plain.NoHotKey"/> and the setter).
    /// </returns>
    private static IReadOnlyList<(MethodBase Method, bool Invoked, int At, FieldInfo? After, bool Bare)> Instructions(MethodBase method)
    {
        var (typeArguments, methodArguments) = Arguments(method);
        var calls = new List<(MethodBase, bool, int, FieldInfo?, bool)>();
        var decoded = Decoded(method).ToList();

        FieldInfo? Loads(int index) =>
            index < decoded.Count && decoded[index] is { Code: var code, Operand: var operand, Bytes: var bytes } && code == OpCodes.Ldsfld
                ? method.Module.ResolveField(BitConverter.ToInt32(bytes, operand), typeArguments, methodArguments)
                : null;

        MethodBase? Invokes(int index) =>
            index < decoded.Count && decoded[index] is { Code: var code, Operand: var operand, Bytes: var bytes } && (code == OpCodes.Call || code == OpCodes.Callvirt)
                ? method.Module.ResolveMethod(BitConverter.ToInt32(bytes, operand), typeArguments, methodArguments)
                : null;

        for (var index = 0; index < decoded.Count; index++)
        {
            var (at, instruction, operand, code) = decoded[index];

            if (instruction.OperandType == OperandType.InlineMethod)
            {
                var turnedOff = index + 3 < decoded.Count
                    && decoded[index + 1].Code == OpCodes.Dup
                    && Loads(index + 2) == NoHotKey
                    && Invokes(index + 3) is { Name: "set_HotKeySpecifier" };

                calls.Add((
                    method.Module.ResolveMethod(BitConverter.ToInt32(code, operand), typeArguments, methodArguments)!,
                    instruction != OpCodes.Ldftn && instruction != OpCodes.Ldvirtftn,
                    at,
                    index > 0 ? Loads(index - 1) : null,
                    instruction == OpCodes.Newobj && !turnedOff));
            }
        }

        return calls;
    }

    /// <summary>The generic arguments a method's tokens resolve against.</summary>
    /// <param name="method">The method.</param>
    /// <returns>Its type's and its own, or null for each that has none.</returns>
    private static (Type[]? Type, Type[]? Method) Arguments(MethodBase method) => (
        method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null,
        method.IsGenericMethod ? method.GetGenericArguments() : null);

    /// <summary>A method's compiled code, one instruction at a time.</summary>
    /// <param name="method">The method.</param>
    /// <returns>Each instruction's offset, its opcode, where its operand starts, and the code it is in.</returns>
    /// <remarks>
    /// Walks the instructions rather than looking for a byte, so an operand is never read as an instruction.
    /// </remarks>
    private static IEnumerable<(int At, OpCode Code, int Operand, byte[] Bytes)> Decoded(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            yield break;
        }

        for (var at = 0; at < code.Length;)
        {
            var value = code[at] == 0xFE ? unchecked((short)(0xFE00 | code[at + 1])) : code[at];
            var instruction = Codes[value];
            var start = at + instruction.Size;

            var operand = instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(code, start)),
                _ => 4,
            };

            yield return (at, instruction, start, code);

            at = start + operand;
        }
    }

    /// <summary>The stretches of a method's code that run only on some paths through it, or more than once.</summary>
    /// <param name="method">The method.</param>
    /// <returns>Each stretch, from its first offset to the offset after its last.</returns>
    /// <remarks>
    /// <para>
    /// Every branch, forwards or backwards, conditional or not, makes the code it jumps over conditional: an <c>if</c>
    /// jumps over what it guards, an <c>else</c> is jumped over by the end of what came before it, and a loop jumps back
    /// over its body. A <c>catch</c> runs only when something throws. The instruction a branch lands on is not inside,
    /// so the call a cached lambda is handed to is not conditional, though making the lambda is.
    /// </para>
    /// <para>
    /// It is stricter than what depends on a constructor's arguments, because it cannot follow where a value came from.
    /// Strict is the safe side: a binding nothing could have skipped is what the probe sees.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(int From, int To)> Conditional(MethodBase method)
    {
        var spans = new List<(int, int)>();

        foreach (var (at, instruction, operand, code) in Decoded(method))
        {
            var after = operand + instruction.OperandType switch
            {
                OperandType.ShortInlineBrTarget => 1,
                OperandType.InlineBrTarget => 4,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(code, operand)),
                _ => 0,
            };

            var targets = instruction.OperandType switch
            {
                OperandType.ShortInlineBrTarget => [after + (sbyte)code[operand]],
                OperandType.InlineBrTarget => [after + BitConverter.ToInt32(code, operand)],
                OperandType.InlineSwitch => Enumerable.Range(0, BitConverter.ToInt32(code, operand))
                    .Select(index => after + BitConverter.ToInt32(code, operand + 4 + (4 * index)))
                    .ToArray(),
                _ => Array.Empty<int>(),
            };

            foreach (var target in targets)
            {
                spans.Add(target > at ? (after, target) : (target, after));
            }
        }

        foreach (var clause in method.GetMethodBody()?.ExceptionHandlingClauses ?? [])
        {
            if (clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter)
            {
                spans.Add((clause.HandlerOffset, clause.HandlerOffset + clause.HandlerLength));
            }
        }

        return spans;
    }
}
