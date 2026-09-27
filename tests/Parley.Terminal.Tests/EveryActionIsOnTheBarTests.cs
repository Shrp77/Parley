using Grpc.Net.Client;
using Parley.Client;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That the menu bar is a reading of the action list and never a second list.
/// </summary>
/// <remarks>
/// <para>
/// <b>021's D1, and the one thing that would make a menu bar a liability.</b> Two hand-written lists
/// agree until one is edited, which is this repository's most-repeated defect — so the bar must derive
/// everything it shows from the same actions the status line and the <c>?</c> dialog read. Nothing here
/// asserts that a particular label exists; what is asserted is that no action can be missing and that
/// nothing is invented.
/// </para>
/// <para>
/// <b>And that nothing lands in <c>Other</c>.</b> An unclassified action has to reach the screen,
/// because no action on this surface exists only as a keystroke — but filing one under a category
/// nobody chose would be the bar inventing a fact, so it goes visibly into <c>Other</c> and this fails
/// the build when anything does. That is the whole reason <c>Category</c> is nullable rather than
/// defaulted.
/// </para>
/// </remarks>
public sealed class EveryActionIsOnTheBarTests
{
    private static BrokerDirectory Directory() =>
        new(
            [new ProjectSettings(ProjectName.Create("alpha"), "a.db", "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));

    private static AgentRecord Agent(string name) =>
        new(
            AgentName.Create(name),
            "mcp",
            AgentTier.Pull,
            "/src/thing",
            null,
            null,
            false,
            AgentStatus.Active,
            "the thing",
            AgentPresence.Running);

    [Fact]
    public async Task Every_action_the_focused_region_offers_is_on_the_bar()
    {
        // **The property that makes the bar safe.** Must fail against a bar built from its own list,
        // which is what every menu bar in every other program is and what this one must not be: the
        // moment an action is added, renamed or made conditional, a second list is wrong and nothing
        // says so.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            var actions = shell.Actions();
            var onTheBar = MenuRow.Menus(actions)
                .SelectMany(menu => menu.Items)
                .Where(item => !item.Divides)
                .Select(item => item.Action!)
                .ToList();

            Assert.Equal(actions.Count, onTheBar.Count);

            foreach (var action in actions)
            {
                Assert.Contains(action, onTheBar);
            }
        }
    }

    [Fact]
    public async Task Nothing_reaches_the_bar_that_no_region_offers()
    {
        // The other direction, and it is not symmetrical with the one above: a bar that invented an
        // item would offer a door to something no key takes, which is how the conversations list once
        // offered an action that did nothing at all.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            var actions = shell.Actions();

            foreach (var item in MenuRow.Menus(actions).SelectMany(menu => menu.Items))
            {
                if (item.Divides)
                {
                    continue;
                }

                Assert.Contains(item.Action!, actions);
            }
        }
    }

    [Fact]
    public async Task No_action_is_unclassified()
    {
        // **Must fail the moment somebody adds an action without saying what it is about.** The
        // category is nullable so that this can be a build failure rather than a menu quietly growing
        // an `Other` heading nobody meant, and the region supplies a default so that stating it is
        // rare rather than routine.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        await foreach (var _ in Walking.EveryPaneAsync(shell))
        {
            var unclassified = shell.Actions().Where(action => action.Category is null).ToList();

            Assert.Empty(unclassified);

            Assert.DoesNotContain(
                MenuRow.Menus(shell.Actions()),
                menu => menu.Title == MenuRow.Unclassified);
        }
    }

    [Fact]
    public async Task The_message_lines_own_actions_are_classified_too()
    {
        // `Messaging()` is a second source of actions -- global rather than a region's, because
        // clearing what the surface last said belongs to no pane. It reaches the `?` menu, so it
        // reaches this rule as well.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);
        await shell.RefreshAsync(TestContext.Current.CancellationToken);
        await shell.Settled;

        Assert.All(shell.Messaging(), action => Assert.NotNull(action.Category));
    }

    [Fact]
    public void A_menu_with_nothing_under_it_is_absent_rather_than_empty()
    {
        // An empty menu is a door to nowhere. Must fail against emitting every category regardless,
        // which is the obvious implementation when the categories are an enum.
        var menus = MenuRow.Menus(
            [new RegionAction('x', "Something", () => Task.CompletedTask, Category: ActionCategory.View)]);

        var only = Assert.Single(menus);

        Assert.Equal("View", only.Title);
    }

    [Fact]
    public void Items_are_ordered_by_their_group_and_then_within_it()
    {
        // D1's rendering rule, and the reason `ActionGroup` is a record: the run's position is a
        // property of the run, so two actions in one run cannot disagree about where it sits.
        var late = new ActionGroup("Late", 2);
        var early = new ActionGroup("Early", 1);

        var menus = MenuRow.Menus(
        [
            new RegionAction('a', "Second in late", () => Task.CompletedTask,
                Category: ActionCategory.View, Group: late, Order: 2),
            new RegionAction('b', "First in early", () => Task.CompletedTask,
                Category: ActionCategory.View, Group: early, Order: 1),
            new RegionAction('c', "First in late", () => Task.CompletedTask,
                Category: ActionCategory.View, Group: late, Order: 1),
            new RegionAction('d', "Second in early", () => Task.CompletedTask,
                Category: ActionCategory.View, Group: early, Order: 2),
        ]);

        var items = Assert.Single(menus).Items;

        Assert.Equal(
            ["First in early", "Second in early", string.Empty, "First in late", "Second in late"],
            items.Select(item => item.Label));

        // One separator, where the group changed, and not between members of one group.
        Assert.Single(items, item => item.Divides);

        // The rule is a line and never something to choose: it carries no action, so nothing can
        // dispatch it, and a menu that let somebody select it would do nothing and look broken.
        Assert.True(MenuItem.Separator.Divides);
        Assert.Null(MenuItem.Separator.Action);
    }

    [Fact]
    public async Task What_the_keys_are_and_which_build_this_is_belong_to_no_region()
    {
        // **D2 scopes the bar to the focused region, and these two are the exception that proves the
        // rule rather than a hole in it.** What the keys are, and which build this is, are about neither
        // a region nor a selection -- scoping them would put Help under six menus or under none.
        //
        // Must fail against them being absent, which is the state 021 exists to correct: the actions
        // were discoverable only to somebody who already knew to press `?`.
        using var driving = Driving.Open();
        using var directory = Directory();
        using var shell = new Shell(directory);

        driving.Show(shell);

        var help = shell.Global();

        Assert.NotEmpty(help);
        Assert.All(help, action => Assert.Equal(ActionCategory.Help, action.Category));

        var menu = Assert.Single(MenuRow.Menus(help));

        Assert.Equal("Help", menu.Title);
        Assert.Contains(menu.Items, item => item.Label == Shell.KeysTitle);
        Assert.Contains(menu.Items, item => item.Label == Shell.AboutTitle);

        await Task.CompletedTask;
    }

    [Fact]
    public void An_ungrouped_action_sinks_below_the_grouped_ones()
    {
        // Rather than displacing them or being dropped. A new action must reach the screen from the
        // moment it exists, and nobody has to group it first.
        var menus = MenuRow.Menus(
        [
            new RegionAction('a', "Ungrouped", () => Task.CompletedTask, Category: ActionCategory.View),
            new RegionAction('b', "Grouped", () => Task.CompletedTask,
                Category: ActionCategory.View, Group: new ActionGroup("Run", 1)),
        ]);

        var items = Assert.Single(menus).Items.Where(item => !item.Divides).ToList();

        Assert.Equal(["Grouped", "Ungrouped"], items.Select(item => item.Label));
    }

    [Fact]
    public void The_key_travels_with_the_label_and_the_cost_is_written_out()
    {
        // A menu that hid the key would teach somebody to reach for the mouse for something one
        // keystroke does. The cost belongs here rather than on the status line, which 0.25.4 settled:
        // two clauses there is fifty columns that push every action behind `?`.
        var menus = MenuRow.Menus(
        [
            new RegionAction('k', "Ask somebody", () => Task.CompletedTask, "opens an exchange",
                Category: ActionCategory.Conversation),
        ]);

        var item = Assert.Single(Assert.Single(menus).Items);

        Assert.Equal("k", item.Stroke);
        Assert.Contains("Ask somebody", item.Label, StringComparison.Ordinal);
        Assert.Contains("opens an exchange", item.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chord_is_written_as_the_chord_rather_than_as_a_letter()
    {
        // The room's input line made chords necessary, because every letter there belongs to the text
        // somebody is typing. A bar showing a blank key for those would be worse than showing none.
        var menus = MenuRow.Menus(
        [
            new RegionAction(default, "Break the line", () => Task.CompletedTask, null,
                Key.Enter.WithCtrl, ActionCategory.Conversation),
        ]);

        var item = Assert.Single(Assert.Single(menus).Items);

        Assert.NotEmpty(item.Stroke);
        Assert.NotEqual("\0", item.Stroke);
    }
}
