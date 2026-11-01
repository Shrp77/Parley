using System.Reflection;
using System.Text.RegularExpressions;
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That every key taken before a command is asked is claimed in the command table, in the place that takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review found the room and the conversations taking Enter through their lists' own accept</b>, so Enter never
/// reached the surface there and the table had no claim on it. A command added on Enter in either passed every check,
/// was named on the status line and never ran. The check that should have caught it asked only whether a key appeared
/// anywhere in the table, and Enter did, as a chord in another pane.
/// </para>
/// <para>
/// <b>So the places are found, not assumed.</b> Each pane and each dialog is built in memory and every key it could take
/// is put to its views: what they bind, what their accept and activate handlers answer, and what their own key handlers
/// take when it is pressed. The surface's own handler is read from its source, each arm's place from its condition.
/// Every key found must be claimed in that place, or in every place when the arm names none.
/// </para>
/// </remarks>
[Unit]
public sealed class KeysTakenBeforeTheTableTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");

    /// <summary>The keys of <c>OnKeyDown</c>'s comparisons, by how the source writes them.</summary>
    private static readonly Dictionary<string, Key> Named = new(StringComparer.Ordinal)
    {
        ["Key.Tab"] = Key.Tab,
        ["Key.Tab.WithShift"] = Key.Tab.WithShift,
        ["Key.CursorUp"] = Key.CursorUp,
        ["Key.CursorDown"] = Key.CursorDown,
        ["Key.Esc"] = Key.Esc,
        ["Key.Enter"] = Key.Enter,
        ["QuitKey"] = Shell.QuitKey,
        ["BrowserKey"] = Shell.BrowserKey,
        ["MenuKey"] = Shell.MenuKey,
    };

    /// <summary>A view's accept and activate handlers, read because the toolkit offers no other way to ask.</summary>
    private static readonly FieldInfo Accepting = typeof(View).GetField("Accepting", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo Activating = typeof(View).GetField("Activating", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly CommandManager<Shell> Surface = new(Shell.Places, Shell.Registered, Shell.Reserved);

    /// <summary>Every pane and dialog, built as the terminal builds it, and what its place claims.</summary>
    /// <returns>Each place, a view standing in it, and whether the place claims a key.</returns>
    private static IEnumerable<(KeyContext Place, View View, Func<Key, bool> Claims)> Everywhere()
    {
        Func<Key, bool> Shell(KeyContext place) => key => Surface.Claims(place, key);

        yield return (KeyContext.Projects, new ProjectsRegion(), Shell(KeyContext.Projects));
        yield return (KeyContext.Agents, new AgentsRegion(), Shell(KeyContext.Agents));
        yield return (KeyContext.Conversations, new ConversationsRegion(), Shell(KeyContext.Conversations));
        yield return (KeyContext.Transcript, new TranscriptRegion(), Shell(KeyContext.Transcript));
        yield return (KeyContext.Room, new RoomRegion(), Shell(KeyContext.Room));
        yield return (KeyContext.Line, new LineRegion(), Shell(KeyContext.Line));
        yield return (KeyContext.Plans, new PlansRegion(), Shell(KeyContext.Plans));
        yield return (KeyContext.Roadmaps, new RoadmapsRegion(), Shell(KeyContext.Roadmaps));
        yield return (KeyContext.Configuration, new ConfigurationRegion(), Shell(KeyContext.Configuration));

        var roadmap = new CommandManager<PlanView>(PlanView.Places, PlanView.Registered, PlanView.Reserved);
        yield return (
            KeyContext.RoadmapDialog,
            new PlanView(new Roadmap(1, 1, "Ship it", default, null, 1, []), new RoadmapStanding([], NextKind.Complete, null, 0)),
            key => roadmap.Claims(KeyContext.RoadmapDialog, key));

        var proposal = new CommandManager<ProposalView>(ProposalView.Places, ProposalView.Registered, ProposalView.Reserved);
        var content = new RoadmapContent("Ship it", [new RoadmapItemDraft(null, "Write the notes", Orders, [])]);
        yield return (
            KeyContext.ProposalDialog,
            new ProposalView(new RoadmapProposal(1, Orders, null, "Ship it", content, 10, ProposalState.Waiting, DateTimeOffset.UnixEpoch, null)),
            key => proposal.Claims(KeyContext.ProposalDialog, key));

        var ticking = new CommandManager<TickDialog>(TickDialog.Places, TickDialog.Registered, TickDialog.Reserved);
        yield return (
            KeyContext.TickDialog,
            new TickDialog("Choose", "Which.", [new Tickable("orders", string.Empty, false)]),
            key => ticking.Claims(KeyContext.TickDialog, key));

        var responsibility = new CommandManager<ResponsibilityDialog>(
            ResponsibilityDialog.Places, ResponsibilityDialog.Registered, ResponsibilityDialog.Reserved);
        yield return (
            KeyContext.ResponsibilityDialog,
            new ResponsibilityDialog("What it owns.", "orders", canSave: true),
            key => responsibility.Claims(KeyContext.ResponsibilityDialog, key));

        var text = new CommandManager<TextDialog>(TextDialog.Places, TextDialog.Registered, TextDialog.Reserved);
        yield return (KeyContext.TextDialog, new TextDialog("Name", "Which?", "alpha", null), key => text.Claims(KeyContext.TextDialog, key));

        var choice = new CommandManager<ChoiceDialog>(ChoiceDialog.Places, ChoiceDialog.Registered, ChoiceDialog.Reserved);
        yield return (KeyContext.ChoiceDialog, new ChoiceDialog("Pick", "Which?", ["one", "two"]), key => choice.Claims(KeyContext.ChoiceDialog, key));

        var form = new CommandManager<FormDialog>(FormDialog.Places, FormDialog.Registered, FormDialog.Reserved);
        yield return (
            KeyContext.FormDialog,
            new FormDialog("Fill", [new FormField("Short:"), new FormField("Long:", Long: true)]),
            key => form.Claims(KeyContext.FormDialog, key));
    }

    [Fact]
    public void Every_key_a_pane_or_dialog_takes_is_claimed_in_its_place()
    {
        var missing = new List<string>();
        var visited = new HashSet<KeyContext>();

        foreach (var (place, root, claims) in Everywhere())
        {
            using (root)
            {
                visited.Add(place);

                foreach (var (key, by) in Taken(root).Where(taken => !claims(taken.Key)))
                {
                    missing.Add($"{place}: {key} (taken by {by})");
                }
            }
        }

        // Non-vacuity: every place with a view was stood in, and the handlers this rests on are where it looks for them.
        Assert.Equal(Enum.GetValues<KeyContext>().Except([KeyContext.Elsewhere, KeyContext.MessageLine, KeyContext.Everywhere]).Order(), visited.Order());
        Assert.NotNull(Accepting);
        Assert.NotNull(Activating);

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
    }

    [Fact]
    public void Every_key_the_surfaces_own_handler_takes_is_claimed_in_the_place_its_arm_names()
    {
        var arms = Arms();
        var missing = new List<string>();

        foreach (var (name, place) in arms)
        {
            Assert.True(Named.TryGetValue(name, out var key), $"OnKeyDown compares {name}; say here which key it is");

            // An arm that names no place takes its key in every one.
            var where = place is { } one ? [one] : Surface.Places.Select(p => p.Context).ToArray();

            missing.AddRange(where.Where(at => !Surface.Claims(at, key!)).Select(at => $"{at}: {name}"));
        }

        // Non-vacuity: the arms were read, with their places, including the two Enters only one pane takes.
        Assert.Contains(("Key.Enter", (KeyContext?)KeyContext.Configuration), arms);
        Assert.Contains(("Key.Enter", (KeyContext?)KeyContext.Transcript), arms);
        Assert.Contains(("Key.CursorUp", (KeyContext?)KeyContext.Line), arms);
        Assert.Contains(("MenuKey", (KeyContext?)null), arms);

        Assert.True(missing.Count == 0, "OnKeyDown takes these, and the place does not claim them: " + string.Join(", ", missing));
    }

    /// <summary>Every key a view or anything inside it takes, and which view takes it.</summary>
    /// <param name="root">The pane or dialog.</param>
    /// <returns>The keys.</returns>
    private static IReadOnlyList<(Key Key, string By)> Taken(View root)
    {
        var taken = new List<(Key, string)>();
        var defaults = Inside(root).OfType<Button>().Any(button => button.IsDefault);

        // Pressed one view at a time, so nothing focused inside may route a key onward: a view out of an application
        // has no focus, and a key that reached a box's copy or paste would reach the machine's clipboard.
        Assert.DoesNotContain(Inside(root), view => view.HasFocus);

        // A view that cannot have focus is never offered a key: the room's completion list, a dialog's heading.
        foreach (var view in Inside(root).Where(view => view == root || view.CanFocus))
        {
            var by = view.GetType().Name;

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
                    taken.Add((key, by));
                }
            }

            // What a view's own key handler takes, found by pressing it. A key bound by this view, or by one inside it
            // that a key pressed here can be passed on to, is never pressed: it is decided above, a binding to copy or
            // paste would reach the machine's clipboard, and activating a view gives it focus. A key handler that takes a
            // bound key before its binding is therefore not seen here; the dialog's own commands are the only ones.
            var bound = Inside(view)
                .SelectMany(inside => inside.KeyBindings.GetBindings())
                .Select(binding => binding.Key)
                .ToHashSet();

            foreach (var key in Candidates().Where(key => !bound.Contains(key)))
            {
                // A fresh key every time: a key carries whether it was handled, and one made from a handled key with
                // Shift added arrives handled already.
                if (view.NewKeyDownEvent(new Key(key.KeyCode)))
                {
                    taken.Add((key, by));
                }

                // Still nothing focused, or a later press would go somewhere other than the view it was pressed at.
                Assert.False(Inside(root).Any(inside => inside.HasFocus), $"{key} at {by} gave a view focus");
            }
        }

        return taken;
    }

    private static bool Answers(View view, FieldInfo handler) => handler.GetValue(view) is not null;

    private static IEnumerable<View> Inside(View root) => [root, .. root.SubViews.SelectMany(Inside)];

    /// <summary>Every key a person could mean as an action: no plain letter, digit or mark, which is typing.</summary>
    /// <remarks>
    /// Tab is not pressed: it moves focus between views, which every place reserves, and pressed at a view in memory it
    /// would give a view focus and send every later key somewhere else.
    /// </remarks>
    private static IEnumerable<Key> Candidates()
    {
        Key[] plain =
        [
            Key.Enter, Key.Space, Key.Esc, Key.Backspace, Key.Delete, Key.InsertChar, Key.Home, Key.End, Key.PageUp,
            Key.PageDown, Key.CursorUp, Key.CursorDown, Key.CursorLeft, Key.CursorRight,
            Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
        ];

        foreach (var key in plain)
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
    }

    /// <summary>Every key <c>Shell.OnKeyDown</c> compares against, with the place its arm names.</summary>
    /// <returns>Each key as written, and its place, or null when the arm names none.</returns>
    private static IReadOnlyList<(string Key, KeyContext? Place)> Arms()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Parley.Terminal", "Shell.cs"));
        var from = source.IndexOf("protected override bool OnKeyDown", StringComparison.Ordinal);

        Assert.True(from > 0, "OnKeyDown is not where this test expects it");

        var body = source[from..];
        var ends = body.IndexOf("\n    }", StringComparison.Ordinal);

        Assert.True(ends > 0, "OnKeyDown no longer ends the way this test expects");

        var conditions = Regex.Matches(
            body[..ends],
            @"if \((?<c>(?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!)))\)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        var arms = new List<(string, KeyContext?)>();

        foreach (Match condition in conditions)
        {
            var text = condition.Groups["c"].Value;
            var place = Regex.Match(text, @"Context == KeyContext\.(\w+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)) is { Success: true } named
                ? Enum.Parse<KeyContext>(named.Groups[1].Value)
                : text.Contains("Line.Editing", StringComparison.Ordinal) ? KeyContext.Line : (KeyContext?)null;

            arms.AddRange(Regex
                .Matches(text, @"key == ([A-Za-z][A-Za-z0-9.]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
                .Select(match => (match.Groups[1].Value, place)));
        }

        return arms;
    }
}
