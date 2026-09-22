using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// Who is in a conversation, what this person must address, and how the agents stand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The narrow column of the room.</b> The owner asked for it after 0.29.2 put a count of waiting mail on the
/// status line: knowing that something is waiting is not knowing what, and the answer to "what" was a key pressed
/// inside a transcript they had no reason to open.
/// </para>
/// <para>
/// <b>Every row is readable with colour stripped.</b> An agent's colour is <see cref="Palette"/>'s, so it is the
/// same hue here, in every other view and in the web console — but it repeats a name that is already written, and
/// never carries a fact of its own. `ColourTests` reads this screen with every attribute removed and asserts
/// nothing is lost, which is a rule this repository has already broken once by dimming an archived row that said
/// nothing else about being archived.
/// </para>
/// <para>
/// <b>What is outstanding comes from the same pair state the input line derives its acts from</b>
/// (<see cref="Pairing"/>), so the list and the line cannot disagree about what is owed. Two sources for one fact
/// is how a surface comes to promise something it will not do.
/// </para>
/// </remarks>
internal sealed class RoomRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Room";

    /// <summary>The heading the participants are listed under.</summary>
    internal const string Participants = "in this conversation";

    /// <summary>The heading what this person owes is listed under.</summary>
    internal const string ToAddress = "waiting on you";

    /// <summary>What is said where nothing is owed, so an empty list is never mistaken for a list that failed.</summary>
    internal const string NothingOwed = "nothing owed by you";

    /// <summary>What is said before a seat has been taken.</summary>
    internal const string NoSeat = "take a seat to take part";

    /// <summary>What is said where a row cannot be shown in the transcript beside it.</summary>
    /// <remarks>
    /// Said rather than swallowed. A transcript refilled since this column was drawn is an ordinary race, and
    /// a row that silently does nothing reads as the key being broken.
    /// </remarks>
    internal const string Unreachable = "That message is not in the transcript as it stands. Read it again.";

    private readonly GuardedList _list = Lists.Filling(Colours.Agents);

    private List<Row> _rows = [];

    internal RoomRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Agents));
        Add(_list);
        Lists.Tint(_list, Tint);

        // The toolkit's own accept, so the key route and the pointer route are one code path -- which is what
        // `contracts/views.md` means by nothing existing only as a key.
        _list.Accepting += (_, args) =>
        {
            if (Selected is not { } item)
            {
                return;
            }

            args.Handled = true;
            Reading?.Invoke(this, item);
        };
    }

    /// <summary>Raised when somebody activated something they owe, to be read where it was said.</summary>
    internal event EventHandler<OutstandingItem>? Reading;

    /// <summary>What the list is showing, for a test to read without the screen.</summary>
    internal IReadOnlyList<string> Written => [.. _rows.Select(r => r.Text)];

    /// <summary>The list itself, so a test drives this column the way it drives every other one.</summary>
    internal GuardedList List => _list;

    /// <summary>The outstanding item under the cursor, or null when the cursor is not on one.</summary>
    internal OutstandingItem? Selected =>
        _list.SelectedItem is int at && at >= 0 && at < _rows.Count ? _rows[at].Item : null;

    /// <summary>Shows a conversation's participants and what this person owes in it.</summary>
    /// <param name="pairs">The pair state for every participant, or empty with no seat.</param>
    /// <param name="outstanding">What this person must address.</param>
    /// <param name="seated">Whether a seat is held.</param>
    /// <remarks>
    /// Headings are rows rather than decoration, for the reason the agents list groups by state: a row does not
    /// wrap, and a heading said once is shorter than a qualifier repeated on every row.
    /// </remarks>
    internal void Show(IReadOnlyList<PairState> pairs, IReadOnlyList<OutstandingItem> outstanding, bool seated)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(outstanding);

        var rows = new List<Row>
        {
            new(Participants, null, null),
        };

        foreach (var pair in pairs)
        {
            rows.Add(new Row("  " + Safe.Text(pair.Peer.Value) + Standing(pair), null, Palette.SlotFor(pair.Peer)));
        }

        rows.Add(new Row(string.Empty, null, null));
        rows.Add(new Row(ToAddress, null, null));

        if (!seated)
        {
            rows.Add(new Row("  " + NoSeat, null, null));
        }
        else if (outstanding.Count == 0)
        {
            rows.Add(new Row("  " + NothingOwed, null, null));
        }
        else
        {
            foreach (var item in outstanding)
            {
                rows.Add(new Row("  " + Safe.Text(item.Says), item, Palette.SlotFor(item.Peer)));
            }
        }

        _rows = rows;
        _list.Replace([.. rows.Select(r => r.Text)], _list.SelectedItem);
    }

    /// <summary>What is said beside a participant about what stands between them and this person.</summary>
    /// <param name="pair">The pair state.</param>
    /// <returns>The clause, or nothing where nothing is open.</returns>
    /// <remarks>
    /// Written out rather than left to the colour, and phrased from this person's side: "owes you" and "waiting on
    /// you" are the two facts somebody reads this column to learn.
    /// </remarks>
    private static string Standing(PairState pair) => pair.Turn switch
    {
        Turn.Mine => " — waiting on you",
        Turn.Theirs => " — owes you",
        _ => string.Empty,
    };

    /// <summary>The colour of one row, derived from a name already written on it.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null for a heading.</returns>
    private Color? Tint(int row) => row >= 0 && row < _rows.Count && _rows[row].Slot is { } slot
        ? Colours.Slot(slot)
        : null;

    /// <summary>One row: what it says, what it refers to, and which colour it takes.</summary>
    private sealed record Row(string Text, OutstandingItem? Item, int? Slot);
}

/// <summary>
/// Something this person must address, and the message it refers to.
/// </summary>
/// <param name="Peer">Who is waiting, or who asked.</param>
/// <param name="Says">What is owed, in words a person reads.</param>
/// <param name="Exchange">The exchange it belongs to.</param>
/// <param name="At">The message to put on screen when this is activated, or null when there is none to show.</param>
/// <remarks>
/// Built from the same pair state the input line derives its acts from, so the column and the line cannot
/// disagree about what is outstanding.
/// </remarks>
internal sealed record OutstandingItem(AgentName Peer, string Says, long Exchange, long? At);
