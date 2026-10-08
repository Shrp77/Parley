using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>What a person chose on a proposed roadmap.</summary>
internal enum ProposalChoice
{
    /// <summary>Nothing was decided; it is still awaiting them.</summary>
    Leave = 0,

    /// <summary>Confirm it as the agent proposed it.</summary>
    Confirm = 1,

    /// <summary>Change it, then confirm it.</summary>
    Edit = 2,

    /// <summary>Decline it.</summary>
    Decline = 3,
}

/// <summary>
/// A proposed roadmap, read in one place: the roadmap, who it names, and what can be done about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A view of its own rather than a menu with the roadmap in its description</b> (the owner's report of 2026-10-08).
/// The roadmap was the description of a list of choices: it did not scroll, the choices were squeezed under it, and
/// nothing said the arrow keys moved between them. The roadmap is the thing being decided, so it has the room, and it
/// scrolls.
/// </para>
/// <para>
/// <b>Three places, each doing one job.</b> The roadmap is on the left, an item at a time with the agent it is meant for
/// beneath it. Who the proposal names is on the right, grouped by agent, so which agent is meant for what can be read
/// from either side. What can be done is a row of buttons along the bottom with the key for each written on it, and a
/// line above them that says how to scroll. Nothing here is a roadmap until the person confirms it, and the view says so.
/// </para>
/// <para>
/// <b>Read-only.</b> Editing is a form of its own (<see cref="Proposing"/>), because rewording a roadmap is a different
/// act from reading one and the keys for it would be the keys that scroll.
/// </para>
/// </remarks>
internal sealed class ProposalView : Dialog
{
    /// <summary>The label of the button that confirms as proposed.</summary>
    internal const string ConfirmLabel = "Confirm (c)";

    /// <summary>The label of the button that edits first.</summary>
    internal const string EditLabel = "Edit (e)";

    /// <summary>The label of the button that declines.</summary>
    internal const string DeclineLabel = "Decline (d)";

    /// <summary>The label of the button that leaves it.</summary>
    internal const string LeaveLabel = "Leave (Esc)";

    /// <summary>What is said above the buttons.</summary>
    internal const string Hint =
        "Up and Down scroll the roadmap. Tab moves between the roadmap, who it names and the buttons; the key on a button "
        + "chooses it. Edit lets you change it before you confirm. Nothing is a roadmap until you confirm it, and it "
        + "assigns nobody: work still goes to an agent as a request from you.";

    /// <summary>How wide the column of agents is.</summary>
    private const int AgentsWidth = 30;

    private readonly string _header;
    private readonly View _items;
    private readonly View _agents;

    /// <summary>Creates the view for one proposal.</summary>
    /// <param name="proposal">What was proposed.</param>
    /// <param name="sameTitle">The title of the live roadmap it says it would change, when there is one.</param>
    internal ProposalView(RoadmapProposal proposal, string? sameTitle = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        Title = Proposing.Title;
        Width = Dim.Percent(92);
        Height = Dim.Percent(92);

        _header = Safe.Lines(ProposalText.Header(proposal, sameTitle));

        var heading = new Plain
        {
            Text = _header,
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = Prompting.Rows(_header) + 1,
        };

        heading.TextFormatter.WordWrap = true;

        // **Headings above the paragraphs, and no borders on them.** The paragraphs had bordered frames with titles, which
        // draw through the toolkit's line canvas; the owner's terminal threw a NullReferenceException out of it on the first
        // real draw of the roadmaps view, and the driven suite failed intermittently inside the same machinery. A dialog
        // is opened from the queue's thread while the loop draws, and a canvas of merged border lines is what that race
        // corrupts. The dialogs that have always worked here have one border, their own.
        var itemsHeading = new Plain { Text = "The roadmap", X = 1, Y = Pos.Bottom(heading), Width = Dim.Fill(AgentsWidth + 3), Height = 1 };

        // The roadmap takes everything the heading, the hint and the buttons leave.
        _items = Entry.Reader(Safe.Lines(ProposalText.Items(proposal)), Choose);
        _items.X = 1;
        _items.Y = Pos.Bottom(itemsHeading);
        _items.Width = Dim.Fill(AgentsWidth + 3);
        _items.Height = Dim.Fill(Reserved);

        // Who it names, on the side, for the same rows.
        var agentsHeading = new Plain { Text = "Who it names", X = Pos.Right(_items) + 1, Y = Pos.Bottom(heading), Width = Dim.Fill(1), Height = 1 };

        _agents = Entry.Reader(Safe.Lines(ProposalText.Agents(proposal)), Choose);
        _agents.X = Pos.Right(_items) + 1;
        _agents.Y = Pos.Bottom(agentsHeading);
        _agents.Width = Dim.Fill(1);
        _agents.Height = Dim.Fill(Reserved);

        var hint = new Plain
        {
            Text = Hint,
            X = 1,
            Y = Pos.AnchorEnd(Reserved - 1),
            Width = Dim.Fill(2),
            Height = 3,
        };

        hint.TextFormatter.WordWrap = true;

        var confirm = Choice(ConfirmLabel, ProposalChoice.Confirm);
        var edit = Choice(EditLabel, ProposalChoice.Edit);
        var decline = Choice(DeclineLabel, ProposalChoice.Decline);
        var leave = Choice(LeaveLabel, ProposalChoice.Leave);

        confirm.X = 1;
        edit.X = Pos.Right(confirm) + 1;
        decline.X = Pos.Right(edit) + 1;
        leave.X = Pos.Right(decline) + 1;

        Add(heading, itemsHeading, _items, agentsHeading, _agents, hint, confirm, edit, decline, leave);
    }

    /// <summary>Rows kept for the hint and the buttons below the roadmap.</summary>
    private const int Reserved = 6;

    /// <summary>Raised once, with what the person chose.</summary>
    internal event EventHandler<ProposalChoice>? Done;

    /// <summary>What heads the view.</summary>
    internal string Header => _header;

    /// <summary>The roadmap's text as the view holds it.</summary>
    internal string ItemsText => Entry.ReaderText(_items);

    /// <summary>Who the proposal names, as the view holds it.</summary>
    internal string AgentsText => Entry.ReaderText(_agents);

    /// <summary>Ends the view with a choice.</summary>
    /// <param name="choice">What was chosen.</param>
    internal void Finish(ProposalChoice choice) => Done?.Invoke(this, choice);

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Choose(key) || base.OnKeyDown(key);
    }

    /// <summary>Takes the key a button is labelled with, or Escape.</summary>
    /// <param name="key">What arrived.</param>
    /// <returns>Whether it was a choice.</returns>
    /// <remarks>
    /// Plain letters, because there is nothing here to type into: the roadmap is read-only, so a letter is never text.
    /// Esc leaves, as it does everywhere on this surface. The paragraphs offer their keys here first, since a focused view
    /// is offered a key before the dialog around it.
    /// </remarks>
    private bool Choose(Key key)
    {
        var chosen = key == Key.Esc ? ProposalChoice.Leave
            : key == Key.C ? ProposalChoice.Confirm
            : key == Key.E ? ProposalChoice.Edit
            : key == Key.D ? ProposalChoice.Decline
            : (ProposalChoice?)null;

        if (chosen is not { } choice)
        {
            return false;
        }

        Finish(choice);

        return true;
    }

    private Button Choice(string label, ProposalChoice choice)
    {
        var button = new Button
        {
            Title = label,
            Y = Pos.AnchorEnd(2),

            // **No default button.** Enter with the roadmap in front of you used to confirm it: the first button was the default
            // and the roadmap does not take Enter. A decision that is hard to take back is chosen by its key or by Enter on
            // its own button, never by a key that was meant for something else.
            IsDefault = false,
        };

        button.Accepting += (_, args) =>
        {
            args.Handled = true;
            Finish(choice);
        };

        return button;
    }
}
