using System.Globalization;

namespace Parley.Terminal;

/// <summary>
/// The menu bar's contents, derived from the focused region's actions.
/// </summary>
/// <remarks>
/// <para>
/// <b>A reading of the action list and never a second list.</b> The owner's D1: an action carries
/// metadata that guides the UI and does not dictate it. So this takes what the actions say about
/// themselves — their category, their group, their order — and decides what a bar looks like, while the
/// status line and the <c>?</c> dialog make their own decisions from the same facts. Two hand-written
/// lists agree until one is edited, which is this repository's most-repeated defect.
/// </para>
/// <para>
/// <b>Scoped to the focused region</b> (D2). What is on the bar is the focused region's, because an
/// action belongs to the region that shows the thing it acts on — 005's User Story 2 — and because a
/// menu offering <i>Withdraw…</i> while the projects pane has focus would read one region's selection
/// from another view, a defect CLAUDE.md records appearing five times.
/// </para>
/// <para>
/// <b>Separated from the view that draws it so it can be tested without a screen.</b> The terminal's
/// recorded trap is the opposite arrangement: a property the suite holds to and the screen never shows,
/// which this file's neighbours have paid for four times. What is here is what the bar *says*; whether
/// the row is drawn is a layout question, and `MenuBarTests` reads the laid-out screen for it.
/// </para>
/// </remarks>
internal static class MenuRow
{
    /// <summary>What a category is called on the bar.</summary>
    /// <param name="category">The category.</param>
    /// <returns>Its title.</returns>
    /// <remarks>
    /// Spelled here rather than taken from the enum's name so the words on screen are chosen rather
    /// than inherited from an identifier, which is the same reason <c>Roster.State</c> exists.
    /// </remarks>
    private static string Titled(ActionCategory category) => category switch
    {
        ActionCategory.Deployment => "Deployment",
        ActionCategory.Project => "Project",
        ActionCategory.Agent => "Agent",
        ActionCategory.Conversation => "Conversation",
        ActionCategory.View => "View",
        ActionCategory.Help => "Help",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>The menu an action falls under when it names no category.</summary>
    /// <remarks>
    /// <b>It exists so the bar cannot quietly lose an action, and it should never be seen.</b> An
    /// unclassified action has to reach the screen — no action on this surface exists only as a
    /// keystroke — but filing it under a category somebody did not choose would be the bar inventing a
    /// fact. So it goes here, visibly, and <c>EveryActionIsOnTheBarTests</c> fails the build when
    /// anything lands in it.
    /// </remarks>
    internal const string Unclassified = "Other";

    /// <summary>The menus the bar shows, in order, for one region's actions.</summary>
    /// <param name="actions">What the focused region can do.</param>
    /// <returns>Each menu's title and what is under it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="actions"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Menus appear in the categories' declared order rather than in the order actions happen to be
    /// built, so the bar does not rearrange itself as a region gains or loses one. A category with
    /// nothing under it is absent rather than empty: an empty menu is a door to nowhere.
    /// </para>
    /// <para>
    /// Within a menu, sorted by <c>(Group.Order, Order)</c> with a separator wherever the group
    /// changes — D1's rendering rule, stated there and applied only here.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(string Title, IReadOnlyList<MenuItem> Items)> Menus(
        IReadOnlyList<RegionAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var menus = new List<(string, IReadOnlyList<MenuItem>)>();

        foreach (var category in Enum.GetValues<ActionCategory>())
        {
            var mine = actions.Where(action => action.Category == category).ToList();

            if (mine.Count > 0)
            {
                menus.Add((Titled(category), Items(mine)));
            }
        }

        var loose = actions.Where(action => action.Category is null).ToList();

        if (loose.Count > 0)
        {
            menus.Add((Unclassified, Items(loose)));
        }

        return menus;
    }

    /// <summary>One menu's items, ordered, with separators where the group changes.</summary>
    /// <param name="actions">The actions under one category.</param>
    /// <returns>The items.</returns>
    private static IReadOnlyList<MenuItem> Items(IReadOnlyList<RegionAction> actions)
    {
        var ordered = actions
            .OrderBy(action => action.Grouping.Order)
            .ThenBy(action => action.Order)
            .ThenBy(action => action.Label, StringComparer.Ordinal)
            .ToList();

        var items = new List<MenuItem>();
        ActionGroup? last = null;

        foreach (var action in ordered)
        {
            if (last is not null && action.Grouping != last)
            {
                items.Add(MenuItem.Separator);
            }

            items.Add(new MenuItem(Written(action), action.Stroke, action));
            last = action.Grouping;
        }

        return items;
    }

    /// <summary>How an action is written on a menu.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Its label, and what it costs whoever it reaches.</returns>
    /// <remarks>
    /// <b>The cost belongs here and not on the status line</b>, which 0.25.4 settled: two clauses on
    /// the status line is fifty columns that push every action behind <c>?</c> at any ordinary width. A
    /// menu is a list somebody reads; a status line is a reminder of what they already know.
    /// </remarks>
    private static string Written(RegionAction action) =>
        action.Costs is { Length: > 0 } costs
            ? string.Create(CultureInfo.InvariantCulture, $"{action.Label} — {costs}")
            : action.Label;
}

/// <summary>One line on a menu: an action, or the rule between two groups.</summary>
/// <param name="Label">What it is called, with its cost where it has one.</param>
/// <param name="Stroke">The key that takes it, written as a person would press it.</param>
/// <param name="Action">What it does, or <see langword="null"/> for a separator.</param>
/// <remarks>
/// <b>The key travels with the label</b>, because a menu that hid it would teach somebody to reach for
/// the mouse for something one keystroke does — and because the keys are unambiguous here: D2 scopes the
/// bar to the focused region, so the <c>r</c> that removes a project and the <c>r</c> that withdraws an
/// agent are never on screen together.
/// </remarks>
internal sealed record MenuItem(string Label, string Stroke, RegionAction? Action)
{
    /// <summary>The rule drawn between two groups.</summary>
    internal static MenuItem Separator { get; } = new(string.Empty, string.Empty, null);

    /// <summary>Whether this line is the rule rather than something to choose.</summary>
    internal bool Divides => Action is null;
}
