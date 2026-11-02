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
/// <b>A view of its own rather than a menu with the roadmap in its description.</b> The roadmap is the thing
/// being decided, so it has the room, and it scrolls.
/// </para>
/// <para>
/// <b>Three places, each doing one job.</b> The roadmap is on the left, an item at a time with the agent it is meant for
/// beneath it. Who the proposal names is on the right, grouped by agent, so which agent is meant for what can be read
/// from either side. What can be done is a row of buttons along the bottom with the key for each written on it, wrapped
/// onto a second row where the dialog is too narrow for one, and a line above them that says how to scroll. Nothing
/// here is a roadmap until the person confirms it, and the view says so.
/// </para>
/// <para>
/// <b>Read-only.</b> Editing is a form of its own (<see cref="Proposing"/>), because rewording a roadmap is a different
/// act from reading one and the keys for it would be the keys that scroll.
/// </para>
/// </remarks>
internal sealed class ProposalView : TableDialog<ProposalView>, IKeyTable<ProposalView>
{
    /// <summary>The label of the button that confirms as proposed, with its key from the table.</summary>
    internal static string ConfirmLabel => Labelled(ConfirmName);

    /// <summary>The label of the button that edits first, with its key from the table.</summary>
    internal static string EditLabel => Labelled(EditName);

    /// <summary>The label of the button that declines, with its key from the table.</summary>
    internal static string DeclineLabel => Labelled(DeclineName);

    /// <summary>The label of the button that leaves it, with its key from the table.</summary>
    internal static string LeaveLabel => Labelled(LeaveName);

    private const string ConfirmName = "Confirm";

    private const string EditName = "Edit";

    private const string DeclineName = "Decline";

    private const string LeaveName = "Leave";

    /// <summary>What is said above the buttons.</summary>
    internal const string Hint =
        "Up and Down scroll the roadmap. Nothing is a roadmap until you confirm it, and it "
        + "assigns nobody: work still goes to an agent as a request from you.";

    /// <summary>How wide the column of agents is.</summary>
    private const int AgentsWidth = 30;

    /// <summary>The most rows the heading takes; the rest of it scrolls.</summary>
    private const int MostHeadingRows = 4;

    private readonly string _header;
    private readonly View _items;
    private readonly View _agents;

    /// <summary>Creates the view for one proposal.</summary>
    /// <param name="proposal">What was proposed.</param>
    /// <param name="sameTitle">The title of the live roadmap it says it would change, when there is one.</param>
    internal ProposalView(RoadmapProposal proposal, string? sameTitle = null)
    {
        HotKeySpecifier = Plain.NoHotKey;
        ArgumentNullException.ThrowIfNull(proposal);

        Title = Proposing.Title;
        Width = Dim.Percent(92);
        Height = Dim.Percent(92);

        var confirm = Choice(ConfirmLabel, ProposalChoice.Confirm);
        var edit = Choice(EditLabel, ProposalChoice.Edit);
        var decline = Choice(DeclineLabel, ProposalChoice.Decline);
        var leave = Choice(LeaveLabel, ProposalChoice.Leave);
        var buttons = new ButtonRows(this, confirm, edit, decline, leave);

        // What is kept below the roadmap: a blank row, the hint, and the buttons in as many rows as the width needs.
        var footer = Dim.Func(_ => 1 + HintRows + buttons.Height);

        _header = Safe.Lines(ProposalText.Header(proposal, sameTitle));

        // **At most a few rows, and it scrolls.** A title has no bound, and a heading as tall as its title pushed who
        // proposed it and every item off the dialog while Confirm stayed on it: the person would confirm what they could
        // not see. Who proposed it is the heading's first line, so a long title cannot push that out either. It never has
        // the focus, so the keys that scroll the roadmap cannot scroll that line away; the mouse scrolls the rest.
        var heading = Entry.Reader(_header);
        heading.CanFocus = false;
        heading.X = 1;
        heading.Y = 0;
        heading.Width = Dim.Fill(2);
        heading.Height = Math.Min(Prompting.Rows(_header) + 1, MostHeadingRows);

        // **Headings above the paragraphs, and no borders on them.** A bordered frame draws through the toolkit's line
        // canvas, which the driven suite never exercises because it never pumps the loop. The dialogs here have one
        // border, their own.
        var itemsHeading = new Plain { Text = "The roadmap", X = 1, Y = Pos.Bottom(heading), Width = Dim.Fill(AgentsWidth + 3), Height = 1 };

        // The roadmap takes everything the heading, the hint and the buttons leave.
        _items = Entry.Reader(Safe.Lines(ProposalText.Items(proposal)), this);
        _items.X = 1;
        _items.Y = Pos.Bottom(itemsHeading);
        _items.Width = Dim.Fill(AgentsWidth + 3);
        _items.Height = Dim.Fill(footer);

        // Who it names, on the side, for the same rows.
        var agentsHeading = new Plain { Text = "Who it names", X = Pos.Right(_items) + 1, Y = Pos.Bottom(heading), Width = Dim.Fill(1), Height = 1 };

        _agents = Entry.Reader(Safe.Lines(ProposalText.Agents(proposal)), this);
        _agents.X = Pos.Right(_items) + 1;
        _agents.Y = Pos.Bottom(agentsHeading);
        _agents.Width = Dim.Fill(1);
        _agents.Height = Dim.Fill(footer);

        var hint = new Plain
        {
            Text = Hint,
            X = 1,
            Y = Pos.Func(_ => Viewport.Height - buttons.Height - HintRows),
            Width = Dim.Fill(2),
            Height = HintRows,
        };

        hint.TextFormatter.WordWrap = true;

        Add(heading, itemsHeading, _items, agentsHeading, _agents, hint, confirm, edit, decline, leave);
    }

    /// <summary>Rows kept for the hint above the buttons.</summary>
    private const int HintRows = 3;

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
    /// <remarks>
    /// <para>
    /// The letter each button is labelled with, and Escape. Alt plus the letter, like every action on this surface, so a
    /// plain character is never an action even though nothing here is typed into. The paragraphs offer their keys to the
    /// table first, since a focused view is offered a key before the dialog around it.
    /// </para>
    /// <para>Enter is only a button's: there is no default, which the buttons below say why.</para>
    /// </remarks>
    static CommandManager<ProposalView> IKeyTable<ProposalView>.BuildTable() => new(
        [new(KeyContext.ProposalDialog, [])],
        [
            new(KeyContext.ProposalDialog, 'c', ConfirmName, KeyCommand.Doing<ProposalView>(view => view.Finish(ProposalChoice.Confirm)), KeyCommand.Always),
            new(KeyContext.ProposalDialog, 'e', EditName, KeyCommand.Doing<ProposalView>(view => view.Finish(ProposalChoice.Edit)), KeyCommand.Always),
            new(KeyContext.ProposalDialog, 'd', DeclineName, KeyCommand.Doing<ProposalView>(view => view.Finish(ProposalChoice.Decline)), KeyCommand.Always),
            new(
                KeyContext.ProposalDialog,
                default,
                LeaveName,
                KeyCommand.Doing<ProposalView>(view => view.Finish(ProposalChoice.Leave)),
                KeyCommand.Always,
                Chord: Key.Esc),
        ],
        [
            .. ReservedKey.Pressing(KeyContext.ProposalDialog),
            .. ReservedKey.Moving(KeyContext.ProposalDialog),
            .. ReservedKey.TakenBy(KeyContext.ProposalDialog, () => Entry.Reader(string.Empty), "the roadmap, or who it names"),
        ]);

    /// <summary>A button's label: the command's name and the key the table gives it.</summary>
    /// <param name="name">The command.</param>
    /// <returns>The label.</returns>
    private static string Labelled(string name) => CommandTables.Of<ProposalView>().Label(name);

    private Button Choice(string label, ProposalChoice choice)
    {
        var button = new Button
        {
            Title = label,

            // **No default button.** The roadmap does not take Enter, so a default would confirm on a key meant for
            // something else. A decision that is hard to take back is chosen by its key or by Enter on its own button.
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
