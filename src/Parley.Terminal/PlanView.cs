using System.Globalization;
using Parley.Client.Roadmaps;
using Parley.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// One confirmed roadmap: its items in order, where each stands, and a way into each one's conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where an item stands is read, never set</b> (SPEC.md §5.7). The words come from <see cref="ItemWording"/> over the
/// derived state, so this view can only say what a conversation supports.
/// </para>
/// <para>
/// <b>Enter opens the item's conversation</b>, which is where the work is. An item not yet started opens a conversation
/// that does not exist yet, with the item's text in the box and its intended agent already in it; sending is the
/// person's act, so starting an item is their request and not an operation of its own.
/// </para>
/// <para>
/// A list of one line per item, with the full text of the one under the cursor below it: an item is one line by the
/// proposal's own rule, but a line can be longer than the room, and a list cuts what does not fit.
/// </para>
/// <para>
/// <b>The cursor starts on the next item</b>, the one a person came to act on, or on the item whose conversation they came
/// from. On a long roadmap the next item can be far down, so a key goes back to it from anywhere.
/// </para>
/// </remarks>
internal sealed class PlanView : TableDialog<PlanView>, IKeyTable<PlanView>
{
    /// <summary>Said on the button that opens an item: the list's own Enter, which is no command of this table's.</summary>
    private static readonly string OpenLabel = KeyCommand.Label("Open item", Key.Enter.ToString());

    /// <summary>The letter, with Alt, that edits the roadmap.</summary>
    internal const char EditKey = 'e';

    /// <summary>The letter, with Alt, that puts the cursor on the next item.</summary>
    internal const char NextKey = 'n';

    /// <summary>What the command and the button that edit the roadmap are called.</summary>
    private const string EditName = "Edit roadmap";

    /// <summary>What the command and the button that go to the next item are called.</summary>
    private const string NextName = "Next item";

    /// <summary>What the command and the button that leave the view are called.</summary>
    private const string CloseName = "Close";

    /// <summary>Said on the button that edits the roadmap, with its key from the table.</summary>
    internal static string EditLabel => Labelled(EditName);

    /// <summary>Said on the button that goes to the next item, with its key from the table.</summary>
    private static string NextLabel => Labelled(NextName);

    /// <summary>Said on the button that leaves the view, with its key from the table.</summary>
    internal static string CloseLabel => Labelled(CloseName);

    /// <summary>What is said above the buttons.</summary>
    internal const string Hint =
        "An item that has not started opens an empty conversation with its text in the box, "
        + "and nothing is sent until you send it.";

    private readonly ListView _items;
    private readonly View _detail;
    private readonly RoadmapStanding _standing;
    private readonly Roadmap _roadmap;

    /// <summary>Creates the view.</summary>
    /// <param name="roadmap">The roadmap.</param>
    /// <param name="standing">Where each of its items stands.</param>
    /// <param name="itemNumber">The item to start on, when reached from its conversation; otherwise the next item.</param>
    internal PlanView(Roadmap roadmap, RoadmapStanding standing, int? itemNumber = null)
    {
        HotKeySpecifier = Plain.NoHotKey;
        ArgumentNullException.ThrowIfNull(roadmap);
        ArgumentNullException.ThrowIfNull(standing);

        _roadmap = roadmap;
        _standing = standing;

        Title = PlansRegion.RegionTitle;
        Width = Dim.Percent(92);
        Height = Dim.Percent(92);

        var open = new PlainButton { Title = OpenLabel };
        var next = new PlainButton { Title = NextLabel };
        var edit = new PlainButton { Title = EditLabel };
        var close = new PlainButton { Title = CloseLabel };
        var buttons = new ButtonRows(this, open, next, edit, close);

        // What is kept below the item's text: a blank row, the hint, and the buttons in as many rows as the width needs.
        int Footer() => 1 + HintRows + buttons.Height;

        var heading = new Plain
        {
            Text = Safe.Text(string.Create(CultureInfo.InvariantCulture, $"#{roadmap.Number} {roadmap.Title}")) + "\n" + ItemWording.Next(standing),
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 2,
        };

        // **A label above the list, and no border on it.** Every list here is a plain ListView under a label: a
        // bordered frame draws through the toolkit's line canvas, which the driven suite never exercises because it
        // never pumps the loop.
        var itemsLabel = new Plain { Text = "Items", X = 1, Y = Pos.Bottom(heading), Width = Dim.Fill(2), Height = 1 };

        _items = new ListView
        {
            HotKeySpecifier = Plain.NoHotKey,
            X = 1,
            Y = Pos.Bottom(itemsLabel),
            Width = Dim.Fill(2),
            Height = Dim.Fill(Dim.Func(_ => Footer() + DetailRows + 1)),
            KeystrokeNavigator = null,
        };

        _items.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>([.. Rows(standing)]));
        _items.SelectedItem = Starting(standing, itemNumber);
        _items.Accepting += (_, args) =>
        {
            args.Handled = true;
            OpenSelected();
        };
        _items.ValueChanged += (_, _) => ShowDetail();

        var detailHeading = new Plain { Text = "The item", X = 1, Y = Pos.Func(_ => Viewport.Height - Footer() - DetailRows - 1), Width = Dim.Fill(2), Height = 1 };

        _detail = Entry.Reader(string.Empty, this);
        _detail.X = 1;
        _detail.Y = Pos.Func(_ => Viewport.Height - Footer() - DetailRows);
        _detail.Width = Dim.Fill(2);
        _detail.Height = DetailRows;

        var hint = new Plain { Text = Hint, X = 1, Y = Pos.Func(_ => Viewport.Height - buttons.Height - HintRows), Width = Dim.Fill(2), Height = HintRows };

        hint.TextFormatter.WordWrap = true;

        open.Accepting += (_, args) => { args.Handled = true; OpenSelected(); };
        next.Accepting += (_, args) => { args.Handled = true; GoToNext(); };
        edit.Accepting += (_, args) => { args.Handled = true; Edit(); };
        close.Accepting += (_, args) => { args.Handled = true; Closed?.Invoke(this, EventArgs.Empty); };

        Add(heading, itemsLabel, _items, detailHeading, _detail, hint, open, next, edit, close);

        ShowDetail();
    }

    /// <summary>Rows kept for the hint above the buttons.</summary>
    private const int HintRows = 2;

    private const int DetailRows = 5;

    /// <summary>Raised with the item chosen.</summary>
    internal event EventHandler<RoadmapItem>? ItemChosen;

    /// <summary>Raised when the person asks to change the roadmap.</summary>
    internal event EventHandler? EditRequested;

    /// <summary>Raised when the view is left without choosing.</summary>
    internal event EventHandler? Closed;

    /// <summary>The rows of the list as drawn.</summary>
    internal IReadOnlyList<string> RowTexts => Rows(_standing);

    /// <summary>The item under the cursor, or null when there are none.</summary>
    internal ItemState? Selected =>
        _items.SelectedItem is { } at && at >= 0 && at < _standing.Items.Count ? _standing.Items[at] : null;

    /// <summary>The list, for tests that drive it.</summary>
    internal ListView Items => _items;

    /// <summary>The full text shown for the item under the cursor.</summary>
    internal string DetailText => Entry.ReaderText(_detail);

    /// <summary>One row per item: its number, where it stands, and its text.</summary>
    /// <param name="standing">The derived standing.</param>
    /// <returns>The rows, made safe to draw.</returns>
    internal static IReadOnlyList<string> Rows(RoadmapStanding standing)
    {
        ArgumentNullException.ThrowIfNull(standing);

        return [.. standing.Items.Select(state => Safe.Text(string.Create(
            CultureInfo.InvariantCulture,
            $"{(state.IsNext ? ">" : " ")} {state.Item.Number}. [{ItemWording.Standing(state)}] {state.Item.Text}")))];
    }

    /// <summary>The full account of one item.</summary>
    /// <param name="state">The item's derived state.</param>
    /// <returns>Its text, who it is for, what it needs, where it stands and its conversation.</returns>
    internal static string Describe(ItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var item = state.Item;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{item.Number}. {item.Text}"),
            "Stands: " + ItemWording.Standing(state) + (state.IsNext ? " (next)" : string.Empty),
        };

        if (item.IntendedAgent is { } agent)
        {
            lines.Add("For: " + agent.Value);
        }

        if (item.Needs.Count > 0)
        {
            lines.Add("Needs: " + string.Join(", ", item.Needs.Order().Select(n => n.ToString(CultureInfo.InvariantCulture))));
        }

        lines.Add("Conversation: " + item.ConversationName);

        return Safe.Lines(string.Join('\n', lines));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// What each button is labelled with, and Escape; and what the items, the item's text and the buttons take before
    /// this view is offered a key.
    /// </remarks>
    static CommandManager<PlanView> IKeyTable<PlanView>.BuildTable() => new(
        [new(KeyContext.RoadmapDialog, [])],
        [
            new(KeyContext.RoadmapDialog, NextKey, NextName, KeyCommand.Doing<PlanView>(view => view.GoToNext()), KeyCommand.Always),
            new(KeyContext.RoadmapDialog, EditKey, EditName, KeyCommand.Doing<PlanView>(view => view.Edit()), KeyCommand.Always),
            new(KeyContext.RoadmapDialog, default, CloseName, KeyCommand.Doing<PlanView>(view => view.Close()), KeyCommand.Always, Chord: Key.Esc),
        ],
        [
            .. ReservedKey.Pressing(KeyContext.RoadmapDialog, "open the item, or the focused button"),
            .. ReservedKey.Moving(KeyContext.RoadmapDialog),
            .. ReservedKey.TakenBy(KeyContext.RoadmapDialog, () => new ListView { HotKeySpecifier = Plain.NoHotKey, KeystrokeNavigator = null }, "the items"),
            .. ReservedKey.TakenBy(KeyContext.RoadmapDialog, () => Entry.Reader(string.Empty), "the item's text"),
        ]);

    /// <summary>A button's label: the command's name and the key the table gives it.</summary>
    /// <param name="name">The command.</param>
    /// <returns>The label.</returns>
    private static string Labelled(string name) => CommandTables.Of<PlanView>().Label(name);

    /// <summary>Leaves the view without choosing.</summary>
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Where the cursor starts: the item asked for, else the next item, else the first.</summary>
    /// <param name="standing">The derived standing.</param>
    /// <param name="itemNumber">The item asked for, or null.</param>
    /// <returns>The row, or null when there are no items.</returns>
    private static int? Starting(RoadmapStanding standing, int? itemNumber)
    {
        if (standing.Items.Count == 0)
        {
            return null;
        }

        var asked = itemNumber is { } number ? Row(standing, state => state.Item.Number == number) : null;

        return asked ?? Row(standing, state => state.IsNext) ?? 0;
    }

    private static int? Row(RoadmapStanding standing, Func<ItemState, bool> which)
    {
        for (var at = 0; at < standing.Items.Count; at++)
        {
            if (which(standing.Items[at]))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>Puts the cursor on the next item, as Alt+n does; nothing moves when the roadmap names none.</summary>
    private void GoToNext()
    {
        if (Row(_standing, state => state.IsNext) is { } at)
        {
            _items.SelectedItem = at;
            ShowDetail();
        }
    }

    /// <summary>Asks to change the roadmap, as Alt+e does.</summary>
    /// <remarks>Internal so a driven test can ask without a key that starts queued work.</remarks>
    internal void Edit() => EditRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Chooses the item under the cursor, as Enter does.</summary>
    /// <remarks>Internal so a driven test can choose without drawing while the queue opens the item, which races the test's own draw.</remarks>
    internal void OpenSelected()
    {
        if (Selected is { } state)
        {
            ItemChosen?.Invoke(this, state.Item);
        }
    }

    private void ShowDetail()
    {
        if (_detail is null)
        {
            return;
        }

        Entry.SetReaderText(_detail, Selected is { } state ? Describe(state) : string.Empty);
    }
}
