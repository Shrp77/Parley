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

    /// <summary>Every pane and dialog, built as the terminal builds it, and what its place claims.</summary>
    /// <returns>Each place, a view standing in it, whether the place claims a key, and whether a command there names it.</returns>
    /// <remarks>
    /// <para>
    /// <b>The panes are a real surface's</b>, built by <see cref="Parley.Terminal.Shell"/> and wired by it, so a binding or
    /// an accept handler the surface adds to a pane's view is pressed with the pane. Review found the panes built alone,
    /// as each region builds itself, and a binding the surface added to the agents list passed every test.
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
        // view would be passed on to it.
        foreach (var view in Inside(root).Where(view => view.HasFocus))
        {
            view.HasFocus = false;
        }

        return root;
    }

    /// <summary>Every dialog, and how to build it as the terminal does.</summary>
    /// <returns>Each dialog's place, how to build one, and whether the place claims a key and whether a command names it.</returns>
    private static IEnumerable<(KeyContext Place, Func<View> Build, (Func<Key, bool> Claims, Func<Key, bool> Names) Table)> Dialogs()
    {
        (Func<Key, bool>, Func<Key, bool>) Dialog<TDialog>(KeyContext place)
            where TDialog : IKeyTable<TDialog>
        {
            var table = CommandTables.Of<TDialog>();

            return (key => table.Claims(place, key), key => table.Names(place, key));
        }

        yield return (
            KeyContext.RoadmapDialog,
            () => new PlanView(new Roadmap(1, 1, "Ship it", default, null, 1, []), new RoadmapStanding([], NextKind.Complete, null, 0)),
            Dialog<PlanView>(KeyContext.RoadmapDialog));

        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);
        yield return (
            KeyContext.ProposalDialog,
            () => new ProposalView(new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, DateTimeOffset.UnixEpoch, null)),
            Dialog<ProposalView>(KeyContext.ProposalDialog));

        yield return (
            KeyContext.TickDialog,
            () => new TickDialog("Choose", "Which.", [new Tickable("orders", string.Empty, false)]),
            Dialog<TickDialog>(KeyContext.TickDialog));

        yield return (
            KeyContext.ResponsibilityDialog,
            () => new ResponsibilityDialog("What it owns.", "orders", canSave: true),
            Dialog<ResponsibilityDialog>(KeyContext.ResponsibilityDialog));

        yield return (KeyContext.TextDialog, () => new TextDialog("Name", "Which?", "alpha", null), Dialog<TextDialog>(KeyContext.TextDialog));
        yield return (KeyContext.ChoiceDialog, () => new ChoiceDialog("Pick", "Which?", ["one", "two"]), Dialog<ChoiceDialog>(KeyContext.ChoiceDialog));
        yield return (
            KeyContext.FormDialog,
            () => new FormDialog("Fill", [new FormField("Short:"), new FormField("Long:", Long: true)]),
            Dialog<FormDialog>(KeyContext.FormDialog));

        // Opened on the test's own build folder, which is small and always there: the walk reads the folder it opens.
        yield return (KeyContext.BrowsingDialog, () => new Browsing(AppContext.BaseDirectory), Dialog<Browsing>(KeyContext.BrowsingDialog));
        yield return (KeyContext.LogDialog, () => new LogView("following nothing"), Dialog<LogView>(KeyContext.LogDialog));
        yield return (KeyContext.HealthDialog, () => new Health("alpha", ["Work that needs you", "  Nothing is flagged."]), Dialog<Health>(KeyContext.HealthDialog));
        yield return (KeyContext.SayingDialog, () => new Saying("Said, whole.", 40), Dialog<Saying>(KeyContext.SayingDialog));
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
        var application = TheTerminal.GetTypes()
            .SelectMany(Bodies)
            .Where(body => Calls(body).Any(BindsForTheApplication))
            .Select(Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(application.Count == 0, "the application's keyboard or bindings reached:\n" + string.Join("\n", application));

        // **A binding, or a handler for what a key does, is added only while a pressed pane or dialog is built.** The
        // probe presses each place as building it, showing it a row and opening it leave it, and nothing else: a binding
        // the surface added to a pane's view, one a pane added when it was shown again, or one a dialog added in an
        // override the toolkit calls later would be a key no claim had to answer for. So such a call may be made only
        // in the constructor (field initialisers included) of a pane or dialog the probe presses, or in a method of the
        // same type that only that construction calls.
        var pressed = Everywhere().Select(place => place.View.GetType()).ToHashSet();
        var construction = Construction(pressed);
        var outside = new List<string>();
        var menuLines = 0;

        foreach (var body in TheTerminal.GetTypes().SelectMany(Bodies))
        {
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

                outside.Add($"{Name(body)}: {called.DeclaringType!.Name}.{called.Name}");
            }
        }

        Assert.True(menuLines > 0, "the menu's lines are no longer built by a call the walk sees; drop the exception");
        Assert.True(outside.Count == 0, "a key bound, or what a key does handled, outside the construction of a place the probe presses:\n" + string.Join("\n", outside.Distinct().Order(StringComparer.Ordinal)));

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
        var probes = Calls(typeof(KeysTakenBeforeTheTableTests).GetMethod(nameof(BindingAsReviewDid), Declared)!);

        Assert.Contains(probes, Binds);
        Assert.Contains(probes, BindsForTheApplication);
        Assert.Contains(probes, TakesAKey);
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
    /// </remarks>
    private static HashSet<MethodBase> Construction(IReadOnlySet<Type> pressed)
    {
        var callers = new Dictionary<MethodBase, List<MethodBase>>();
        var addressed = new HashSet<MethodBase>();

        foreach (var body in TheTerminal.GetTypes().SelectMany(Bodies))
        {
            foreach (var (called, invoked) in Instructions(body))
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

    /// <summary>The bindings lens 3's runs 144 and 145 added and no test saw, compiled here so the walk can be shown to see them.</summary>
    /// <param name="view">A view.</param>
    /// <param name="keyboard">The application's keyboard.</param>
    /// <remarks>Never called.</remarks>
    private static void BindingAsReviewDid(View view, IKeyboard keyboard)
    {
        view.HotKeyBindings.Add(Key.Z.WithCtrl, Command.Down);
        keyboard.KeyBindings.Add(Key.F2, Command.Quit);
        view.Accepting += (_, args) => args.Handled = true;
    }

    /// <summary>Whether a call reaches the application's keyboard or its bindings.</summary>
    /// <param name="called">The method called.</param>
    /// <returns>
    /// <see langword="true"/> for anything on the keyboard but subscribing to its events, the static application's
    /// keyboard or bindings, or an application binding added through a view's.
    /// </returns>
    private static bool BindsForTheApplication(MethodBase called) =>
        called.DeclaringType is { } type
        && ((typeof(IKeyboard).IsAssignableFrom(type) && !called.Name.StartsWith("add_", StringComparison.Ordinal) && !called.Name.StartsWith("remove_", StringComparison.Ordinal))
            || (type == typeof(Application) && called.Name is "get_Keyboard" or "get_KeyBindings")
            || (BindingsOfKeys(type) && called.Name == "AddApp"));

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

                // Accepting or activating alone is taken only where something answers it, and Enter by a default button
                // wherever it is pressed in the dialog.
                var answered = commands.All(command => command is Command.Accept or Command.Activate)
                    ? (commands.Contains(Command.Accept) && (Answers(view, Accepting) || (defaults && key == Key.Enter)))
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

                // Still nothing focused, or a later press would go somewhere other than the view it was pressed at.
                Assert.False(Inside(root).Any(inside => inside.HasFocus), $"{key} at {view.GetType().Name} gave a view focus");
            }
        }

        return taken;
    }

    private static bool Answers(View view, FieldInfo handler) => handler.GetValue(view) is not null;

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
    /// <returns>Each method, with <see langword="true"/> for a call and <see langword="false"/> for taking its address.</returns>
    private static IReadOnlyList<(MethodBase Method, bool Invoked)> Instructions(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            return [];
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var calls = new List<(MethodBase, bool)>();

        for (var at = 0; at < code.Length;)
        {
            var value = code[at] == 0xFE ? unchecked((short)(0xFE00 | code[at + 1])) : code[at];
            var instruction = Codes[value];

            at += instruction.Size;

            var operand = instruction.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(code, at)),
                _ => 4,
            };

            if (instruction.OperandType == OperandType.InlineMethod)
            {
                calls.Add((
                    method.Module.ResolveMethod(BitConverter.ToInt32(code, at), typeArguments, methodArguments)!,
                    instruction != OpCodes.Ldftn && instruction != OpCodes.Ldvirtftn));
            }

            at += operand;
        }

        return calls;
    }
}
