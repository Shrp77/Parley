using Parley.Client.Views;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// That no pane offers two actions on one key.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key handler runs the first action whose key matches, so the second one on a key is an action the
/// surface names and never runs.</b> The agents pane had two: <c>a</c> was both adding a repository and asking
/// the selected agent what it owns, so the ask could not be reached by its key at all; and <c>w</c> was both
/// what was told to the selected agent and taking delivery for a held seat. Both pairs were on the menu and the
/// status line side by side, and nothing in the suite noticed, because every test ran an action by its label.
/// </para>
/// <para>
/// <b>Driven into the richest state each pane has</b> — a seat held and a peer selected — because the offers
/// are conditional, and a clash that only appears once both conditions hold is exactly the one a fixture with
/// neither would miss. Found by the owner asking where enrolment had gone and a letter being chosen for it.
/// </para>
/// </remarks>
public sealed class NoTwoActionsShareAKeyTests
{
    [Fact]
    public async Task No_pane_in_a_project_offers_two_actions_on_one_key()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);
        var shell = seated.Shell;

        await Walking.GoAsync(shell, Viewing.Project);
        await shell.Settled;

        seated.Focus<AgentsRegion>();
        seated.Session.Row(shell.Agents.List, "orders@test");

        Assert.NotNull(shell.Agents.Selected);
        Assert.NotNull(shell.Held);

        EveryPaneIsDistinct(shell);
    }

    [Fact]
    public async Task No_pane_in_a_conversation_offers_two_actions_on_one_key()
    {
        await using var seated = await Seated.OpenAsync(TestContext.Current.CancellationToken);

        EveryPaneIsDistinct(seated.Shell);
    }

    private static void EveryPaneIsDistinct(Shell shell)
    {
        for (var pane = 0; pane < shell.Panes.Count; pane++)
        {
            shell.Focus(pane);

            var clashes = shell.Actions()
                .Where(action => action.Key != default)
                .GroupBy(action => action.Key)
                .Where(group => group.Count() > 1)
                .Select(group => $"'{group.Key}': {string.Join(" / ", group.Select(action => action.Label))}")
                .ToList();

            Assert.True(
                clashes.Count == 0,
                $"{shell.Panes[pane].GetType().Name} offers two actions on one key: {string.Join("; ", clashes)}");
        }
    }
}
