using System.Globalization;
using Parley.Client.Rendering;
using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>Which of the Inbox's three views an exchange is in.</summary>
/// <remarks>Exclusive: an exchange is in one view at a time, decided by the broker's wait-for graph and its state.</remarks>
public enum InboxView
{
    /// <summary>Open, and an agent waits on the person.</summary>
    WaitingOnYou = 0,

    /// <summary>Open, and the person waits on an agent.</summary>
    InFlight = 1,

    /// <summary>Closed within the window, and the person was on it.</summary>
    Closed = 2,
}

/// <summary>Where an agent stands now, as its registry row says.</summary>
public enum AgentState
{
    /// <summary>A turn is running.</summary>
    Working = 0,

    /// <summary>Running, no turn running.</summary>
    Idle = 1,

    /// <summary>Running, and nobody reports its turns.</summary>
    Running = 2,

    /// <summary>Not running, and a node can start it.</summary>
    NotRunning = 3,

    /// <summary>Not running, and nothing can start it now.</summary>
    CannotStart = 4,

    /// <summary>Running only as a process Parley did not start.</summary>
    OpenOutside = 5,

    /// <summary>Nothing the broker reports says.</summary>
    Unknown = 6,

    /// <summary>Running, no turn running, and background work it started still runs: it carries on when that finishes.</summary>
    Background = 7,

    /// <summary>Running as a process a node that is gone started: no node can give it a turn or stop it (SPEC.md §11.1).</summary>
    Left = 8,
}

/// <summary>A flag the broker raised on an exchange (SPEC.md §6).</summary>
public enum ExchangeFlag
{
    /// <summary>It has not moved for longer than its limit.</summary>
    Stalled = 0,

    /// <summary>What was sent has not been collected.</summary>
    NotCollected = 1,

    /// <summary>The owing agent's last turn ended with tool calls refused.</summary>
    PermissionRefused = 2,

    /// <summary>The owing agent was retired.</summary>
    Retired = 3,
}

/// <summary>What a status chip says about the agent an exchange in flight waits on.</summary>
/// <param name="State">Where the agent stands now.</param>
/// <param name="Since">When its running turn started, for <see cref="AgentState.Working"/>.</param>
/// <param name="Flag">A flag on the exchange, if the broker raised one.</param>
/// <param name="BackgroundTasks">How many background tasks the agent has running, for <see cref="AgentState.Background"/>.</param>
/// <remarks>
/// <b>Never more than the broker reported</b> (spec 036, FR-006). "Working" means a turn is running, not that the agent is
/// working on the person's message, and nothing records that a start is under way, so there is no "being started" (research
/// R2). The words are written here once, so the web console and the terminal say the same thing.
/// </remarks>
public sealed record InFlightStanding(AgentState State, DateTimeOffset? Since, ExchangeFlag? Flag, int BackgroundTasks = 0)
{
    /// <summary>The chip's own words.</summary>
    public string Chip => State switch
    {
        AgentState.Working => "Working",
        AgentState.Background => "Background work",
        AgentState.Idle => "Idle",
        AgentState.Running => "Running",
        AgentState.NotRunning => "Not running",
        AgentState.CannotStart => "Cannot be started now",
        AgentState.OpenOutside => "Open outside Parley",
        AgentState.Left => "Left running",
        _ => "Unknown",
    };

    /// <summary>The sentence beside the chip.</summary>
    public string Detail => State switch
    {
        AgentState.Working when Since is { } since =>
            $"A turn has been running since {since.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}.",
        AgentState.Working => "A turn is running.",
        AgentState.Background =>
            $"Its turn ended while background work it started runs on ({(BackgroundTasks == 1 ? "1 task" : $"{BackgroundTasks.ToString(CultureInfo.InvariantCulture)} tasks")}). "
            + "It carries on when that work finishes.",
        AgentState.Idle => "No turn is running, and it has not replied.",
        AgentState.Running => "Its turns are not reported, so Parley cannot say whether it is working.",
        AgentState.NotRunning => "A node can start it; Parley starts it when a message waits for it.",
        AgentState.CannotStart => "Nothing happens until somebody starts it.",
        AgentState.OpenOutside => "It reads this when it is prompted where it is open.",
        AgentState.Left => "A node that is gone started it, so Parley cannot give it a turn or stop it. It reads this when it next looks.",
        _ => "Nothing the broker reports says where it stands.",
    };

    /// <summary>The flag's words, or <see langword="null"/> when there is none.</summary>
    public string? FlagWords => Flag switch
    {
        ExchangeFlag.Stalled => "Stalled",
        ExchangeFlag.NotCollected => "Not collected yet",
        ExchangeFlag.PermissionRefused => "Stopped: a permission was refused",
        ExchangeFlag.Retired => "Retired",
        _ => null,
    };

    /// <summary>Reads where an agent stands, and any flag on one exchange.</summary>
    /// <param name="agent">Its registry row, or <see langword="null"/> when the registry does not list it.</param>
    /// <param name="flags">The flags report's exchanges.</param>
    /// <param name="exchange">The exchange the chip is for.</param>
    /// <returns>The standing.</returns>
    /// <remarks>In the order of research R2: a process Parley did not start says so first, whatever else is true.</remarks>
    public static InFlightStanding From(AgentRecord? agent, IEnumerable<Flag> flags, long exchange)
    {
        ArgumentNullException.ThrowIfNull(flags);

        var state = agent switch
        {
            null => AgentState.Unknown,
            { Unsupervised: true } => AgentState.Left,
            { OpenOutside: true } => AgentState.OpenOutside,
            { Presence: AgentPresence.Stranded } => AgentState.CannotStart,
            { Presence: AgentPresence.Startable } => AgentState.NotRunning,
            { Presence: AgentPresence.Running, Turns: not { Reported: true } } => AgentState.Running,
            { Presence: AgentPresence.Running, Turns.RunningSince: not null } => AgentState.Working,
            { Presence: AgentPresence.Running, Turns.BackgroundTasks: > 0 } => AgentState.Background,
            { Presence: AgentPresence.Running } => AgentState.Idle,
            _ => AgentState.Unknown,
        };

        var flag = flags
            .Where(one => one.ExchangeId == exchange)
            .Select(one => one.Kind switch
            {
                FlagKind.NoProgress => ExchangeFlag.Stalled,
                FlagKind.NotCollected => ExchangeFlag.NotCollected,
                FlagKind.PermissionRefused => ExchangeFlag.PermissionRefused,
                FlagKind.Retired => ExchangeFlag.Retired,
                _ => (ExchangeFlag?)null,
            })
            .OfType<ExchangeFlag>()
            .Order()
            .Cast<ExchangeFlag?>()
            .FirstOrDefault();

        return new InFlightStanding(
            state,
            state == AgentState.Working ? agent?.Turns?.RunningSince : null,
            flag,
            state == AgentState.Background ? agent?.Turns?.BackgroundTasks ?? 0 : 0);
    }
}

/// <summary>How a closed exchange ended.</summary>
public enum Ending
{
    /// <summary>A Result said it was done.</summary>
    Done = 0,

    /// <summary>A Result answered it.</summary>
    Answered = 1,

    /// <summary>A Result said it was received, with nothing further to do.</summary>
    Received = 2,

    /// <summary>A Result said it was blocked.</summary>
    Blocked = 3,

    /// <summary>The agent settled its own question to the person (SPEC.md §5.6).</summary>
    Settled = 4,

    /// <summary>Closed with no answer: by the person, or by a conversation ending.</summary>
    Closed = 5,
}

/// <summary>How a closed exchange ended, and when.</summary>
/// <param name="ClosedUtc">When it closed.</param>
/// <param name="How">How.</param>
/// <param name="Blocker">For <see cref="Ending.Blocked"/>: the kind, in words.</param>
/// <param name="By">Who sent the Result, or settled it; <see langword="null"/> when nobody did.</param>
public sealed record ExchangeEnding(DateTimeOffset ClosedUtc, Ending How, string? Blocker, AgentName? By)
{
    /// <summary>The ending in words.</summary>
    public string Words => How switch
    {
        Ending.Done => "Done",
        Ending.Answered => "Answered",
        Ending.Received => "Received, nothing further to do",
        Ending.Blocked when Blocker is { } kind => $"Blocked: {kind}",
        Ending.Blocked => "Blocked",
        Ending.Settled => "Settled by the agent",
        _ => "Closed without an answer",
    };
}

/// <summary>One exchange the person is part of, in one of the Inbox's views.</summary>
/// <param name="Exchange">Its number, unique within its project.</param>
/// <param name="Conversation">The conversation it is in now.</param>
/// <param name="Agent">The other side of it.</param>
/// <param name="View">Which view it is in.</param>
/// <param name="OpenedUtc">When it opened.</param>
/// <param name="Last">The last message on it, or <see langword="null"/> when retention removed its words.</param>
/// <param name="Standing">In flight: where the agent stands.</param>
/// <param name="Ended">Closed: how it ended.</param>
public sealed record InboxRow(
    long Exchange,
    string Conversation,
    AgentName Agent,
    InboxView View,
    DateTimeOffset OpenedUtc,
    Message? Last,
    InFlightStanding? Standing,
    ExchangeEnding? Ended)
{
    /// <summary>Whether a follow-up may be offered: not to the facilitator, which is never given work.</summary>
    public bool FollowUp => !Facilitation.Is(Agent);

    /// <summary>When it last moved: its last message, or when it closed or opened.</summary>
    public DateTimeOffset MovedUtc => Last?.CreatedUtc ?? Ended?.ClosedUtc ?? OpenedUtc;
}

/// <summary>One read of the three views for one project.</summary>
/// <param name="Rows">Every row, every view.</param>
/// <param name="Days">How many days the closed window covers.</param>
/// <param name="Older">Which window: 0 for the latest, 1 for the one before it, and so on.</param>
/// <param name="FromUtc">Where the closed window starts.</param>
/// <param name="UntilUtc">Where it ends.</param>
public sealed record InboxReading(
    IReadOnlyList<InboxRow> Rows, int Days, int Older, DateTimeOffset FromUtc, DateTimeOffset UntilUtc)
{
    /// <summary>The rows in one view, in the order a surface shows them.</summary>
    /// <param name="view">The view.</param>
    /// <returns>Newest first.</returns>
    public IReadOnlyList<InboxRow> In(InboxView view) =>
        [.. Rows.Where(row => row.View == view).OrderByDescending(row => row.MovedUtc).ThenByDescending(row => row.Exchange)];

    /// <summary>How many rows a view has.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The count.</returns>
    public int Count(InboxView view) => Rows.Count(row => row.View == view);
}

/// <summary>The Inbox's three views, derived from what the broker already keeps (spec 036).</summary>
/// <remarks>
/// <para>
/// <b>Nothing here is stored.</b> Which view an exchange is in is read from the wait-for graph and the exchange's own state
/// every time (research R1), so it moves exactly when the turn moves and both surfaces agree. Reading changes nothing in
/// the broker: no delivery is taken.
/// </para>
/// <para>
/// One place for the web console and the terminal, so their rows, chips and words cannot drift.
/// </para>
/// </remarks>
public static class Inbox
{
    /// <summary>The window when nothing says otherwise.</summary>
    public const int DefaultDays = 7;

    /// <summary>Reads the three views for one project.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="me">The person's seat in it.</param>
    /// <param name="days">How many days the closed window covers.</param>
    /// <param name="older">Which window: 0 for the latest.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The reading.</returns>
    public static async Task<InboxReading> ReadAsync(
        BrokerClient broker,
        AgentName me,
        int days,
        int older,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        days = days is >= 1 and <= 365 ? days : DefaultDays;
        // At most a century back: a window count from a hand-typed address must not move the clock past its range, and
        // nothing in a store is older than that.
        older = Math.Clamp(older, 0, 36_500 / days);

        var until = nowUtc.AddDays(-days * (double)older);
        var from = until.AddDays(-days);

        var edges = await broker.WaitingAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new List<ConversationSummary>();
        string? next = null;

        do
        {
            var page = await broker
                .ConversationsAsync(me, pageToken: next, includeArchived: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            conversations.AddRange(page.Items);
            next = page.NextPageToken;
        }
        while (next is { Length: > 0 });

        // Only what could hold a row: open work, a message since the window opened, or an exchange that closed since. The
        // close is the broker's own: an exchange can close with no message in its conversation (a question settled by a
        // Request elsewhere, or moved after it was settled), and read by messages alone it was never listed (review, lens
        // 1 round 17). Exporting every conversation instead made each render cost the person's whole history, queued
        // through the broker's one gate ahead of every agent (lens 1 round 18).
        var wanted = conversations
            .Where(c => c.HasOpenExchange || c.LastActivityUtc >= from || c.LastClosedUtc >= from)
            .Select(c => c.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var exports = await Task.WhenAll(wanted.Select(id => broker.ExportAsync(id, cancellationToken, latestOnly: true))).ConfigureAwait(false);

        var inFlight = edges.Any(edge => edge.Waiter == me && edge.WaitedOn != me);
        var registry = inFlight ? await broker.RegistryAsync(cancellationToken).ConfigureAwait(false) : [];
        var flags = inFlight ? (await broker.FlagsAsync(cancellationToken).ConfigureAwait(false)).Exchanges : [];

        return Build(me, edges, exports, registry, flags, days, older, from, until);
    }

    /// <summary>Builds the reading from what was read, with no broker, so it can be held exactly.</summary>
    /// <param name="me">The person's seat.</param>
    /// <param name="edges">The wait-for graph.</param>
    /// <param name="exports">The conversations that could hold a row.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="flags">The flags on exchanges.</param>
    /// <param name="days">The window's length.</param>
    /// <param name="older">Which window.</param>
    /// <param name="from">Where it starts.</param>
    /// <param name="until">Where it ends.</param>
    /// <returns>The reading.</returns>
    public static InboxReading Build(
        AgentName me,
        IReadOnlyList<WaitingEdge> edges,
        IEnumerable<ConversationExport> exports,
        IReadOnlyList<AgentRecord> registry,
        IReadOnlyList<Flag> flags,
        int days,
        int older,
        DateTimeOffset from,
        DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(flags);

        var rows = new Dictionary<long, InboxRow>();

        foreach (var export in exports)
        {
            foreach (var exchange in export.Exchanges)
            {
                if (rows.ContainsKey(exchange.ExchangeId))
                {
                    continue;
                }

                var on = export.Messages.Where(m => m.ExchangeId == exchange.ExchangeId).OrderBy(m => m.Id).ToList();
                var mine = on.Any(m => m.From == me || m.To == me);

                if (exchange.State == ExchangeState.Open)
                {
                    // Named in the conversation its messages are in, so a row's link goes where the exchange is.
                    if (on.Count == 0)
                    {
                        continue;
                    }

                    // Waiting on the person first: an exchange the person is waited on and also waits on is theirs to move.
                    var waitedOn = edges.FirstOrDefault(e => e.ExchangeId == exchange.ExchangeId && e.WaitedOn == me && e.Waiter != me);
                    var waiting = edges.FirstOrDefault(e => e.ExchangeId == exchange.ExchangeId && e.Waiter == me && e.WaitedOn != me);

                    if (waitedOn is not null)
                    {
                        rows[exchange.ExchangeId] = Row(exchange, export, on, waitedOn.Waiter, InboxView.WaitingOnYou, null, null);
                    }
                    else if (waiting is not null)
                    {
                        var agent = registry.FirstOrDefault(r => r.Name == waiting.WaitedOn);

                        rows[exchange.ExchangeId] = Row(
                            exchange, export, on, waiting.WaitedOn, InboxView.InFlight,
                            InFlightStanding.From(agent, flags, exchange.ExchangeId), null);
                    }

                    continue;
                }

                // After the window's start and up to its end, so what closed this instant is in the latest window and every
                // instant is in exactly one.
                if (!mine || exchange.ClosedUtc is not { } closed || closed <= from || closed > until)
                {
                    continue;
                }

                if (on.Select(m => m.From == me ? m.To : m.From).Where(a => a != me).Cast<AgentName?>().FirstOrDefault() is not { } other)
                {
                    continue;
                }

                rows[exchange.ExchangeId] = Row(exchange, export, on, other, InboxView.Closed, null, Ended(exchange, closed, on, other));
            }
        }

        return new InboxReading([.. rows.Values], days, older, from, until);
    }

    private static InboxRow Row(
        ExchangeSnapshot exchange,
        ConversationExport export,
        List<Message> on,
        AgentName agent,
        InboxView view,
        InFlightStanding? standing,
        ExchangeEnding? ended) =>
        new(
            exchange.ExchangeId,
            on.LastOrDefault()?.ConversationId ?? export.Messages.FirstOrDefault()?.ConversationId ?? string.Empty,
            agent,
            view,
            exchange.OpenedUtc,
            on.LastOrDefault(),
            standing,
            ended);

    private static ExchangeEnding Ended(ExchangeSnapshot exchange, DateTimeOffset closed, List<Message> on, AgentName other)
    {
        if (exchange.Outcome == ExchangeOutcome.Settled)
        {
            return new ExchangeEnding(closed, Ending.Settled, null, other);
        }

        // **Closed without an answer is the exchange's outcome, not a Result's.** Closing a request or a conversation
        // writes Results from the broker whose point says "closed", and read as an ordinary Result that fell through to
        // Done, attributed to whoever the broker's Result named. The outcome says it, so it is read first.
        if (exchange.Outcome == ExchangeOutcome.Closed)
        {
            return new ExchangeEnding(closed, Ending.Closed, null, null);
        }

        if (on.LastOrDefault(m => m.Type == MessageType.Result) is not { } result)
        {
            return new ExchangeEnding(closed, Ending.Closed, null, null);
        }

        var points = Payload.Read(result.PayloadJson).Points;

        if (points.FirstOrDefault(p => string.Equals(p.Outcome, "blocked", StringComparison.Ordinal)) is { } blocked)
        {
            return new ExchangeEnding(closed, Ending.Blocked, blocked.Blocker?.Replace('_', ' '), result.From);
        }

        var how = points.Select(p => p.Outcome).FirstOrDefault() switch
        {
            "answered" => Ending.Answered,
            "acknowledged" => Ending.Received,
            "closed" => Ending.Closed,
            _ => Ending.Done,
        };

        return new ExchangeEnding(closed, how, null, how == Ending.Closed ? null : result.From);
    }
}
