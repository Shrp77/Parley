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
/// </remarks>
internal sealed class PlanView : Dialog
{
    /// <summary>Said on the button that opens an item.</summary>
    private const string OpenLabel = "Open item (Enter)";

    /// <summary>Said on the button that edits the roadmap; its key is Alt plus <see cref="EditKey"/>.</summary>
    internal const string EditLabel = "Edit roadmap (Alt+e)";

    /// <summary>The letter, with Alt, that edits the roadmap.</summary>
    internal const char EditKey = 'e';

    /// <summary>Said on the button that leaves the view.</summary>
    internal const string CloseLabel = "Close (Esc)";

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
    internal PlanView(Roadmap roadmap, RoadmapStanding standing)
    {
        ArgumentNullException.ThrowIfNull(roadmap);
        ArgumentNullException.ThrowIfNull(standing);

        _roadmap = roadmap;
        _standing = standing;

        Title = PlansRegion.RegionTitle;
        Width = Dim.Percent(92);
        Height = Dim.Percent(92);

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
            X = 1,
            Y = Pos.Bottom(itemsLabel),
            Width = Dim.Fill(2),
            Height = Dim.Fill(Reserved + DetailRows + 1),
            KeystrokeNavigator = null,
        };

        _items.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>([.. Rows(standing)]));
        _items.SelectedItem = standing.Items.Count > 0 ? 0 : null;
        _items.Accepting += (_, args) =>
        {
            args.Handled = true;
            OpenSelected();
        };
        _items.ValueChanged += (_, _) => ShowDetail();

        var detailHeading = new Plain { Text = "The item", X = 1, Y = Pos.AnchorEnd(Reserved + DetailRows + 1), Width = Dim.Fill(2), Height = 1 };

        _detail = Entry.Reader(string.Empty, Choose);
        _detail.X = 1;
        _detail.Y = Pos.AnchorEnd(Reserved + DetailRows);
        _detail.Width = Dim.Fill(2);
        _detail.Height = DetailRows;

        var hint = new Plain { Text = Hint, X = 1, Y = Pos.AnchorEnd(Reserved - 1), Width = Dim.Fill(2), Height = 2 };

        hint.TextFormatter.WordWrap = true;

        var open = new Button { Title = OpenLabel, X = 1, Y = Pos.AnchorEnd(2) };
        var edit = new Button { Title = EditLabel, X = Pos.Right(open) + 1, Y = Pos.AnchorEnd(2) };
        var close = new Button { Title = CloseLabel, X = Pos.Right(edit) + 1, Y = Pos.AnchorEnd(2) };

        open.Accepting += (_, args) => { args.Handled = true; OpenSelected(); };
        edit.Accepting += (_, args) => { args.Handled = true; Edit(); };
        close.Accepting += (_, args) => { args.Handled = true; Closed?.Invoke(this, EventArgs.Empty); };

        Add(heading, itemsLabel, _items, detailHeading, _detail, hint, open, edit, close);

        ShowDetail();
    }

    private const int Reserved = 5;

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
    protected override bool OnKeyDown(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Choose(key) || base.OnKeyDown(key);
    }

    private bool Choose(Key key)
    {
        if (Shell.Typed(key, EditKey))
        {
            Edit();

            return true;
        }

        if (key != Key.Esc)
        {
            return false;
        }

        Closed?.Invoke(this, EventArgs.Empty);

        return true;
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
