using System.Reflection;
using System.Reflection.Emit;
using Parley.Client.Roadmaps;
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
/// compiled code and must be one of the few in <see cref="KeyRouting"/>; and every view that can be opened as a session
/// must build a table.
/// </para>
/// <para>
/// <b>And the places are pressed, not assumed.</b> Review found the room and the conversations taking Enter through
/// their lists' own accept, so a command added on Enter there was named on the status line and never ran. Each pane and
/// each dialog is built in memory and every key it could take is put to its views: what they bind, what their accept and
/// activate handlers answer, and what their own key handlers take when it is pressed. Every key found must be claimed in
/// that place, and a plain character taken by a view that does not type is a failure whatever claims it.
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
    /// <returns>Each place, a view standing in it, and whether the place claims a key.</returns>
    private static IEnumerable<(KeyContext Place, View View, Func<Key, bool> Claims)> Everywhere()
    {
        Func<Key, bool> Shell(KeyContext place) => key => Surface.Claims(place, key);

        Func<Key, bool> Dialog<TDialog>(KeyContext place)
            where TDialog : IKeyTable<TDialog>
        {
            var table = CommandTables.Of<TDialog>();

            return key => table.Claims(place, key);
        }

        yield return (KeyContext.Projects, new ProjectsRegion(), Shell(KeyContext.Projects));
        yield return (KeyContext.Agents, new AgentsRegion(), Shell(KeyContext.Agents));
        yield return (KeyContext.Conversations, new ConversationsRegion(), Shell(KeyContext.Conversations));
        yield return (KeyContext.Transcript, new TranscriptRegion(), Shell(KeyContext.Transcript));
        yield return (KeyContext.Room, new RoomRegion(), Shell(KeyContext.Room));
        yield return (KeyContext.Line, new LineRegion(), Shell(KeyContext.Line));
        yield return (KeyContext.Plans, new PlansRegion(), Shell(KeyContext.Plans));
        yield return (KeyContext.Roadmaps, new RoadmapsRegion(), Shell(KeyContext.Roadmaps));
        yield return (KeyContext.Configuration, new ConfigurationRegion(), Shell(KeyContext.Configuration));

        yield return (
            KeyContext.RoadmapDialog,
            new PlanView(new Roadmap(1, 1, "Ship it", default, null, 1, []), new RoadmapStanding([], NextKind.Complete, null, 0)),
            Dialog<PlanView>(KeyContext.RoadmapDialog));

        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);
        yield return (
            KeyContext.ProposalDialog,
            new ProposalView(new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, DateTimeOffset.UnixEpoch, null)),
            Dialog<ProposalView>(KeyContext.ProposalDialog));

        yield return (
            KeyContext.TickDialog,
            new TickDialog("Choose", "Which.", [new Tickable("orders", string.Empty, false)]),
            Dialog<TickDialog>(KeyContext.TickDialog));

        yield return (
            KeyContext.ResponsibilityDialog,
            new ResponsibilityDialog("What it owns.", "orders", canSave: true),
            Dialog<ResponsibilityDialog>(KeyContext.ResponsibilityDialog));

        yield return (KeyContext.TextDialog, new TextDialog("Name", "Which?", "alpha", null), Dialog<TextDialog>(KeyContext.TextDialog));
        yield return (KeyContext.ChoiceDialog, new ChoiceDialog("Pick", "Which?", ["one", "two"]), Dialog<ChoiceDialog>(KeyContext.ChoiceDialog));
        yield return (
            KeyContext.FormDialog,
            new FormDialog("Fill", [new FormField("Short:"), new FormField("Long:", Long: true)]),
            Dialog<FormDialog>(KeyContext.FormDialog));

        // Opened on the test's own build folder, which is small and always there: the walk reads the folder it opens.
        yield return (KeyContext.BrowsingDialog, new Browsing(AppContext.BaseDirectory), Dialog<Browsing>(KeyContext.BrowsingDialog));
        yield return (KeyContext.LogDialog, new LogView("following nothing"), Dialog<LogView>(KeyContext.LogDialog));
        yield return (KeyContext.HealthDialog, new Health("alpha", ["Work that needs you", "  Nothing is flagged."]), Dialog<Health>(KeyContext.HealthDialog));
        yield return (KeyContext.SayingDialog, new Saying("Said, whole.", 40), Dialog<Saying>(KeyContext.SayingDialog));
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

        foreach (var (_, root, _) in Everywhere())
        {
            using (root)
            {
                pressed.Add(root.GetType());
            }
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
    public void Every_key_a_pane_or_dialog_takes_is_claimed_in_its_place()
    {
        var missing = new List<string>();
        var plain = new List<string>();
        var visited = new HashSet<KeyContext>();

        foreach (var (place, root, claims) in Everywhere())
        {
            using (root)
            {
                visited.Add(place);

                foreach (var (key, by) in Taken(root))
                {
                    // A plain character is never an action: where nothing types, a view that takes one is a key a
                    // person types by accident, and no claim makes it otherwise.
                    if (CommandManager<Shell>.Printable(key) && !Types(by))
                    {
                        plain.Add($"{place}: {key} (taken by {by.GetType().Name})");
                    }
                    else if (!claims(key))
                    {
                        missing.Add($"{place}: {key} (taken by {by.GetType().Name})");
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

        // A view that cannot have focus is never offered a key: the room's completion list, a dialog's heading.
        foreach (var view in Inside(root).Where(view => view == root || view.CanFocus))
        {
            foreach (var (key, binding) in view.KeyBindings.GetBindings())
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
                .SelectMany(inside => inside.KeyBindings.GetBindings())
                .Select(binding => binding.Key)
                .ToHashSet();

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

    /// <summary>A method as the assertions name it: its declaring type, outside any compiler-made one, and its name.</summary>
    /// <param name="method">The method.</param>
    /// <returns>The name.</returns>
    private static string Name(MethodBase method)
    {
        var type = method.DeclaringType!;

        while (type.IsNested && type.Name.Contains('<', StringComparison.Ordinal))
        {
            type = type.DeclaringType!;
        }

        return type.Name + "." + method.Name;
    }

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
    private static IReadOnlyList<MethodBase> Calls(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } code)
        {
            return [];
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var calls = new List<MethodBase>();

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
                calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(code, at), typeArguments, methodArguments)!);
            }

            at += operand;
        }

        return calls;
    }
}
