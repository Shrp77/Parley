using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
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

    /// <summary>The heading the person's own seat is listed under.</summary>
    /// <remarks>
    /// <b>On its own line, above the agents, because a person is not one of them.</b> The owner asked for it
    /// after taking a seat and then hunting for where it said so: every other row here is somebody they might
    /// send to, and the one row that is them was indistinguishable in the middle of that list.
    /// </remarks>
    internal const string You = "you";

    /// <summary>What marks a participant this message is addressed to.</summary>
    /// <remarks>
    /// <para>
    /// <b>Written, never a colour or a highlight.</b> `ColourTests` strips every attribute and asserts nothing
    /// is lost, and who a message is about to go to is exactly the fact that must survive that.
    /// </para>
    /// <para>
    /// Square brackets rather than a tick glyph: this column is drawn in whatever terminal somebody has, and a
    /// box-drawing character that renders as a question mark would make the check unreadable on the machine
    /// least able to fix it.
    /// </para>
    /// </remarks>
    internal const string Addressed = "[x] ";

    /// <summary>What marks a participant this message is not addressed to.</summary>
    internal const string NotAddressed = "[ ] ";

    /// <summary>The heading what this person owes is listed under.</summary>
    internal const string ToAddress = "waiting on you";

    /// <summary>What is said where nothing is owed, so an empty list is never mistaken for a list that failed.</summary>
    internal const string NothingOwed = "nothing owed by you";

    /// <summary>What is said while this terminal is not connected to the project as the person.</summary>
    /// <remarks>Not "take a seat" (specs/023): there is no seat to take, only a connection coming back.</remarks>
    internal const string NoSeat = "not connected as you just now";

    /// <summary>What is said when somebody tries to exclude the last recipient left.</summary>
    internal const string LastOne =
        "That is the only agent this is addressed to. Include another before excluding this one.";

    /// <summary>What is said where a row cannot be shown in the transcript beside it.</summary>
    /// <remarks>
    /// Said rather than swallowed. A transcript refilled since this column was drawn is an ordinary race, and
    /// a row that silently does nothing reads as the key being broken.
    /// </remarks>
    internal const string Unreachable = "That message is not in the transcript as it stands. Read it again.";

    private readonly GuardedList _list = Lists.Filling(Colours.Agents);

    private List<Row> _rows = [];

    // What the last read said, so the ticks can be redrawn when somebody changes who they are addressing
    // without asking a broker anything. A column that had to be re-read to change a checkbox would put a
    // network round trip between a keystroke and the mark it makes.
    private IReadOnlyList<PairState> _pairs = [];
    private IReadOnlyList<OutstandingItem> _outstanding = [];
    private IReadOnlyList<AgentName> _addressed = [];
    private AgentName? _me;
    private bool _seated;

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
            // **Enter does what the row under it is for**, which is FR-031 read at the level of a list: on a
            // participant it is going to say something, and on a thing owed it is going to read it.
            //
            // **Enter on a participant goes to the box and leaves the ticks alone** (the owner's call of 2026-10-02).
            // It ticked and unticked until then, the same as Space; the owner's report was that pressing Enter on an
            // agent is somebody about to type, and they want to start typing. Space is still the tick.
            if (SelectedPeer is { } peer)
            {
                args.Handled = true;
                Choosing?.Invoke(this, peer);

                return;
            }

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

    /// <summary>Raised when somebody included or excluded a participant from what they are writing.</summary>
    internal event EventHandler<AgentName>? Toggling;

    /// <summary>Raised when somebody pressed Enter on a participant, which means they are about to write.</summary>
    internal event EventHandler<AgentName>? Choosing;

    /// <summary>What the list is showing, for a test to read without the screen.</summary>
    internal IReadOnlyList<string> Written => [.. _rows.Select(r => r.Text)];

    /// <summary>The list itself, so a test drives this column the way it drives every other one.</summary>
    internal GuardedList List => _list;

    /// <summary>The outstanding item under the cursor, or null when the cursor is not on one.</summary>
    internal OutstandingItem? Selected => Under?.Item;

    /// <summary>The participant under the cursor, or null when the cursor is not on one.</summary>
    internal AgentName? SelectedPeer => Under?.Peer;

    /// <summary>The row under the cursor, or null when there is none.</summary>
    private Row? Under =>
        _list.SelectedItem is int at && at >= 0 && at < _rows.Count ? _rows[at] : null;

    /// <summary>Includes or excludes the participant under the cursor.</summary>
    /// <remarks>
    /// Raises an event rather than changing anything itself. This column draws who a message is going to; it does
    /// not decide it. Space, since 2026-10-02: Enter on a participant goes to the box instead.
    /// </remarks>
    internal void Toggle()
    {
        if (SelectedPeer is { } peer)
        {
            Toggling?.Invoke(this, peer);
        }
    }

    /// <summary>Redraws the ticks for a different set of recipients, without re-reading anything.</summary>
    /// <param name="addressed">Who the message being written is going to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="addressed"/> is null.</exception>
    internal void Address(IReadOnlyList<AgentName> addressed)
    {
        ArgumentNullException.ThrowIfNull(addressed);

        // With the activity it already has: a tick changes who a line goes to, not what anybody is doing, and drawing
        // without it blanked every mark until the next read.
        Show(_pairs, _outstanding, _seated, _me, addressed, _activity, _now);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Drawn again at a new width, because where a row splits depends on it. The rows are what the column was last
    /// given, so nothing is read again.
    /// </remarks>
    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);

        if (Viewport.Width != _width && _width >= 0)
        {
            Show(_pairs, _outstanding, _seated, _me, _addressed, _activity, _now);
        }
    }

    /// <summary>Shows a conversation's participants and what this person owes in it.</summary>
    /// <param name="pairs">The pair state for every participant, or empty with no seat.</param>
    /// <param name="outstanding">What this person must address.</param>
    /// <param name="seated">Whether a seat is held.</param>
    /// <param name="me">The name the person holds a seat under, or null when they hold none.</param>
    /// <param name="addressed">Who what is being written goes to, or null for every participant.</param>
    /// <param name="activity">What each participant is doing, or null when it could not be read.</param>
    /// <param name="now">The time to measure how long somebody has been working against.</param>
    /// <remarks>
    /// Headings are rows rather than decoration, for the reason the agents list groups by state: a row does not
    /// wrap, and a heading said once is shorter than a qualifier repeated on every row.
    /// </remarks>
    internal void Show(
        IReadOnlyList<PairState> pairs,
        IReadOnlyList<OutstandingItem> outstanding,
        bool seated,
        AgentName? me = null,
        IReadOnlyList<AgentName>? addressed = null,
        IReadOnlyDictionary<AgentName, Activity>? activity = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(outstanding);

        _activity = activity ?? new Dictionary<AgentName, Activity>();
        _now = now ?? DateTimeOffset.UtcNow;

        _pairs = pairs;
        _outstanding = outstanding;
        _seated = seated;
        _me = me;
        _addressed = addressed ?? [.. pairs.Select(pair => pair.Peer)];

        var rows = new List<Row>
        {
            new(You, null, null, null),
            new(
                "  " + (me is { } held ? Safe.Text(held.Value) : NoSeat),
                null,
                me is { } mine ? Palette.SlotFor(mine) : null,
                null),
            new(string.Empty, null, null, null),
            new(Participants, null, null, null),
        };

        foreach (var pair in pairs)
        {
            // Parley is named as Parley rather than by its registry name, and is not a row a person can address: it
            // receives nobody's work, and ticking it would put a recipient on a message the broker refuses.
            if (Facilitation.Is(pair.Peer))
            {
                rows.Add(new Row("  " + Facilitation.DisplayName + " -- this project's facilitator", null, null, null));

                continue;
            }

            rows.Add(new Row(
                Mark(pair.Peer) + Glyph(pair.Peer) + " " + Safe.Text(Shown(pair.Peer, pairs)) + Standing(pair),
                null,
                Palette.SlotFor(pair.Peer),
                pair.Peer));
        }

        rows.Add(new Row(string.Empty, null, null, null));
        rows.Add(new Row(ToAddress, null, null, null));

        if (!seated)
        {
            rows.Add(new Row("  " + NoSeat, null, null, null));
        }
        else if (outstanding.Count == 0)
        {
            rows.Add(new Row("  " + NothingOwed, null, null, null));
        }
        else
        {
            foreach (var item in outstanding)
            {
                rows.Add(new Row("  " + Safe.Text(item.Says), item, Palette.SlotFor(item.Peer), null));
            }
        }

        // **What the marks mean, said on screen**, because a mark nothing explains is a fact that exists only for the
        // person who wrote it — the same rule that keeps colour from carrying anything alone.
        if (pairs.Any(pair => !Facilitation.Is(pair.Peer)))
        {
            rows.Add(new Row(string.Empty, null, null, null));
            rows.AddRange(Legend.Select(line => new Row(line, null, null, null)));
        }

        _width = Viewport.Width;
        rows = Fit(rows, _width);

        // **The cursor stays on what it was on, not on the row number it was at.** A row that splits or joins above it
        // moves everything below by one, and every refresh can do that now that a status can grow past the edge — so a
        // cursor kept by position would land on a different agent, and Space would tick somebody else.
        var was = Under;
        var at = was is null ? _list.SelectedItem : rows.FindIndex(row => Same(row, was));

        _rows = rows;
        _list.Replace([.. rows.Select(r => r.Text)], at is >= 0 ? at : _list.SelectedItem);
    }

    /// <summary>Whether two rows are about the same agent or the same thing owed.</summary>
    /// <param name="row">A row.</param>
    /// <param name="other">Another.</param>
    /// <returns><see langword="true"/> when they name one thing.</returns>
    private static bool Same(Row row, Row other) =>
        (other.Peer is { } peer && row.Peer == peer)
        || (other.Item is { } item && row.Item is { } mine && mine.Exchange == item.Exchange)
        || (other.Peer is null && other.Item is null && row.Text == other.Text);

    /// <summary>The width the rows were last fitted to, or -1 before anything was shown.</summary>
    private int _width = -1;

    /// <summary>Splits a row too wide for the column at its dash, with what follows on an indented row of its own.</summary>
    /// <param name="rows">The rows as written.</param>
    /// <param name="width">The column's width, or nought before it has one.</param>
    /// <returns>The rows as they fit.</returns>
    /// <remarks>
    /// <para>
    /// <b>The owner's report of 2026-10-03</b>, with the marks already in: "inventoryledger — worki" and "orders —
    /// waiting on y" at the edge of a column some thirty characters wide. What an agent is doing is the half of the row
    /// worth reading, and it was the half that was cut.
    /// </para>
    /// <para>
    /// <b>Both rows belong to the same thing</b>, so a key on either acts on the agent or the item it names — the second
    /// row is not a separate place for the cursor to mean nothing. A row with no dash, or one that fits, is left alone.
    /// </para>
    /// </remarks>
    private static List<Row> Fit(List<Row> rows, int width)
    {
        if (width <= 0)
        {
            return rows;
        }

        var fitted = new List<Row>(rows.Count);

        foreach (var row in rows)
        {
            var dash = row.Text.IndexOf(" — ", StringComparison.Ordinal);

            if (row.Text.Length <= width || dash < 0)
            {
                fitted.Add(row);

                continue;
            }

            // Under the name, which starts after the tick and the mark; the leading spaces of an owed row otherwise.
            var indent = row.Peer is not null
                ? Addressed.Length + 2
                : row.Text.Length - row.Text.TrimStart().Length;

            fitted.Add(row with { Text = row.Text[..dash] });
            fitted.Add(row with { Text = new string(' ', indent + 2) + row.Text[(dash + 3)..] });
        }

        return fitted;
    }

    /// <summary>The mark for a participant that is working on a request it has picked up.</summary>
    internal const string WorkingMark = "◆";

    /// <summary>The mark for a participant that is running and holds nothing it has picked up.</summary>
    internal const string RunningMark = "●";

    /// <summary>The mark for a participant nothing is running.</summary>
    internal const string NotRunningMark = "○";

    /// <summary>What the marks mean, at the foot of the column.</summary>
    internal static readonly IReadOnlyList<string> Legend =
    [
        $"{RunningMark} running",
        $"{WorkingMark} working on a request",
        $"{NotRunningMark} not running",
    ];

    /// <summary>The word for how long somebody has been working, before the duration.</summary>
    internal const string WorkingFor = " — working ";

    private IReadOnlyDictionary<AgentName, Activity> _activity = new Dictionary<AgentName, Activity>();

    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>The mark in front of a participant, from what they are doing.</summary>
    /// <param name="peer">The participant.</param>
    /// <returns>One character; a space when the registry could not be read, so nothing is claimed.</returns>
    private string Glyph(AgentName peer) => (_activity.TryGetValue(peer, out var doing) ? doing.Doing : Doing.Unknown) switch
    {
        Doing.Working => WorkingMark,
        Doing.Running => RunningMark,
        Doing.NotRunning => NotRunningMark,
        _ => " ",
    };

    /// <summary>A participant's name as the column draws it.</summary>
    /// <param name="peer">The participant.</param>
    /// <param name="pairs">Everybody in the conversation.</param>
    /// <returns>The role alone where no other participant shares it, else the whole name.</returns>
    /// <remarks>
    /// <b>The machine half was most of what the owner could not see.</b> Agents in one deployment are nearly always on one
    /// machine, so <c>@machine</c> on every row is the same word repeated in a column twenty-odd characters wide — and it
    /// pushed what each agent was doing off the edge. Where two participants share a role the machine is what tells them
    /// apart, and the whole name is kept.
    /// </remarks>
    private static string Shown(AgentName peer, IReadOnlyList<PairState> pairs) =>
        pairs.Count(pair => pair.Peer.Role == peer.Role) > 1 ? peer.Value : peer.Role;

    /// <summary>Whether what is being written goes to this participant.</summary>
    /// <param name="peer">The participant.</param>
    /// <returns>The mark that goes in front of the name.</returns>
    /// <remarks>
    /// The indent the unchecked mark occupies is the same width as the checked one, so the names stay in one
    /// column — a list whose rows shift sideways as they are ticked is harder to read than one that does not.
    /// </remarks>
    private string Mark(AgentName peer) =>
        _addressed.Contains(peer) ? Addressed : NotAddressed;

    /// <summary>What is said beside a participant about what stands between them and this person.</summary>
    /// <param name="pair">The pair state.</param>
    /// <returns>The clause, or nothing where nothing is open.</returns>
    /// <remarks>
    /// Written out rather than left to the colour, and phrased from this person's side: "owes you" and "waiting on
    /// you" are the two facts somebody reads this column to learn. Since 2026-10-03 a participant working on a request it
    /// has picked up says so and for how long, whoever asked it -- unless the turn is this person's, which wins.
    /// </remarks>
    private string Standing(PairState pair) =>
        pair.Turn == Turn.Mine
            ? " — waiting on you"
            : _activity.TryGetValue(pair.Peer, out var doing) && doing is { Doing: Doing.Working, Since: { } since }
                ? WorkingFor + Activities.For(since, _now)
                : pair.Turn == Turn.Theirs ? " — owes you" : string.Empty;

    /// <summary>The colour of one row, derived from a name already written on it.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null for a heading.</returns>
    private Color? Tint(int row) => row >= 0 && row < _rows.Count && _rows[row].Slot is { } slot
        ? Colours.Slot(slot)
        : null;

    /// <summary>One row: what it says, what it refers to, which colour it takes, and whose it is.</summary>
    /// <param name="Text">What is drawn.</param>
    /// <param name="Item">The thing owed this row activates, or null.</param>
    /// <param name="Slot">The colour slot, or null for a heading.</param>
    /// <param name="Peer">The participant this row is a checkbox for, or null.</param>
    /// <remarks>
    /// <b>At most one of <paramref name="Item"/> and <paramref name="Peer"/> is ever set</b>, and which one
    /// decides what Enter does. A row that was both would be a keystroke whose meaning depended on the order
    /// two branches were written in.
    /// </remarks>
    private sealed record Row(string Text, OutstandingItem? Item, int? Slot, AgentName? Peer);
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
