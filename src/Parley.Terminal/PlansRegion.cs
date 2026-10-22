using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The roadmaps a person has confirmed, listed in the project above its conversations.
/// </summary>
/// <remarks>
/// <para>
/// <b>A confirmed roadmap stays visible in the project</b>: this is where it is. It sits above the conversations because a
/// roadmap is what the conversations come out of — each item is a conversation of its own — and beside the proposals
/// because the two are the same list at different stages.
/// </para>
/// <para>
/// Like the proposals pane it is there only while there is something to list, so a project with no roadmap looks as it
/// did. It is called <i>plans</i> in code to keep it apart from <see cref="RoadmapsRegion"/>, which holds proposals; on
/// screen both say roadmap, since that is what a person calls them.
/// </para>
/// </remarks>
internal sealed class PlansRegion : FrameView
{
    /// <summary>What the pane is called.</summary>
    internal const string RegionTitle = "Roadmaps";

    /// <summary>What Enter does here, named on the status line and in the menu.</summary>
    internal const string OpenTitle = "Open this roadmap";

    private readonly GuardedList _list = Lists.Filling(Colours.Conversations);

    private List<RoadmapSummary> _roadmaps = [];

    /// <summary>Creates the pane.</summary>
    internal PlansRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));
        Add(_list);

        // The list's own accept, for the reason the conversations list uses it: Enter reaches the focused view first.
        _list.Accepting += (_, args) =>
        {
            if (OpenSelected())
            {
                args.Handled = true;
            }
        };
    }

    /// <summary>Raised when a roadmap is chosen.</summary>
    internal event EventHandler<RoadmapSummary>? Opened;

    /// <summary>The roadmaps listed, in order.</summary>
    internal IReadOnlyList<RoadmapSummary> Roadmaps => _roadmaps;

    /// <summary>The roadmap under the cursor, or null when there is none.</summary>
    internal RoadmapSummary? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _roadmaps.Count ? _roadmaps[at] : null;

    /// <summary>The list itself, for tests that drive it.</summary>
    internal ListView List => _list;

    /// <summary>Shows the roadmaps, keeping the cursor on the same one when it is still there.</summary>
    /// <param name="roadmaps">What the project holds.</param>
    internal void Show(IReadOnlyList<RoadmapSummary> roadmaps)
    {
        ArgumentNullException.ThrowIfNull(roadmaps);

        var keep = Selected?.Id;
        var shown = roadmaps.ToList();
        var at = keep is null ? -1 : shown.FindIndex(r => r.Id == keep);

        _list.Replace(
            [.. shown.Select(r => Safe.Text(ItemWording.Row(r)))],
            at >= 0 ? at : shown.Count > 0 ? 0 : null,
            also: () => _roadmaps = shown);
    }

    /// <summary>Opens the roadmap under the cursor.</summary>
    /// <returns>Whether there was one.</returns>
    internal bool OpenSelected()
    {
        if (Selected is not { } roadmap)
        {
            return false;
        }

        Opened?.Invoke(this, roadmap);

        return true;
    }
}
