using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Seating;

/// <summary>One recipient a Request to several could not be sent to, and why not.</summary>
/// <param name="Agent">Who it was for.</param>
/// <param name="Because">Why the broker would not take it, as a sentence.</param>
/// <remarks>
/// The reason travels with the name. A report of names with no cause is the one shape a person cannot act
/// on, and one cause can refuse a whole roster.
/// </remarks>
internal sealed record TurnedAway(string Agent, string Because);

/// <summary>What became of a Result or a reply a person sent.</summary>
/// <param name="Closed">Whether the exchange is now settled, or the turn handed back.</param>
/// <param name="Says">A sentence for the person who wrote it.</param>
/// <remarks>
/// Two fields rather than a sentence the caller reads. A surface has to know whether to stop showing what
/// is owed, and deciding that by matching the first word of a message breaks the first time it is reworded.
/// </remarks>
public sealed record Answered(bool Closed, string Says);

/// <summary>
/// What a seated person can say, and what became of saying it.
/// </summary>
/// <remarks>
/// <para>
/// A person taking part from a seat is an ordinary participant (SPEC.md §5) and speaks the same protocol as
/// an agent: a Request, a Clarify, a Result. Every one is answered (principle 2.7), so there is no way here
/// to send something that expects nothing back — an announcement is a Request asking for an acknowledgement.
/// </para>
/// <para>
/// The payloads are assembled here rather than accepted whole, exactly as the adapter assembles them, so a
/// Request missing what done looks like comes back as a sentence about thinking rather than a constraint
/// name.
/// </para>
/// </remarks>
/// <param name="agents">The client for the project this person is seated in.</param>
/// <param name="seat">Whose voice this is.</param>
public sealed class SeatVoice(Wire.Agents.AgentsClient agents, Seat seat)
{

    /// <summary>The outcomes a person may give an item they were asked for.</summary>
    /// <remarks>
    /// Each blocked outcome carries its kind of blocker after a colon, so a surface offers one choice and
    /// the Result still names what blocks it (§5.3). <c>closed</c> is not among them: only the broker records
    /// that, when a conversation is closed first.
    /// </remarks>
    public static IReadOnlyList<(string Value, string Says)> Outcomes { get; } =
    [
        ("done", "Done"),
        ("answered", "Answered"),
        ("acknowledged", "Received — nothing further to do"),
        ("blocked:missing_information", "Blocked: I need information nobody here has"),
        ("blocked:conflict", "Blocked: a conflict I could not resolve"),
        ("blocked:permission", "Blocked: a permission I do not have")
    ];

    private readonly Wire.Agents.AgentsClient _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    private readonly Seat _seat = seat ?? throw new ArgumentNullException(nameof(seat));

    /// <summary>Sends one participant a Request: the ask, and the conversation it belongs to.</summary>
    /// <param name="to">Who is being asked.</param>
    /// <param name="ask">What is wanted, in the person's own words (§5.1).</param>
    /// <param name="conversationId">
    /// The conversation it belongs to, or <see langword="null"/> to start a new one the broker names. A person
    /// may always start one (principle 2.10).
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A sentence saying what happened.</returns>
    /// <remarks>
    /// One ask rather than a list: a person composing several in one text box composes something they cannot then
    /// read one outcome against. Sending twice is clearer than sending once ambiguously.
    /// </remarks>
    public async Task<string> RequestAsync(
        AgentName to,
        string? ask,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        if (Unsendable(ask, conversationId) is { } wrong)
        {
            return wrong;
        }

        var payload = Framed(ask!);

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Request, payload.Length, cancellationToken).ConfigureAwait(false);
            return refused;
        }

        try
        {
            var reply = await _agents.RequestAsync(
                Open(to.Value, payload, conversationId), cancellationToken: cancellationToken);

            var outcome = reply.Outcomes.Single();

            return outcome.Ok
                ? $"Sent. Exchange {outcome.ExchangeId} is open in conversation {outcome.ConversationId}, and " +
                  $"{to.Value} owes a Result. " + Waking(outcome.Wake, to)
                : Refusal(outcome, to);
        }
        catch (RpcException failure)
        {
            return Failed(failure);
        }
    }

    /// <summary>Sends every participant given the same Request, each as its own exchange.</summary>
    /// <param name="hold">The seat to send under, re-read before every send.</param>
    /// <param name="peers">Who to send it to, resolved by the surface from its own roster.</param>
    /// <param name="ask">What is wanted, in the person's own words (§5.1).</param>
    /// <param name="conversationId">The conversation they all belong to, or <see langword="null"/> for a new one.</param>
    /// <param name="cancellationToken">Cancels the sends.</param>
    /// <returns>A sentence saying what happened, in counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="peers"/> or <paramref name="hold"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>N ordinary Requests, and no new protocol</b>, which is §5.5's shape addressed from a roster rather
    /// than a role — what a deployment needs when every repository answers to a role of its own.
    /// </para>
    /// <para>
    /// <b>One conversation for all of them.</b> Without a name the first Request starts one and every later
    /// one names it, so the person can read the whole thing in one place (principle 2.10).
    /// </para>
    /// <para>
    /// The seat is re-read before every send and the sends are sequential: a composer that captured its seat
    /// once spoke under a name the book had already released, and a report of where it stopped is only true
    /// if the sends are ordered.
    /// </para>
    /// </remarks>
    public Task<string> RequestEveryoneAsync(
        SeatHold hold,
        IReadOnlyList<AgentName> peers,
        string? ask,
        string? conversationId,
        CancellationToken cancellationToken = default) =>
        RequestEachAsync(hold, peers, ask, _ => conversationId, cancellationToken);

    /// <summary>Sends every participant the same Request, each in a conversation of its own.</summary>
    /// <param name="hold">The seat to send under, re-read before every send.</param>
    /// <param name="peers">Who to send it to, resolved by the surface from its own roster.</param>
    /// <param name="ask">What is wanted, in the person's own words (§5.1).</param>
    /// <param name="conversationFor">
    /// The conversation each peer's exchange belongs to, or null from it for a new one. Asked per peer
    /// rather than taken once, which is the whole difference from <see cref="RequestEveryoneAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the sends.</param>
    /// <returns>A sentence saying what happened, in counts.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="peers"/>, <paramref name="hold"/> or <paramref name="conversationFor"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>One body, two entry points, because the report is the expensive part to get right.</b> Grouping
    /// refusals by reason and abbreviating names after four is 0.25.1's finding — a report that ran off
    /// the screen at the half naming what to do about it — and a second bulk send with its own reporting
    /// would be that lesson learned once and applied in one place.
    /// </para>
    /// <para>
    /// <b>Asking several agents what each of them owns is why this exists.</b> Those are not one
    /// conversation: each discussion is about one agent and its identifier carries that agent's role, so
    /// a shared conversation would put nineteen unrelated arguments in one transcript and leave no
    /// surface able to find the one it wanted.
    /// </para>
    /// </remarks>
    public async Task<string> RequestEachAsync(
        SeatHold hold,
        IReadOnlyList<AgentName> peers,
        string? ask,
        Func<AgentName, string?> conversationFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(conversationFor);

        if (peers.Count == 0)
        {
            return NobodyToAsk;
        }

        // Checked against the first peer's conversation, because the rule being applied is about the
        // *ask* -- whether it is blank, and whether the identifier is one a conversation may carry. A
        // caller deriving an illegal identifier for a later peer is refused by the broker on that send
        // and named in the report, which is where a per-peer fault belongs.
        if (Unsendable(ask, conversationFor(peers[0])) is { } wrong)
        {
            return wrong;
        }

        var payload = Framed(ask!);

        if (TooLong(payload) is { } refused)
        {
            // Recorded once rather than once per recipient: one over-long Request written once is one act.
            await RecordAsync(Wire.MessageType.Request, payload.Length, cancellationToken).ConfigureAwait(false);
            return refused;
        }

        var counted = new Dictionary<Wire.WakeOutcome, int>();
        var unreachable = new List<string>();
        var turnedAway = new List<TurnedAway>();
        var sent = 0;
        var stopped = false;
        // **Carried forward only when the caller gave none.** A bulk send with no identifier is one
        // conversation, the first send naming it and the rest joining; a caller that derives one per peer
        // gets exactly what it derived, and nothing leaks from one peer's discussion into the next.
        string? carried = null;

        foreach (var peer in peers)
        {
            if (hold.Lost)
            {
                stopped = true;
                break;
            }

            var conversation = conversationFor(peer) ?? carried;

            Wire.RequestOutcome outcome;

            try
            {
                var reply = await _agents.RequestAsync(
                    Open(peer.Value, payload, conversation), cancellationToken: cancellationToken);

                outcome = reply.Outcomes.Single();
            }
            catch (RpcException failure)
            {
                // One unreachable participant must not lose the rest.
                turnedAway.Add(new TurnedAway(peer.Value, failure.Status.Detail));
                continue;
            }

            if (!outcome.Ok)
            {
                turnedAway.Add(new TurnedAway(peer.Value, Because(outcome.Rejection)));
                continue;
            }

            carried ??= outcome.ConversationId;
            sent++;

            if (outcome.Wake == Wire.WakeOutcome.Unreachable)
            {
                unreachable.Add(peer.Value);
            }

            counted[outcome.Wake] = counted.GetValueOrDefault(outcome.Wake) + 1;
        }

        return Reported(peers.Count, sent, stopped, counted, unreachable, turnedAway);
    }

    /// <summary>What a Request to several did, in counts rather than in twenty sentences.</summary>
    /// <param name="asked">How many it was going to.</param>
    /// <param name="sent">How many exchanges were opened.</param>
    /// <param name="stopped">Whether the seat went part way through.</param>
    /// <param name="counted">How many of each waking outcome.</param>
    /// <param name="unreachable">Those nothing can start right now.</param>
    /// <param name="turnedAway">Those the broker refused.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// <b>The debt is stated.</b> A Request that landed is an exchange somebody now owes a Result on, and a
    /// report saying only how many were sent would leave that off the screen.
    /// </remarks>
    internal static string Reported(
        int asked,
        int sent,
        bool stopped,
        IReadOnlyDictionary<Wire.WakeOutcome, int> counted,
        IReadOnlyList<string> unreachable,
        IReadOnlyList<TurnedAway> turnedAway)
    {
        ArgumentNullException.ThrowIfNull(counted);
        ArgumentNullException.ThrowIfNull(unreachable);
        ArgumentNullException.ThrowIfNull(turnedAway);

        var said = new StringBuilder().Append(sent == asked ? $"Sent to all {asked}." : $"Sent to {sent} of {asked}.");

        if (sent > 0)
        {
            _ = said.Append(sent == 1 ? " One exchange is open and owed a Result." : $" {sent} exchanges are open, each owed a Result.");
        }

        var parts = new List<string>();

        if (counted.GetValueOrDefault(Wire.WakeOutcome.Started) is > 0 and var starting)
        {
            parts.Add($"{starting} being started");
        }

        if (counted.GetValueOrDefault(Wire.WakeOutcome.AlreadyRunning) is > 0 and var live)
        {
            parts.Add($"{live} already running");
        }

        if (counted.GetValueOrDefault(Wire.WakeOutcome.AlreadyComing) is > 0 and var coming)
        {
            parts.Add($"{coming} already being seen to");
        }

        if (parts.Count > 0)
        {
            _ = said.Append(' ').Append(string.Join(", ", parts)).Append('.');
        }

        // Named rather than counted, because this is the half somebody does something about — and it is
        // not a failure: the Request is stored, and is flagged to the person if nobody collects it.
        if (unreachable.Count > 0)
        {
            _ = said.Append(" Waiting for ")
                .Append(unreachable.Count)
                .Append(" — nothing can start them right now (")
                .Append(Some(unreachable))
                .Append(").");
        }

        Turned(said, turnedAway);

        if (stopped)
        {
            _ = said.Append(' ').Append(Left);
        }

        return said.ToString();
    }

    /// <summary>Why the broker would not open an exchange, worded for a group.</summary>
    /// <param name="rejection">What it said.</param>
    /// <returns>The sentence.</returns>
    internal static string Because(Wire.RequestRejection rejection) => rejection switch
    {
        // The one a fresh deployment meets. An unregistered name cannot be addressed, and the remedy is a
        // start rather than a retry.
        Wire.RequestRejection.ReceiverNotRegistered =>
            "nothing has registered under that name yet. Starting it once is what registers it.",

        Wire.RequestRejection.ReceiverRetired => "it has been retired. Reinstate it, or ask whoever owns its work now.",

        Wire.RequestRejection.Saturated =>
            "you already have as many requests open with it as this deployment allows.",

        Wire.RequestRejection.Duplicate => "you have already sent exactly this, and it is still open.",

        Wire.RequestRejection.ConversationClosed => "that conversation is closed, so nothing can be added to it.",

        Wire.RequestRejection.ConversationNotAssignable => "that conversation cannot take a request from this seat.",

        Wire.RequestRejection.SenderRetired => "your seat has been retired. Reinstate it to send.",

        Wire.RequestRejection.NoSuchRole => "nothing is registered under that role.",

        _ => "the broker refused it and did not say why.",
    };

    /// <summary>What is said when a project holds nobody to send to.</summary>
    internal const string NobodyToAsk = "There is nobody in this project to send this to, so nothing was sent.";

    /// <summary>What is said when the seat went part way through a Request to several.</summary>
    /// <remarks>It names what did go rather than only what did not, so a person knows what to send again.</remarks>
    internal const string Left = "Your seat was released part way through, so the rest were not sent.";

    /// <summary>What is wrong with a Request before any of it goes on the wire.</summary>
    /// <param name="ask">What is wanted.</param>
    /// <param name="conversationId">The conversation named, or null.</param>
    /// <returns>The refusal, or null when there is none.</returns>
    /// <remarks>
    /// Shared by the single and plural forms. What was checked here until 0.7 — an intent, a kind, and a finish line
    /// of at least twenty characters — went with the fields: a receiver that cannot scope an ask sends a Clarify
    /// (§5.2), which is a question the receiver is in a position to ask and the sender is not.
    /// </remarks>
    private static string? Unsendable(string? ask, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(ask))
        {
            return "Nothing was asked for, so nothing was sent.";
        }

        return conversationId is null ? null : ConversationName.Problem(conversationId);
    }

    private static string Framed(string ask) => new JsonObject { ["ask"] = ask }.ToJsonString();

    private Wire.OpenRequest Open(string to, string payload, string? conversationId) => new()
    {
        From = _seat.Name.Value,
        To = to,
        ResponderModel = _seat.Responder,
        PayloadJson = payload,
        ConversationId = conversationId ?? string.Empty
    };

    /// <summary>Appends what the broker refused, grouped by why.</summary>
    /// <param name="said">The report so far.</param>
    /// <param name="turnedAway">Those the broker refused.</param>
    /// <param name="what">What became of them, for a report about something other than a refusal.</param>
    /// <remarks>
    /// The count first, the reason next, the names last, and the groups bounded: a terminal shows this in one
    /// row, so what comes first is what survives, and a broker whose detail names the recipient gives one
    /// group each.
    /// </remarks>
    private static void Turned(
        StringBuilder said,
        IReadOnlyList<TurnedAway> turnedAway,
        string what = "Refused for")
    {
        var groups = turnedAway
            .GroupBy(refusal => Reason(refusal.Because), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToList();

        foreach (var group in groups.Take(Causes))
        {
            _ = said.Append(' ')
                .Append(what)
                .Append(' ')
                .Append(group.Count())
                .Append(": ")
                .Append(group.Key)
                .Append(" (")
                .Append(Some([.. group.Select(refusal => refusal.Agent)]))
                .Append(')');
        }

        if (groups.Count > Causes)
        {
            _ = said.Append($" {groups.Skip(Causes).Sum(group => group.Count())} more were refused, each for a different reason.");
        }
    }

    /// <summary>How many names to print before counting the rest.</summary>
    private const int Named = 4;

    /// <summary>How many distinct reasons to spell out before counting the rest.</summary>
    private const int Causes = 2;

    private static string Reason(string because) =>
        Flat(because) is { Length: > 0 } said ? said : Because(Wire.RequestRejection.Unspecified);

    private static string Some(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return names.Count <= Named
            ? string.Join(", ", names.Select(Flat))
            : string.Join(", ", names.Take(Named).Select(Flat)) + $" and {names.Count - Named} more";
    }

    /// <summary>Puts a piece of somebody else's text on one line.</summary>
    /// <param name="text">The text.</param>
    /// <returns>One line of it.</returns>
    /// <remarks>
    /// A refusal's reason is <c>Status.Detail</c> — whatever came back from the far end of a gRPC call — and
    /// this report is contracted to be one line. A newline in the detail once produced a second line reading
    /// as this report's own success sentence, written by somebody else.
    /// </remarks>
    private static string Flat(string text) => text.ReplaceLineEndings(" ");

    /// <summary>What became of the receiver, in words.</summary>
    /// <param name="wake">What the broker did about it.</param>
    /// <param name="to">Who it was sent to.</param>
    /// <returns>A sentence to show beside the send.</returns>
    /// <remarks>
    /// None of these is a failure: the Request is stored before any of it is decided, and one nobody can
    /// start right now is flagged to the person if it goes uncollected (§6).
    /// </remarks>
    internal static string Waking(Wire.WakeOutcome wake, AgentName to) => wake switch
    {
        Wire.WakeOutcome.Started => $"{to.Value} was not running, so it is being started.",
        Wire.WakeOutcome.AlreadyRunning => $"{to.Value} is running and has been handed it.",
        Wire.WakeOutcome.AlreadyComing => $"{to.Value} is already being seen to, and will collect this with the rest.",
        Wire.WakeOutcome.Unreachable =>
            $"Nothing can start {to.Value} right now; it collects this when it next runs, and you will see it " +
            "flagged if that takes too long.",
        _ => $"What became of {to.Value} was not reported.",
    };

    /// <summary>How much is waiting, without taking any of it.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Messages waiting, and closure polls waiting.</returns>
    /// <remarks>Marks nothing delivered, which is what makes it safe to call while rendering a page.</remarks>
    public async Task<(int Messages, int ClosurePolls)> WaitingAsync(CancellationToken cancellationToken = default)
    {
        var notice = await _agents.WaitingAsync(
            new Wire.WaitingRequest { Agent = _seat.Name.Value }, cancellationToken: cancellationToken);

        return (notice.MessagesWaiting, notice.ClosurePollsWaiting);
    }

    /// <summary>Takes everything waiting for this person, which is what marks it collected.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was handed over, and how many closure polls came with it.</returns>
    /// <remarks>
    /// <b>This is not a read.</b> Collecting a Request is where §6's second stage begins, so a person who
    /// collects work and does nothing with it is flagged as stalled rather than as never having picked it up.
    /// It is offered as something a person does on purpose, never as part of rendering a page.
    /// </remarks>
    public async Task<(IReadOnlyList<Delivered> Messages, int ClosurePolls)> DrainAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _agents.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = _seat.Name.Value, Limit = 0 },
            cancellationToken: cancellationToken);

        return ([.. reply.Messages.Select(one => new Delivered(Read(one.Message)))], reply.ClosurePolls.Count);
    }

    /// <summary>Collects and records what came back against the seat, as one act.</summary>
    /// <param name="hold">The seat to record against.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was taken, and how many closure polls came with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> is null.</exception>
    /// <remarks>
    /// Here rather than in each surface, so two surfaces cannot disagree about what a person has taken
    /// delivery of.
    /// </remarks>
    public async Task<(IReadOnlyList<Delivered> Messages, int ClosurePolls)> TakeDeliveryAsync(
        SeatHold hold, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        var (messages, polls) = await DrainAsync(cancellationToken).ConfigureAwait(false);
        hold.Record(messages);

        return (messages, polls);
    }

    /// <summary>Sends the Result of a Request this person was asked, and settles it against the seat when it closed.</summary>
    /// <param name="hold">The seat holding the delivered Request.</param>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="outcomes">One entry per item: its id, what came of it, and an outcome from <see cref="Outcomes"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened, and whether the exchange closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> is null.</exception>
    /// <remarks>
    /// Settled only when the broker says the exchange closed, so a surface never shows nothing owed while the
    /// broker still believes otherwise.
    /// </remarks>
    public async Task<Answered> ResultAsync(
        SeatHold hold,
        long exchangeId,
        IReadOnlyList<(string Id, string Text, string Outcome)> outcomes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        var answered = await ResultAsync(exchangeId, outcomes, cancellationToken).ConfigureAwait(false);

        if (answered.Closed)
        {
            hold.Settled(exchangeId);
        }

        return answered;
    }

    /// <summary>Sends the Result of a Request this person was asked.</summary>
    /// <param name="exchangeId">The exchange being closed.</param>
    /// <param name="outcomes">One entry per item: its id, what came of it, and an outcome from <see cref="Outcomes"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// The rolled-up outcome is derived by the broker, and it checks the items against the stored Request
    /// and names the ones it expected when they differ — so this does not second-guess either.
    /// </remarks>
    public async Task<Answered> ResultAsync(
        long exchangeId,
        IReadOnlyList<(string Id, string Text, string Outcome)> outcomes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (outcomes.Count == 0)
        {
            return new Answered(false, "That request carried nothing to report on.");
        }

        if (outcomes.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Text)) is { Id: { } blank })
        {
            return new Answered(
                false,
                $"Write something for {blank}. A Result that says nothing closes the exchange while telling the " +
                "requester nothing.");
        }

        var entries = new JsonArray();

        foreach (var (id, text, chosen) in outcomes)
        {
            var parts = (chosen ?? string.Empty).Split(':', 2);

            if (!Outcomes.Any(o => o.Value == chosen) || !OutcomeTokens.TryParse(parts[0], out Outcome _))
            {
                return new Answered(false, $"'{chosen}' is not an outcome this surface offers.");
            }

            var entry = new JsonObject { ["id"] = id, ["outcome"] = parts[0], ["text"] = text };

            if (parts.Length == 2)
            {
                entry["blocker"] = parts[1];
            }

            entries.Add(entry);
        }

        var payload = new JsonObject { ["outcomes"] = entries }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Result, payload.Length, cancellationToken).ConfigureAwait(false);
            return new Answered(false, refused);
        }

        try
        {
            var reply = await _agents.ResultAsync(
                new Wire.ResultRequest
                {
                    From = _seat.Name.Value,
                    ExchangeId = exchangeId,
                    ResponderModel = _seat.Responder,
                    PayloadJson = payload
                },
                cancellationToken: cancellationToken);

            return reply.Ok
                ? new Answered(true, "Sent, and the exchange is closed.")
                : new Answered(false, Refused(reply));
        }
        catch (RpcException failure)
        {
            return new Answered(false, Failed(failure));
        }
    }

    /// <summary>Performs a draft: one act per recipient, each the one its own pair state calls for.</summary>
    /// <param name="hold">The seat this is sent under.</param>
    /// <param name="draft">What was written, who it goes to, and what Enter was said to do.</param>
    /// <param name="conversationId">The conversation the room is showing.</param>
    /// <param name="cancellationToken">Cancels the sends.</param>
    /// <returns>How many recipients got it, and a sentence saying what happened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> or <paramref name="draft"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The acts are the draft's, not this method's.</b> What typing means per recipient is derived from who
    /// owes whom (<see cref="Pairing"/>), and this performs exactly what the person was shown before they pressed
    /// Enter. That is the whole of SC-002, and the reason the plan is read once here rather than recomputed: a
    /// second derivation between the preview and the send is a second opinion about what was promised.
    /// </para>
    /// <para>
    /// <b>Length is checked for every recipient before anything is sent.</b> Each act frames its payload
    /// differently, so one line can fit as a Clarify and not as a Result; refusing the whole draft up front is
    /// what keeps a partial send from being the first thing a person learns about the cap.
    /// </para>
    /// <para>
    /// <b>N recipients is N sends</b> (SPEC.md section 5.5), and a failure against one must not lose the rest.
    /// The seat is re-checked each time round, because the sweep can release it while this is running and nothing
    /// may be recorded under a name the book no longer holds.
    /// </para>
    /// </remarks>
    public async Task<Sent> SendAsync(
        SeatHold hold,
        Draft draft,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(draft.Text))
        {
            return new Sent(0, NothingWritten);
        }

        var plan = draft.Plan();

        if (plan.Count == 0)
        {
            return new Sent(0, NobodyToAsk);
        }

        // Every payload measured first. A draft that fits one act and not another would otherwise send in part.
        foreach (var addressed in plan)
        {
            if (TooLong(Shaped(addressed, draft, hold)) is { } refused)
            {
                await RecordAsync(Wire.MessageType.Request, draft.Text.Length, cancellationToken)
                    .ConfigureAwait(false);

                return new Sent(0, refused);
            }
        }

        var done = new List<string>();
        var turnedAway = new List<TurnedAway>();
        var instead = new List<TurnedAway>();
        var stopped = false;

        foreach (var addressed in plan)
        {
            if (hold.Lost)
            {
                stopped = true;

                break;
            }

            try
            {
                var problem = await PerformAsync(hold, addressed, draft, conversationId, cancellationToken)
                    .ConfigureAwait(false);

                if (problem is null)
                {
                    done.Add(addressed.Peer.Value);
                }
                else if (addressed.Act != Act.Ask
                    && await RequestAsync(addressed.Peer, draft.Text, conversationId, cancellationToken)
                        .ConfigureAwait(false) is var retried
                    && retried.StartsWith("Sent.", StringComparison.Ordinal))
                {
                    // **A message a person typed is delivered, whatever the bookkeeping says.**
                    //
                    // The acts of §5.1 to §5.3 are about whose turn it is, and this surface derives which one
                    // applies from the wait-for graph and the transcript. When that derivation is wrong the
                    // broker refuses — correctly — and the words were simply lost: the owner watched a message
                    // to two agents reach neither, with two different reasons about turns and nothing said
                    // about what to do instead.
                    //
                    // So a refused act becomes a Request, which opens an exchange of its own and is admissible
                    // whatever stands between the two of them. It is said rather than hidden: a new exchange
                    // is an obligation somebody now owes a Result on, and §5.5 is the reason this surface
                    // shows the bill before Enter rather than after.
                    done.Add(addressed.Peer.Value);
                    instead.Add(new TurnedAway(addressed.Peer.Value, problem));
                }
                else
                {
                    turnedAway.Add(new TurnedAway(addressed.Peer.Value, problem));
                }
            }
            catch (RpcException failure)
            {
                // One unreachable participant must not lose the rest.
                turnedAway.Add(new TurnedAway(addressed.Peer.Value, failure.Status.Detail));
            }
        }

        // Grouped by reason through the same helper the broadcast uses, so one over-long report cannot
        // differ between the two places a person sends to several agents at once.
        var report = new StringBuilder($"Sent to {done.Count} of {plan.Count}.");

        if (instead.Count > 0)
        {
            // Named, with the reason the derived act could not be used: a new exchange is an obligation, and
            // this surface does not open one quietly.
            Turned(report, instead, "Sent as a new request instead");
        }

        if (turnedAway.Count > 0)
        {
            Turned(report, turnedAway);
        }

        if (stopped)
        {
            _ = report.Append(' ').Append(Left);
        }

        return new Sent(done.Count, report.ToString());
    }

    /// <summary>What is said when Enter was pressed on an empty line.</summary>
    internal const string NothingWritten = "Nothing was written, so nothing was sent.";

    /// <summary>The outcome a person answering in prose is recorded under.</summary>
    /// <remarks>
    /// <b>A simplification, and recorded as one.</b> Section 5.3 offers done, answered, acknowledged and blocked,
    /// and a person typing a reply into a line is not asked which. <c>answered</c> is the honest default for prose
    /// -- it is what a question comes back as -- but somebody who did work and wants it recorded as <c>done</c>,
    /// or who is blocked and must name a blocker, still needs the result form. Choosing the word from the line is
    /// worth doing and is not done here.
    /// </remarks>
    private const string Answered = "answered";

    /// <summary>Performs one recipient's act.</summary>
    /// <param name="hold">The seat.</param>
    /// <param name="addressed">The recipient and what was promised.</param>
    /// <param name="draft">The draft.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The problem, or null when it went.</returns>
    private async Task<string?> PerformAsync(
        SeatHold hold,
        Addressed addressed,
        Draft draft,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (addressed.Act == Act.Ask)
        {
            var said = await RequestAsync(addressed.Peer, draft.Text, conversationId, cancellationToken)
                .ConfigureAwait(false);

            return said.StartsWith("Sent.", StringComparison.Ordinal) ? null : said;
        }

        if (addressed.Act == Act.Answer)
        {
            var answered = await ResultAsync(
                hold,
                addressed.Exchange!.Value,
                [.. Held(hold, addressed.Exchange.Value).Select(id => (id, draft.Text, Answered))],
                cancellationToken).ConfigureAwait(false);

            return answered.Closed ? null : answered.Says;
        }

        var clarified = await ClarifyAsync(hold, addressed.Exchange!.Value, draft.Text, cancellationToken)
            .ConfigureAwait(false);

        return clarified.Closed ? null : clarified.Says;
    }

    /// <summary>What a recipient's act would put on the wire, for measuring before anything is sent.</summary>
    /// <param name="addressed">The recipient and its act.</param>
    /// <param name="draft">The draft.</param>
    /// <param name="hold">The seat, for the items a Result must answer.</param>
    /// <returns>The payload as it would be stored.</returns>
    private static string Shaped(Addressed addressed, Draft draft, SeatHold hold) =>
        addressed.Act switch
        {
            Act.Ask => Framed(draft.Text),
            Act.Answer => new JsonObject
            {
                ["outcomes"] = new JsonArray([.. Held(hold, addressed.Exchange!.Value).Select(id =>
                    (JsonNode?)new JsonObject { ["id"] = id, ["outcome"] = Answered, ["text"] = draft.Text })]),
            }.ToJsonString(),
            _ => new JsonObject { ["text"] = draft.Text }.ToJsonString(),
        };

    /// <summary>The items a Result on one exchange must answer.</summary>
    /// <param name="hold">The seat holding it.</param>
    /// <param name="exchangeId">The exchange.</param>
    /// <returns>The identifiers, which for anything written since 0.7 is the one spelling section 5.3 reserves.</returns>
    private static IReadOnlyList<string> Held(SeatHold hold, long exchangeId) =>
        hold.Delivered.FirstOrDefault(d => d.Message.ExchangeId == exchangeId) is { } held
            ? held.Items()
            : [PayloadShape.WholeRequest];

    /// <summary>Sends a Clarify on an exchange this person holds, and settles the seat when the turn went.</summary>
    /// <param name="hold">The seat holding the delivered message.</param>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="text">What is asked, or the reply to what they asked.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened; <see cref="Answered.Closed"/> means the turn went back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> is null.</exception>
    /// <remarks>
    /// Settled for the reason <see cref="ResultAsync(SeatHold, long, IReadOnlyList{ValueTuple{string, string, string}}, CancellationToken)"/>
    /// settles: once the turn is theirs, nothing is owed on what this seat holds until they send something new.
    /// </remarks>
    public async Task<Answered> ClarifyAsync(
        SeatHold hold,
        long exchangeId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        var sent = await ClarifyAsync(exchangeId, text, cancellationToken).ConfigureAwait(false);

        if (sent.Closed)
        {
            hold.Settled(exchangeId);
        }

        return sent;
    }

    /// <summary>Replies to a clarification this person was asked, handing the turn back.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="text">The reply.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened; <see cref="Answered.Closed"/> means the turn went back.</returns>
    public async Task<Answered> ClarifyAsync(long exchangeId, string? text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Answered(false, "Nothing was written, so nothing was sent.");
        }

        var payload = new JsonObject { ["text"] = text }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Clarify, payload.Length, cancellationToken).ConfigureAwait(false);
            return new Answered(false, refused);
        }

        try
        {
            var reply = await _agents.ClarifyAsync(
                new Wire.ClarifyRequest
                {
                    From = _seat.Name.Value,
                    ExchangeId = exchangeId,
                    ResponderModel = _seat.Responder,
                    PayloadJson = payload
                },
                cancellationToken: cancellationToken);

            return reply.Ok ? new Answered(true, "Sent. The turn is back with them.") : new Answered(false, Refused(reply));
        }
        catch (RpcException failure)
        {
            return new Answered(false, Failed(failure));
        }
    }

    private static string Refused(Wire.SayReply reply) => reply.Refusal switch
    {
        Wire.TransitionError.ExchangeNotOpen => "That exchange is closed: a Result was sent, or its conversation was closed.",
        Wire.TransitionError.NotYourTurn => "The other side holds the next move on that exchange.",
        Wire.TransitionError.NotAParticipant => "That exchange is not yours.",
        Wire.TransitionError.OnlyReceiverSendsResult => "You sent that request, so its receiver sends the Result.",
        Wire.TransitionError.OutcomesDoNotMatchItems =>
            $"That request asked {string.Join(", ", reply.ExpectedItems)}; give each exactly one outcome.",
        Wire.TransitionError.ClarifyDepthExceeded => "This exchange has reached the clarification cap its deployment set.",
        _ => "The broker refused it and did not say why."
    };

    private static Message Read(Wire.Message wire) => new(
        wire.Id,
        wire.ExchangeId,
        BrokerClient.Type(wire.Type),
        AgentName.Create(wire.From),
        AgentName.Create(wire.To),
        wire.ResponderModel,
        wire.PayloadJson,
        wire.ConversationId,
        DateTimeOffset.Parse(wire.CreatedUtc, CultureInfo.InvariantCulture),
        string.IsNullOrEmpty(wire.DeliveredUtc) ? null : DateTimeOffset.Parse(wire.DeliveredUtc, CultureInfo.InvariantCulture));

    /// <summary>Refuses a payload over the cap, in the same words the adapter uses.</summary>
    /// <param name="payload">The assembled document.</param>
    /// <returns>The refusal, or null when it fits.</returns>
    private static string? TooLong(string payload) =>
        payload.Length <= MessageLimits.MaxPayloadCharacters
            ? null
            : $"That is {payload.Length} characters and the limit is {MessageLimits.MaxPayloadCharacters}. Send what " +
              "the receiver needs for its part — the contract, the interface, the version — and point at the rest.";

    /// <summary>Records that a message was refused for its size, so the cap has evidence.</summary>
    /// <param name="type">Which kind of message it would have been.</param>
    /// <param name="characters">How long it was. Never the text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task completing when it has been recorded, or given up on.</returns>
    private async Task RecordAsync(Wire.MessageType type, int characters, CancellationToken cancellationToken)
    {
        try
        {
            await _agents.RecordRefusalAsync(
                new Wire.RecordRefusalRequest { Agent = _seat.Name.Value, Type = type, Characters = characters },
                cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // The person has already been told; a broker that cannot record a refusal must not turn it into a
            // second error they can do nothing about.
        }
    }

    /// <summary>Puts a refused Request into words.</summary>
    /// <param name="outcome">What the broker said.</param>
    /// <param name="to">Who it was for.</param>
    /// <returns>A sentence, with the remedy where there is one.</returns>
    private static string Refusal(Wire.RequestOutcome outcome, AgentName to) => outcome.Rejection switch
    {
        Wire.RequestRejection.Saturated =>
            $"You already have as many requests open with {to.Value} as this deployment allows.",
        Wire.RequestRejection.Duplicate => "You have already sent exactly this, and it is still open.",
        Wire.RequestRejection.ReceiverNotRegistered =>
            $"Nothing has registered as {to.Value} yet. Starting it once is what registers it.",
        Wire.RequestRejection.ReceiverRetired => $"{to.Value} has been retired.",
        Wire.RequestRejection.ConversationClosed => "That conversation is closed, so nothing can be added to it.",
        Wire.RequestRejection.ConversationNotAssignable => "That conversation cannot take a request from this seat.",
        Wire.RequestRejection.SenderRetired => "Your seat has been retired. Reinstate it to send.",
        _ => "The broker refused it and did not say why."
    };

    private static string Failed(RpcException failure) =>
        failure.StatusCode == StatusCode.Unavailable
            ? "This project's broker is not answering, so nothing was sent."
            : $"The broker refused it: {failure.Status.Detail}";
}
