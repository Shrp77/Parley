using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the terminal's command table refuses a key claimed twice in any place, and that the real table is not refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Review found the roadmaps pane and the message line both on Alt+e.</b> The status line and the View menu named
/// both, and the message line's never ran, because a pane's letter was searched first. The walks that held keys apart
/// compared one pane's own letters, in a fixture with no roadmap, so neither the pane nor the layer on top of it was
/// ever looked at together.
/// </para>
/// <para>
/// <b>A unit test of the real table</b>, so a push fails before anybody starts the tool: the surface builds the same
/// table in its constructor and would refuse to start. The rest build a table with one claim added, the way a change
/// next month would.
/// </para>
/// </remarks>
[Unit]
public sealed class CommandTableTests
{
    private static KeyCommand Extra(KeyContext context, char key, string name) =>
        new(context, key, name, _ => Task.CompletedTask, KeyCommand.Always);

    private static CommandManager With(params KeyCommand[] extra) =>
        new(Shell.Places, [.. Shell.Registered, .. extra], Shell.Reserved);

    [Fact]
    public void The_real_table_has_no_key_claimed_twice_in_any_place()
    {
        var table = new CommandManager(Shell.Places, Shell.Registered, Shell.Reserved);

        // Non-vacuity: both halves of the clash that was found are in the table, in the places they were found in.
        Assert.Contains(table.Commands, c => c.Context == KeyContext.Plans && c.Key == PlanView.EditKey);
        Assert.Contains(table.Commands, c => c.Context == KeyContext.MessageLine && c.Key == Shell.WholeKey);
        Assert.All(table.Places, place => Assert.Contains(KeyContext.MessageLine, place.Layers));
    }

    [Fact]
    public void Putting_the_message_lines_key_back_on_edit_refuses_naming_both_and_the_place()
    {
        var table = Shell.Registered
            .Select(c => c.Context == KeyContext.MessageLine && c.Key == Shell.WholeKey ? c with { Key = PlanView.EditKey } : c)
            .ToList();

        var refused = Assert.Throws<InvalidOperationException>(() => new CommandManager(Shell.Places, table, Shell.Reserved));

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
        var chord = new KeyCommand(
            KeyContext.Plans, default, "Chorded", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.E.WithAlt);

        Assert.Throws<InvalidOperationException>(() => With(chord));
    }

    [Fact]
    public void A_key_the_surface_keeps_for_itself_cannot_be_a_command()
    {
        var tab = new KeyCommand(KeyContext.Agents, default, "Tabbed", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.Tab);

        var refused = Assert.Throws<InvalidOperationException>(() => With(tab));

        Assert.Contains("the next pane", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_character_is_never_a_command()
    {
        var plain = new KeyCommand(KeyContext.Agents, default, "Plain", _ => Task.CompletedTask, KeyCommand.Always, Chord: Key.J);

        Assert.Throws<InvalidOperationException>(() => With(plain));
    }

    [Fact]
    public void A_command_in_a_place_nothing_declares_is_refused()
    {
        var places = Shell.Places.Where(place => place.Context != KeyContext.Roadmaps).ToList();

        Assert.Throws<InvalidOperationException>(() => new CommandManager(places, Shell.Registered, Shell.Reserved));
    }
}
