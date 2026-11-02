using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal's command tables refuse a key claimed twice in any place, and that the real tables are not refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review found the roadmaps pane and the message line both on Alt+e.</b> The status line and the View menu named
/// both, and the message line's never ran, because a pane's letter was searched first. The walks that held keys apart
/// compared one pane's own letters, in a fixture with no roadmap, so neither the pane nor the layer on top of it was
/// ever looked at together.
/// </para>
/// <para>
/// <b>A unit test of the real tables</b>, so a push fails before anybody starts the tool: the tool builds the same tables
/// before it takes the console and would refuse to start. The rest build a table with one claim added, the way a change
/// next month would.
/// </para>
/// </remarks>
[Unit]
public sealed class CommandTableTests
{
    private static KeyCommand<Shell> Extra(KeyContext context, char key, string name) =>
        new(context, key, name, _ => Task.CompletedTask, KeyCommand.Always);

    /// <summary>The surface's real table, as the terminal builds it.</summary>
    private static readonly CommandManager<Shell> Surface = CommandTables.Of<Shell>();

    private static CommandManager<Shell> With(params KeyCommand<Shell>[] extra) =>
        new(Surface.Places, [.. Surface.Commands, .. extra], Surface.Reserved, Surface.Handlers);

    [Fact]
    public void The_real_table_has_no_key_claimed_twice_in_any_place()
    {
        var table = CommandTables.Of<Shell>();

        // Non-vacuity: both halves of the clash that was found are in the table, in the places they were found in.
        Assert.Contains(table.Commands, c => c.Context == KeyContext.Plans && c.Key == PlanView.EditKey);
        Assert.Contains(table.Commands, c => c.Context == KeyContext.MessageLine && c.Key == Shell.WholeKey);

        // Both layers on every place, as the status line and the menus have always shown them: they read the same
        // layers the keys do now, so a place that dropped one would drop it from the screen as well.
        Assert.All(table.Places, place => Assert.Equal([KeyContext.MessageLine, KeyContext.Everywhere], place.Layers));
    }

    [Fact]
    public void Every_table_the_terminal_has_is_built_before_it_starts_and_every_place_is_in_one()
    {
        var places = CommandTables.BuildEvery();

        // A place declared and left out of every table is one whose dialog would be checked only when it opened.
        Assert.Equal(Enum.GetValues<KeyContext>().Except([KeyContext.MessageLine, KeyContext.Everywhere]).Order(), places.Order());
    }

    [Fact]
    public void A_refused_table_is_said_plainly_with_its_clash_and_an_exit_code_and_no_stack_trace()
    {
        using var error = new StringWriter();

        var code = CommandTables.Refused(error, () => With(Extra(KeyContext.Projects, Shell.ClearKey, "Clash")));

        Assert.Equal(CommandTables.Refusal, code);
        Assert.Contains("Clash", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(Shell.ClearTitle, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_tables_are_not_refused_and_say_nothing()
    {
        using var error = new StringWriter();

        Assert.Null(CommandTables.Refused(error));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Putting_the_message_lines_key_back_on_edit_refuses_naming_both_and_the_place()
    {
        var table = Surface.Commands
            .Select(c => c.Context == KeyContext.MessageLine && c.Key == Shell.WholeKey ? c with { Key = PlanView.EditKey } : c)
            .ToList();

        var refused = Assert.Throws<InvalidOperationException>(() => new CommandManager<Shell>(Surface.Places, table, Surface.Reserved, Surface.Handlers));

        Assert.Contains(Shell.WholeTitle, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Revising.Title, refused.Message, StringComparison.Ordinal);
        Assert.Contains($"in {nameof(KeyContext.Plans)}:", refused.Message, StringComparison.Ordinal);

        // The clash review did not report: on the agents pane, asking every agent what it owns had Alt+e as well, so the
        // message line's key was dead there too. Every clash is named at once.
        Assert.Contains(Ownership.AskEveryTitle, refused.Message, StringComparison.Ordinal);
        Assert.Contains($"in {nameof(KeyContext.Agents)}:", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_claims_its_key_on_every_place_it_is_live_in()
    {
        // `y` is health on the projects list and the conversations: a key that belongs to no pane cannot have it.
        var refused = Assert.Throws<InvalidOperationException>(() => With(Extra(KeyContext.Everywhere, 'y', "Anywhere")));

        Assert.Contains("Anywhere", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Alt+y", refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(KeyContext.Projects), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_a_place_does_not_list_is_neither_offered_nor_answered_there()
    {
        // The screen and the keys read one call, so they agree on which layers a place has: before, the status line read
        // each layer by name whatever the place listed, and the keys read the place's list.
        KeyCommand<string>[] commands =
        [
            new(KeyContext.Projects, 'a', "Own", _ => Task.CompletedTask, KeyCommand.Always),
            new(KeyContext.MessageLine, 'w', "Layered", _ => Task.CompletedTask, KeyCommand.Always),
        ];
        var table = new CommandManager<string>(
            [new(KeyContext.Projects, []), new(KeyContext.Agents, [KeyContext.MessageLine])], commands, []);

        Assert.Equal(["Own"], table.Offered(KeyContext.Projects, "x").Select(o => o.Action.Label));
        Assert.Null(table.Answering(KeyContext.Projects, "x", Key.W.WithAlt));
        Assert.False(table.Claims(KeyContext.Projects, Key.W.WithAlt));

        var agents = Assert.Single(table.Offered(KeyContext.Agents, "x"));
        Assert.Equal((KeyContext.MessageLine, "Layered"), (agents.From, agents.Action.Label));
        Assert.NotNull(table.Answering(KeyContext.Agents, "x", Key.W.WithAlt));
        Assert.True(table.Claims(KeyContext.Agents, Key.W.WithAlt));
    }

    [Fact]
    public void A_pane_command_on_a_message_line_key_is_refused_rather_than_left_to_win()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => With(Extra(KeyContext.Projects, Shell.ClearKey, "Clash")));

        Assert.Contains(Shell.ClearTitle, refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(KeyContext.Projects), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_letter_in_two_places_is_not_a_clash()
    {
        // `c` creates a project on the projects list and changes a setting in the configuration view: never live together.
        var table = With(Extra(KeyContext.Roadmaps, 'c', "Roadmaps only"));

        Assert.Equal(2, table.Commands.Count(c => c.Key == 'c' && c.Context is KeyContext.Projects or KeyContext.Configuration));
    }

    [Fact]
    public void A_letter_and_its_capital_are_one_key()
    {
        Assert.Throws<InvalidOperationException>(() => With(Extra(KeyContext.Plans, char.ToUpperInvariant(PlanView.EditKey), "Shouted")));
    }

    [Fact]
    public void A_chord_is_compared_with_the_letter_it_spells()
    {
        var chord = new KeyCommand<Shell>(
            KeyContext.Plans, default, "Chorded", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.E.WithAlt);

        Assert.Throws<InvalidOperationException>(() => With(chord));
    }

    [Fact]
    public void A_key_the_surface_keeps_for_itself_cannot_be_a_command()
    {
        var tab = new KeyCommand<Shell>(KeyContext.Agents, default, "Tabbed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Tab);

        var refused = Assert.Throws<InvalidOperationException>(() => With(tab));

        Assert.Contains(Shell.NextPaneName, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_on_a_key_a_pane_takes_first_is_refused_in_that_pane_only()
    {
        // Review's probe: a command on Enter in the room passed every check, was named on the screen and never ran,
        // because the room's list takes Enter through its own accept. The same command in the agents pane is Enter's
        // name there already; on the projects list it would be refused for the same reason as the room.
        var room = new KeyCommand<Shell>(KeyContext.Room, default, "Roomed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Enter);
        var refused = Assert.Throws<InvalidOperationException>(() => With(room));

        Assert.Contains($"in {nameof(KeyContext.Room)}:", refused.Message, StringComparison.Ordinal);

        // A list's own keys are the place's too: Home moves the cursor in every list, so a command there never runs.
        var home = new KeyCommand<Shell>(KeyContext.Conversations, default, "Homed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Home);

        Assert.Contains(Shell.ListName, Assert.Throws<InvalidOperationException>(() => With(home)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_views_that_take_one_key_are_not_a_clash_and_a_command_among_them_is()
    {
        // A dialog's list and its paragraph both move on the arrows; neither costs a command anything.
        ReservedKey[] reserved = [new(KeyContext.Projects, Key.Home, "a list"), new(KeyContext.Projects, Key.Home, "a paragraph")];

        _ = new CommandManager<string>([new(KeyContext.Projects, [])], [], reserved);

        var command = new KeyCommand<string>(KeyContext.Projects, default, "Homed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Home);

        Assert.Throws<InvalidOperationException>(() => new CommandManager<string>([new(KeyContext.Projects, [])], [command], reserved));
    }

    [Fact]
    public void A_dialog_is_held_to_the_same_check()
    {
        // Confirm and edit on one letter in the proposal dialog: the second would never run.
        var real = CommandTables.Of<ProposalView>();
        var table = real.Commands.Select(c => c.Name == "Edit" ? c with { Key = 'c' } : c).ToList();

        var refused = Assert.Throws<InvalidOperationException>(
            () => new CommandManager<ProposalView>(real.Places, table, real.Reserved));

        Assert.Contains($"in {nameof(KeyContext.ProposalDialog)}:", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Confirm", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_dialog_goes_back_on_escape_from_its_table()
    {
        Assert.Contains(CommandTables.Of<PlanView>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<ProposalView>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<TickDialog>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<ResponsibilityDialog>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<TextDialog>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<ChoiceDialog>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<FormDialog>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<Browsing>().Commands, c => Key.Esc.Equals(c.Chord));
        Assert.Contains(CommandTables.Of<LogView>().Commands, c => Key.Esc.Equals(c.Chord));
    }

    [Fact]
    public void A_refusals_first_line_says_what_kind_of_refusal_it_is()
    {
        // The first line once said two commands shared a key whatever the table was refused for.
        static string First(Func<object> build)
        {
            using var error = new StringWriter();

            Assert.Equal(CommandTables.Refusal, CommandTables.Refused(error, build));

            return error.ToString().Split(Environment.NewLine)[0];
        }

        const string Starts = "parley-terminal cannot start: ";
        var plain = new KeyCommand<Shell>(KeyContext.Agents, default, "Plain", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.J);
        var both = new KeyCommand<Shell>(KeyContext.Agents, 'j', "Both", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.J.WithAlt);
        var tab = new KeyCommand<Shell>(KeyContext.Agents, default, "Tabbed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Tab);
        var places = Surface.Places.Where(place => place.Context != KeyContext.Roadmaps).ToList();

        Assert.Equal(Starts + "a command is bound to a plain character.", First(() => With(plain)));
        Assert.Equal(Starts + "a command has both a letter and a chord, or neither.", First(() => With(both)));
        Assert.Equal(Starts + "a command is on a key taken before any command.", First(() => With(tab)));
        Assert.Equal(Starts + "two of its commands share a key.", First(() => With(Extra(KeyContext.Projects, Shell.ClearKey, "Clash"))));
        Assert.Equal(
            Starts + "two of its commands share a key, and a command is on a key taken before any command.",
            First(() => With(tab, Extra(KeyContext.Projects, Shell.ClearKey, "Clash"))));
        Assert.Equal(
            Starts + "a command is registered in a place nothing declares.",
            First(() => new CommandManager<Shell>(places, Surface.Commands, Surface.Reserved, Surface.Handlers)));
    }

    [Fact]
    public async Task The_terminal_refuses_a_clashing_table_before_it_asks_anything_of_the_console()
    {
        // The seam `RunAsync` goes through: whether there is a terminal is the first thing it asks of the console, so a
        // refusal that came after it would have been said on a console the toolkit could already have had.
        using var error = new StringWriter();
        var asked = false;

        var code = await TerminalCommand.RunAsync(
            null,
            null,
            error,
            () => With(Extra(KeyContext.Projects, Shell.ClearKey, "Clash")),
            // Answered "no terminal" so a build that asked first would stop there, not go on to take the console.
            () =>
            {
                asked = true;

                return false;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandTables.Refusal, code);
        Assert.False(asked);
        Assert.Contains("Clash", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_label_that_names_a_key_reads_it_from_its_dialogs_table()
    {
        Assert.Equal("Edit roadmap (" + KeyCommand.Stroke(CommandTables.Of<PlanView>().Commands, "Edit roadmap") + ")", PlanView.EditLabel);
        Assert.Equal("Edit roadmap (Alt+e)", PlanView.EditLabel);
        Assert.Equal("Close (Esc)", PlanView.CloseLabel);

        // One format on every button that names a key: the name, then the key in brackets.
        Assert.Equal("Confirm (Alt+c)", ProposalView.ConfirmLabel);
        Assert.Equal("Edit (Alt+e)", ProposalView.EditLabel);
        Assert.Equal("Decline (Alt+d)", ProposalView.DeclineLabel);
        Assert.Equal("Leave (Esc)", ProposalView.LeaveLabel);
        Assert.Equal("Space: tick   Alt+a: all   Alt+n: none   Enter: go   Esc: cancel", TickDialog.KeysLine(paged: false));
        Assert.Equal(
            "Space: tick   Alt+a: all   Alt+n: none   PageUp/PageDown: page   Enter: go   Esc: cancel", TickDialog.KeysLine(paged: true));
    }

    [Fact]
    public void A_dialogs_keys_run_from_its_table()
    {
        // Pressed at the dialog itself, in memory: Alt+a ticks every row, Alt+n none, and Escape gives up.
        using var ticking = new TickDialog("Choose", "Which.", [new Tickable("orders", string.Empty, false), new Tickable("billing", string.Empty, false)]);
        IReadOnlyList<Tickable>? done = [];
        ticking.Done += (_, chosen) => done = chosen;

        Assert.True(ticking.NewKeyDownEvent(Key.A.WithAlt));
        Assert.StartsWith("2 of 2 chosen.", ticking.Counted, StringComparison.Ordinal);

        Assert.True(ticking.NewKeyDownEvent(Key.N.WithAlt));
        Assert.StartsWith("0 of 2 chosen.", ticking.Counted, StringComparison.Ordinal);

        Assert.True(ticking.NewKeyDownEvent(Key.Esc));
        Assert.Null(done);
    }

    [Fact]
    public void A_plain_character_is_never_a_command()
    {
        var plain = new KeyCommand<Shell>(KeyContext.Agents, default, "Plain", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.J);

        Assert.Throws<InvalidOperationException>(() => With(plain));
    }

    [Fact]
    public void A_command_in_a_place_nothing_declares_is_refused()
    {
        var places = Surface.Places.Where(place => place.Context != KeyContext.Roadmaps).ToList();

        Assert.Throws<InvalidOperationException>(() => new CommandManager<Shell>(places, Surface.Commands, Surface.Reserved, Surface.Handlers));
    }

    [Fact]
    public void A_printable_mark_is_as_plain_as_a_letter()
    {
        // Typing a question mark into a box is typing, whatever the mark: the check once refused letters and digits only.
        var marked = new KeyCommand<Shell>(KeyContext.Agents, default, "Marked", _ => Task.CompletedTask, KeyCommand.Always, Chord: new Key('?'));
        var refused = Assert.Throws<InvalidOperationException>(() => With(marked));

        Assert.StartsWith("a command is bound to a plain character.", refused.Message, StringComparison.Ordinal);

        // Space, Enter and Escape are the chords a person presses unmodified, and are not refused for it.
        Assert.False(CommandManager<Shell>.Printable(Key.Space));
        Assert.False(CommandManager<Shell>.Printable(Key.Enter));
        Assert.False(CommandManager<Shell>.Printable(Key.Esc));
        Assert.True(CommandManager<Shell>.Printable(new Key('j')));
        Assert.True(CommandManager<Shell>.Printable(new Key('J')));
    }

    [Fact]
    public void A_key_the_surface_takes_itself_is_held_to_the_same_check()
    {
        static KeyHandler<string> Handler(KeyContext context, Key key, string name) => new(context, key, name, _ => true);

        KeyPlace[] places = [new(KeyContext.Projects, [])];
        var command = new KeyCommand<string>(KeyContext.Projects, default, "Commanded", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.F5);

        // On a command's key: the command would be named and the handler would never be reached, or the other way round.
        var under = Assert.Throws<InvalidOperationException>(
            () => new CommandManager<string>(places, [command], [], [Handler(KeyContext.Projects, Key.F5, "Handled")]));

        Assert.StartsWith("a command is on a key taken before any command.", under.Message, StringComparison.Ordinal);
        Assert.Contains("Handled", under.Message, StringComparison.Ordinal);

        // Two on one key: only one could ever run.
        var twice = Assert.Throws<InvalidOperationException>(
            () => new CommandManager<string>(places, [], [], [Handler(KeyContext.Projects, Key.F5, "One"), Handler(KeyContext.Projects, Key.F5, "Two")]));

        Assert.StartsWith("two keys the surface takes itself share a key.", twice.Message, StringComparison.Ordinal);

        // On a plain character, or in no place.
        Assert.StartsWith(
            "a key the surface takes is a plain character.",
            Assert.Throws<InvalidOperationException>(() => new CommandManager<string>(places, [], [], [Handler(KeyContext.Projects, new Key('x'), "Typed")])).Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "a key the surface takes is in a place nothing declares.",
            Assert.Throws<InvalidOperationException>(() => new CommandManager<string>(places, [], [], [Handler(KeyContext.Agents, Key.F5, "Nowhere")])).Message,
            StringComparison.Ordinal);

        // Beside a view's own reservation of the key it is not a clash: the view takes it while it has focus, and what it
        // declines reaches the handler.
        _ = new CommandManager<string>(places, [], [new(KeyContext.Projects, Key.F5, "a view")], [Handler(KeyContext.Projects, Key.F5, "Handled")]);
    }

    [Fact]
    public void A_key_is_answered_by_the_live_command_then_the_handler_then_kept_or_passed_on()
    {
        var ran = new List<string>();
        var live = false;
        KeyPlace[] places = [new(KeyContext.Projects, [])];
        var table = new CommandManager<string>(
            places,
            [
                new(KeyContext.Projects, default, "Back", _ => { ran.Add("back"); return Task.CompletedTask; }, _ => live, Chord: Key.Esc, KeepsKey: true),
                new(KeyContext.Projects, default, "Refresh", _ => { ran.Add("refresh"); return Task.CompletedTask; }, _ => live, Chord: Key.F5),
            ],
            [],
            [
                new(KeyContext.Projects, Key.Tab, "Moved", _ => { ran.Add("moved"); return true; }),
                new(KeyContext.Projects, Key.F6, "Declined", _ => { ran.Add("declined"); return false; }),
            ]);

        // Not live: Escape is kept and runs nothing, a command's other key is passed on, and a handler answers for itself.
        Assert.True(table.Take(KeyContext.Projects, "x", Key.Esc));
        Assert.False(table.Take(KeyContext.Projects, "x", Key.F5));
        Assert.True(table.Take(KeyContext.Projects, "x", Key.Tab));
        Assert.False(table.Take(KeyContext.Projects, "x", Key.F6));
        Assert.False(table.Take(KeyContext.Projects, "x", Key.F7));
        Assert.Equal(["moved", "declined"], ran);

        live = true;
        ran.Clear();

        Assert.True(table.Take(KeyContext.Projects, "x", Key.Esc));
        Assert.True(table.Take(KeyContext.Projects, "x", Key.F5));
        Assert.Equal(["back", "refresh"], ran);
    }

    [Fact]
    public void Escape_on_the_surface_is_kept_where_there_is_nowhere_to_go_back_to()
    {
        // Passed on from the top it would reach the toolkit, which ends the program on it.
        var back = Assert.Single(Surface.Commands, command => Key.Esc.Equals(command.Chord) && command.Context == KeyContext.Everywhere);

        Assert.True(back.KeepsKey);
        Assert.DoesNotContain(Surface.Commands, command => command.KeepsKey && command != back);
    }
}
