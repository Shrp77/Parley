using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The roadmaps agents have proposed, listed in the project they were proposed in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Listed where the project is, not offered from a menu.</b> A proposed roadmap is work waiting on a person,
/// so it is a pane of the project view, beside the agents and the conversations, with Enter to open a proposal.
/// </para>
/// <para>
/// <b>Only there when something is proposed.</b> The pane joins the project view when at least one proposal is waiting and
/// leaves it when none is, so a project with nothing proposed looks exactly as it did. An empty pane would take a third of
/// the screen to say nothing.
/// </para>
/// </remarks>
internal sealed class RoadmapsRegion : FrameView
{
    /// <summary>What the pane is called.</summary>
    internal const string RegionTitle = "Proposed roadmaps";

    /// <summary>What Enter does here, named on the status line and in the menu.</summary>
    internal const string ReviewTitle = "Review this proposed roadmap";

    private readonly GuardedList _list = Lists.Filling(Colours.Conversations);

    private List<RoadmapProposal> _proposals = [];

    /// <summary>Creates the pane.</summary>
    internal RoadmapsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Conversations));
        Add(_list);

        // The list's own accept, for the reason the conversations list uses it: Enter reaches the focused view first and the
        // list takes it, so a handler on this region would never run. A double click raises the same event.
        _list.Accepting += (_, args) =>
        {
            if (OpenSelected())
            {
                args.Handled = true;
            }
        };
    }

    /// <summary>Raised when a proposal is chosen.</summary>
    internal event EventHandler<RoadmapProposal>? Opened;

    /// <summary>The proposals waiting for a decision, in the order they are listed.</summary>
    internal IReadOnlyList<RoadmapProposal> Proposals => _proposals;

    /// <summary>The proposal under the cursor, or null when there is none.</summary>
    internal RoadmapProposal? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _proposals.Count ? _proposals[at] : null;

    /// <summary>The list itself, for tests that drive it.</summary>
    internal ListView List => _list;

    /// <summary>Shows what is waiting, keeping the cursor on the same proposal when it is still there.</summary>
    /// <param name="proposals">The proposals awaiting a decision.</param>
    internal void Show(IReadOnlyList<RoadmapProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);

        var keep = Selected?.Id;
        var shown = proposals.ToList();
        var at = keep is null ? -1 : shown.FindIndex(p => p.Id == keep);

        // Something is always on, so Enter has something to open the moment the pane has focus.
        _list.Replace(
            [.. shown.Select(p => Safe.Text(ProposalText.Row(p)))],
            at >= 0 ? at : shown.Count > 0 ? 0 : null,
            also: () => _proposals = shown);
    }

    /// <summary>Opens the proposal under the cursor.</summary>
    /// <returns>Whether there was one.</returns>
    internal bool OpenSelected()
    {
        if (Selected is not { } proposal)
        {
            return false;
        }

        Opened?.Invoke(this, proposal);

        return true;
    }
}
