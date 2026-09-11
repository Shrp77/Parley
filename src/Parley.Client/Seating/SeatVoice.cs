using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Seating;

/// <summary>One recipient a broadcast could not be sent to, and why not.</summary>
/// <param name="Agent">Who it was for.</param>
/// <param name="Because">Why the broker would not take it, as a sentence.</param>
/// <remarks>
/// <para>
/// <b>The reason used to be dropped and the name kept, which is exactly the wrong half.</b> The
/// wire carries a rejection on every refusal and the single-recipient path already turns it into a
/// sentence; a broadcast, where one cause can refuse the whole roster, reported nineteen names and
/// no cause at all. A person reading that knows who did not get it and has no idea what to do.
/// </para>
/// <para>
/// <b>Internal, because nothing outside this assembly has to live with it.</b> It appears in the
/// signature of <see cref="SeatVoice.Reported"/>, which is internal, and in the body of the
/// broadcast; a first version was public, which is the assembly's surface widened for a type
/// nobody outside it can reach a use for. Lens 3.
/// </para>
/// </remarks>
internal sealed record TurnedAway(string Agent, string Because);

/// <summary>What became of an answer.</summary>
/// <param name="Closed">Whether the exchange is now settled.</param>
/// <param name="Says">A sentence for the person who wrote it.</param>
/// <remarks>
/// Two fields rather than a sentence the caller reads. The inbox has to know whether to stop
/// showing the debt, and deciding that by matching the first word of a message is a coupling
/// that breaks silently the first time somebody rewords it — leaving an answered question on
/// the page for ever, or worse, clearing one that was refused.
/// </remarks>
public sealed record Answered(bool Closed, string Says);

/// <summary>
/// The two things a seated person can say, and what became of saying them.
/// </summary>
/// <remarks>
/// <para>
/// Ask and notify, of the operations SPEC.md §10.1 gives an agent, and no more. A person at a
/// console is a participant rather than a full adapter: they have not been delivered an exchange,
/// so they hold no turn to answer or clarify on, and concluding a discussion they can only see
/// half of is not a judgement this surface can support. Those are real operations a seat should
/// eventually reach, and each needs the inbox this does not yet have.
/// </para>
/// <para>
/// The payloads are assembled here rather than accepted whole, exactly as the adapter assembles
/// them. §8 rule 1 is enforced by the schema, so a question missing its consequences comes back
/// as a <c>CHECK</c> constraint naming <c>json_extract</c> — which is the least useful thing a
/// person could be shown for a mistake that is about thinking rather than about formatting.
/// </para>
/// </remarks>
/// <param name="agents">The client for the project this person is seated in.</param>
/// <param name="seat">Whose voice this is.</param>
public sealed class SeatVoice(Wire.Agents.AgentsClient agents, Seat seat)
{
    /// <summary>The shortest <c>consequences</c> the schema will store, per SPEC.md §8 rule 1.</summary>
    /// <remarks>
    /// The same floor the adapter holds agents to, and held to for the same reason: a question
    /// that says nothing about what its answer is for cannot be scoped by the peer or used by the
    /// asker once their own attention has moved on. A person asking is not exempt from the rule
    /// the rest of the protocol leans on.
    /// </remarks>
    public const int MinimumConsequences = 40;

    private readonly Wire.Agents.AgentsClient _agents = agents
        ?? throw new ArgumentNullException(nameof(agents));

    private readonly Seat _seat = seat ?? throw new ArgumentNullException(nameof(seat));

    /// <summary>Asks one agent one question, inside a conversation.</summary>
    /// <param name="to">Who is being asked.</param>
    /// <param name="intent">What the person is trying to decide.</param>
    /// <param name="question">The question itself, which becomes sub-question <c>q1</c>.</param>
    /// <param name="consequences">What turns on the answer.</param>
    /// <param name="conversationId">The discussion it belongs to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A sentence saying what happened.</returns>
    /// <remarks>
    /// One sub-question rather than a list. §5.3 answers by identifier and a person composing
    /// several in a text box would be composing something they cannot then read the answers
    /// against; asking twice is clearer than asking once ambiguously.
    /// </remarks>
    public async Task<string> AskAsync(
        AgentName to,
        string? intent,
        string? question,
        string? consequences,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "Nothing was asked, so nothing was sent.";
        }

        if (BadConversation(conversationId) is { } named)
        {
            return named;
        }

        if (string.IsNullOrWhiteSpace(intent))
        {
            return "Say what you are trying to decide. The peer scopes its answer to it, and so "
                 + "will you when this comes back hours from now.";
        }

        if (consequences is null || consequences.Trim().Length < MinimumConsequences)
        {
            return "Say what you will do with each possible answer, in at least "
                 + $"{MinimumConsequences} characters. That is SPEC.md §8 rule 1, and it is what "
                 + "tells the peer how much precision is worth its time.";
        }

        var payload = new JsonObject
        {
            ["intent"] = intent,
            ["sub_questions"] = new JsonArray(
                new JsonObject { ["id"] = "q1", ["text"] = question }),
            ["consequences"] = consequences
        }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Question, payload.Length, cancellationToken)
                .ConfigureAwait(false);

            return refused;
        }

        try
        {
            var reply = await _agents.AskAsync(
                new Wire.AskRequest
                {
                    From = _seat.Name.Value,
                    To = to.Value,
                    ResponderModel = _seat.Responder,
                    PayloadJson = payload,
                    ConversationId = conversationId
                },
                cancellationToken: cancellationToken);

            return reply.Ok
                ? $"Asked. Exchange {reply.ExchangeId} is open and {to.Value} owes the answer."
                : Refusal(reply.Rejection, to);
        }
        catch (RpcException failure)
        {
            return Failed(failure);
        }
    }

    /// <summary>Tells one agent something, expecting no reply.</summary>
    /// <param name="to">Who is being told.</param>
    /// <param name="text">What they are being told.</param>
    /// <param name="conversationId">The discussion it belongs to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A sentence saying what happened.</returns>
    public async Task<string> NotifyAsync(
        AgentName to,
        string? text,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Nothing was written, so nothing was sent.";
        }

        if (BadConversation(conversationId) is { } named)
        {
            return named;
        }

        var payload = new JsonObject { ["text"] = text }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Notify, payload.Length, cancellationToken)
                .ConfigureAwait(false);

            return refused;
        }

        try
        {
            var reply = await _agents.NotifyAsync(
                new Wire.NotifyRequest
                {
                    From = _seat.Name.Value,
                    To = to.Value,
                    ResponderModel = _seat.Responder,
                    PayloadJson = payload,
                    ConversationId = conversationId
                },
                cancellationToken: cancellationToken);

            if (!reply.Ok)
            {
                return reply.Refusal switch
                {
                    Wire.NotifyRejection.RecipientUnroutable =>
                        $"{to.Value} is not somewhere this can reach.",
                    Wire.NotifyRejection.SenderRetired =>
                        "Your seat has been retired, so nothing was sent.",
                    Wire.NotifyRejection.ConversationClosed =>
                        "That conversation is closed. Nothing was added to it.",
                    _ => "The broker refused it and did not say why."
                };
            }

            return reply.Deduplicated
                ? "Already sent — this was the same notice again, so it was not repeated."
                : "Sent. " + Waking(reply.Wake, to);
        }
        catch (RpcException failure)
        {
            return Failed(failure);
        }
    }

    /// <summary>Tells every agent in a project the same thing.</summary>
    /// <param name="hold">The seat it is being said from.</param>
    /// <param name="peers">Who to tell, resolved by the surface from its own roster.</param>
    /// <param name="text">What they are being told.</param>
    /// <param name="conversationId">The discussion it belongs to.</param>
    /// <param name="cancellationToken">Cancels the sends.</param>
    /// <returns>A sentence saying what happened, in counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="peers"/> or the hold is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>N ordinary notifications, and no new protocol.</b> SPEC.md §5.5 settled this shape for
    /// questions — "not a new kind of message and not a new state machine" — because a one-to-many
    /// exchange would need its own admission rules, its own cap arithmetic and its own definition
    /// of concluded. Every one of those costs is <em>absent</em> here, since a notification opens no
    /// exchange, takes no slot and creates no debt, so the argument for restraint is stronger than
    /// in the case that already took it.
    /// </para>
    /// <para>
    /// <b>The seat is re-read before every send, not once at the start.</b> A broadcast to twenty
    /// agents is twenty round trips, and the idle sweep runs throughout — so the rule established in
    /// 0.18.9 applies with more force here than anywhere: a composer that captured its seat spoke
    /// under a name the book had already released. Stopping half way and saying so is the honest
    /// outcome; finishing is not.
    /// </para>
    /// <para>
    /// Sequential rather than concurrent, deliberately. Twenty concurrent notifications would race
    /// the broker's per-recipient coalescing to no purpose — the recipients differ, so nothing is
    /// coalesced anyway — and would make the "stopped after n" report above meaningless.
    /// </para>
    /// </remarks>
    public async Task<string> NotifyEveryoneAsync(
        SeatHold hold,
        IReadOnlyList<AgentName> peers,
        string? text,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(peers);

        if (peers.Count == 0)
        {
            return NobodyToTell;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "Nothing was written, so nothing was sent.";
        }

        if (BadConversation(conversationId) is { } named)
        {
            return named;
        }

        var payload = new JsonObject { ["text"] = text }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            // Recorded once rather than once per recipient. §17 question 16 counts refusals as
            // evidence about the cap, and twenty rows for one over-long message somebody wrote once
            // would be twenty pieces of evidence about a single act.
            await RecordAsync(Wire.MessageType.Notify, payload.Length, cancellationToken)
                .ConfigureAwait(false);

            return refused;
        }

        var counted = new Dictionary<Wire.WakeOutcome, int>();
        var unreachable = new List<string>();
        var turnedAway = new List<TurnedAway>();
        var told = 0;
        var repeated = 0;
        var stopped = false;

        foreach (var peer in peers)
        {
            if (hold.Lost)
            {
                stopped = true;

                break;
            }

            Wire.NotifyReply reply;

            try
            {
                reply = await _agents.NotifyAsync(
                    new Wire.NotifyRequest
                    {
                        From = _seat.Name.Value,
                        To = peer.Value,
                        ResponderModel = _seat.Responder,
                        PayloadJson = payload,
                        ConversationId = conversationId
                    },
                    cancellationToken: cancellationToken);
            }
            catch (RpcException failure)
            {
                // One unreachable peer must not lose the other nineteen sends. The broker being
                // gone entirely will show up as every one of them failing, which the report says.
                turnedAway.Add(new TurnedAway(peer.Value, failure.Status.Detail));

                continue;
            }

            if (!reply.Ok)
            {
                // **The reason travels with the name, and it used to be thrown away here.** The
                // report then read "Refused for" and nineteen names, which is the one shape that
                // cannot be acted on: the wire already carries why, the single-recipient path
                // already says it, and this — the path where every recipient can be refused for
                // one cause — was the one that said least.
                turnedAway.Add(new TurnedAway(peer.Value, Because(reply.Refusal)));

                continue;
            }

            if (reply.Deduplicated)
            {
                // **Not counted as told, because nothing was stored.** §13.1 refuses a
                // byte-identical repeat within its window, and a person re-sending a broadcast
                // after a partial failure is exactly who meets it — so reporting "told all
                // nineteen" here would be the "Sent." defect this feature exists to remove,
                // rebuilt in the code that removes it.
                repeated++;

                continue;
            }

            told++;

            if (reply.Wake == Wire.WakeOutcome.Unreachable)
            {
                unreachable.Add(peer.Value);
            }

            counted[reply.Wake] = counted.GetValueOrDefault(reply.Wake) + 1;
        }

        return Reported(peers.Count, told, repeated, stopped, counted, unreachable, turnedAway);
    }

    /// <summary>What a broadcast did, in counts rather than in twenty sentences.</summary>
    /// <param name="asked">How many were going to be told.</param>
    /// <param name="told">How many were.</param>
    /// <param name="repeated">How many were refused as the same notice again.</param>
    /// <param name="stopped">Whether the seat went part way through.</param>
    /// <param name="counted">How many of each outcome.</param>
    /// <param name="unreachable">Those nothing here can start.</param>
    /// <param name="turnedAway">Those the broker refused.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    /// <b>Counts, and then names for the two things a person can act on.</b> Twenty sentences is
    /// not a report, and a bare "sent" hides the case that matters: a broadcast that reached three
    /// of nineteen is a different fact from one that reached all nineteen, and the surface saying
    /// nothing about the difference is what produced the report this feature answers.
    /// </remarks>
    internal static string Reported(
        int asked,
        int told,
        int repeated,
        bool stopped,
        IReadOnlyDictionary<Wire.WakeOutcome, int> counted,
        IReadOnlyList<string> unreachable,
        IReadOnlyList<TurnedAway> turnedAway)
    {
        ArgumentNullException.ThrowIfNull(counted);
        ArgumentNullException.ThrowIfNull(unreachable);
        ArgumentNullException.ThrowIfNull(turnedAway);

        var said = new StringBuilder()
            .Append(told == asked
                ? $"Told all {asked}."
                : $"Told {told} of {asked}.");

        // Said before the outcomes, because it changes what they mean: a broadcast where every
        // recipient was a repeat stored nothing at all, and the counts below then describe agents
        // that were woken for mail they already had.
        if (repeated > 0)
        {
            _ = said.Append(
                repeated == asked
                    ? " Every one was the same notice again, so nothing was repeated."
                    : $" {repeated} had already been sent this, so it was not repeated.");
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

        // Named rather than counted, because this is the half somebody does something about — and
        // it is not a failure: the message is stored and each of these collects it whenever that
        // agent next runs.
        if (unreachable.Count > 0)
        {
            // Abbreviated for the reason the refusals are: nineteen names on one line is a line
            // that runs off the screen, and this is the half that is not even a failure.
            _ = said.Append(" Waiting for ")
                .Append(unreachable.Count)
                .Append(" — nothing here can start them (")
                .Append(Some(unreachable))
                .Append(").");
        }

        // **Grouped by why, the names abbreviated, and the groups themselves bounded.** "Refused
        // for" followed by nineteen names is a line that runs off the screen and says nothing a
        // person can act on — reported by the owner, whose whole roster was refused for one cause
        // and whose message was cut off before the end of the list. The reason is the actionable
        // half, so it comes first and is said once per cause.
        //
        // **The bound on the groups was found by the gate, and it is the owner's own defect coming
        // back through the door the grouping opened.** Grouping is only short when the reasons
        // repeat, and a broker whose status detail names the recipient — the ordinary shape of a
        // gRPC status — gives one group each: nineteen recipients produced a thousand-character
        // line again.
        var groups = turnedAway
            .GroupBy(refusal => Reason(refusal.Because), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToList();

        // **The count first, the reason next, the names last, and that ordering is the report the
        // owner actually saw.** A terminal shows this in one row of whatever width it has, so
        // whatever comes first is the part that survives — and what they got was
        // *"Told 0 of 19. Refused for"* and then a list of names that ran off the end, with the
        // one sentence they could have acted on never reaching the screen at all.
        foreach (var group in groups.Take(Causes))
        {
            _ = said.Append(" Refused for ")
                .Append(group.Count())
                .Append(": ")
                .Append(group.Key)
                .Append(" (")
                .Append(Some([.. group.Select(refusal => refusal.Agent)]))
                .Append(')');
        }

        if (groups.Count > Causes)
        {
            _ = said.Append($" {groups.Skip(Causes).Sum(group => group.Count())} more were refused, "
                + "each for a different reason.");
        }

        if (stopped)
        {
            _ = said.Append(' ').Append(Left);
        }

        return said.ToString();
    }

    /// <summary>Why the broker would not take a notification, as a sentence.</summary>
    /// <param name="rejection">What it said.</param>
    /// <returns>The sentence, ending in a full stop.</returns>
    /// <remarks>
    /// The same four answers <see cref="NotifyAsync"/> gives for one recipient, worded for a group
    /// rather than for a name. One source for both would be better still and is not reachable: the
    /// single-recipient wording names the agent inside the sentence, which is what makes it read
    /// well there and useless here.
    /// </remarks>
    internal static string Because(Wire.NotifyRejection rejection) => rejection switch
    {
        // **The one a fresh deployment meets, and the one worth being specific about.** §14 counts
        // a name nothing has registered as unroutable, and every message type is refused for one —
        // so this is not a quiet agent but an unaddressable one, and the remedy is a start rather
        // than a retry.
        Wire.NotifyRejection.RecipientUnroutable =>
            "nothing has registered under that name yet, so nothing can be addressed to it. "
            + "Starting it once is what registers it.",

        Wire.NotifyRejection.SenderRetired =>
            "your seat has been retired, so nothing was sent. Take a seat again to send it.",

        Wire.NotifyRejection.ConversationClosed =>
            "that conversation is closed, so nothing was added to it.",

        _ => "the broker refused it and did not say why.",
    };

    /// <summary>How many names to print before counting the rest.</summary>
    /// <remarks>
    /// Four fits a status line beside the sentence that carries them. The number matters less than
    /// there being one: the report this replaces printed every name it had, and the owner's screen
    /// cut the line off part way through the list.
    /// </remarks>
    private const int Named = 4;

    /// <summary>How many distinct reasons to spell out before counting the rest.</summary>
    /// <remarks>
    /// Two, because the realistic mixed broadcast has one dominant cause and one other — and
    /// because the point of the bound is that a status row shows one line. Everything past it is
    /// counted, so nothing is hidden: a person who needs the detail has <c>parley notifications</c>
    /// and the broker's own log.
    /// </remarks>
    private const int Causes = 2;

    /// <summary>A refusal's reason, or a sentence saying there was not one.</summary>
    /// <param name="because">What the broker said.</param>
    /// <returns>Something to print.</returns>
    /// <remarks>
    /// A gRPC status may carry an empty detail, which printed as <c>Refused for a@b:</c> and then
    /// nothing — a dangling colon reads as the report having been cut off, which is exactly the
    /// thing this whole change is about not doing.
    /// </remarks>
    private static string Reason(string because) =>
        Flat(because) is { Length: > 0 } said ? said : Because(Wire.NotifyRejection.Unspecified);

    /// <summary>Some of a list of names, and a count of whatever is left.</summary>
    /// <param name="names">The names.</param>
    /// <returns>The phrase.</returns>
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
    /// <para>
    /// <b>Found by the review gate, and it is 0.18.6's forgery in a new place.</b> A refusal on the
    /// failure path carries <c>Status.Detail</c> — whatever came back from the far end of a gRPC
    /// call — and this report is contracted to be one line, which the surfaces rely on: a terminal
    /// shows it in a status row. A newline in that detail produced a second line reading
    /// <c>Told all 2.</c>, which is this report's own success sentence, written by somebody else.
    /// </para>
    /// <para>
    /// <b>Applied to the names as well as to the reasons, and that is deliberate rather than
    /// thorough.</b> An agent name cannot carry a line break today, so flattening it changes
    /// nothing — and <c>parley instructions</c> is on record here for flattening the reply and not
    /// the operator's own words, where one rule applied to half the output was worse than either
    /// applied whole.
    /// </para>
    /// </remarks>
    private static string Flat(string text) => text.ReplaceLineEndings(" ");

    /// <summary>What is said when a project holds nobody to tell.</summary>
    internal const string NobodyToTell =
        "There is nobody in this project to tell, so nothing was sent.";

    /// <summary>What is said when the seat went part way through a broadcast.</summary>
    /// <remarks>
    /// It names what did go rather than only what did not. A person told merely that their seat
    /// was released cannot tell whether to send the whole thing again or the remainder.
    /// </remarks>
    internal const string Left =
        "Your seat was released part way through, so the rest were not sent.";

    /// <summary>What became of the recipient, in words.</summary>
    /// <param name="wake">What the broker did about it.</param>
    /// <param name="to">Who was told.</param>
    /// <returns>A sentence to show beside the send.</returns>
    /// <remarks>
    /// <para>
    /// <b>"Sent." on its own is what produced the report this exists to answer.</b> It was true —
    /// the message was stored — and it left somebody believing a peer was reading a notification
    /// that had reached a closed session and started nothing. A send that delivers and a send that
    /// lands in an empty room looked identical.
    /// </para>
    /// <para>
    /// <b>None of these is a failure and none of them is worded as one.</b> The message is stored
    /// before any of it is decided, and SPEC.md §17 question 1 requires that whatever bounds this
    /// "must not make an event undeliverable" — so <c>Unreachable</c> says the notice is waiting
    /// rather than that it was not sent.
    /// </para>
    /// </remarks>
    internal static string Waking(Wire.WakeOutcome wake, AgentName to) => wake switch
    {
        Wire.WakeOutcome.Started => $"{to.Value} was not running, so it is being started.",
        Wire.WakeOutcome.AlreadyRunning => $"{to.Value} is running and has been handed it.",

        // Said rather than passed over in silence, because "nothing happened" and "something is
        // already happening" look the same from here and lead somewhere completely different.
        Wire.WakeOutcome.AlreadyComing =>
            $"{to.Value} is already being seen to, and will collect this with the rest.",

        Wire.WakeOutcome.Unreachable =>
            $"It is waiting for {to.Value}, but nothing here can start it — it will be collected "
            + "when that agent next runs.",

        // A broker older than this one, which cannot happen while the protocol version is checked
        // on attach. Said plainly rather than guessed at: claiming a start that may not have been
        // asked for is the failure this whole sentence exists to remove.
        _ => $"What became of {to.Value} was not reported.",
    };

    /// <summary>How much is waiting, without taking any of it.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Messages waiting, and closure polls waiting.</returns>
    /// <remarks>
    /// The read that <see cref="DrainAsync"/> is not. It marks nothing delivered and starts no
    /// working window, which is what makes it safe to call while rendering a page — and it is
    /// why the inbox can say how much is there before anybody commits to receiving it.
    /// </remarks>
    public async Task<(int Messages, int ClosurePolls)> WaitingAsync(
        CancellationToken cancellationToken = default)
    {
        var notice = await _agents.WaitingAsync(
            new Wire.WaitingRequest { Agent = _seat.Name.Value },
            cancellationToken: cancellationToken);

        return (notice.MessagesWaiting, notice.ClosurePollsWaiting);
    }

    /// <summary>Takes everything waiting for this person, which is what marks it delivered.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was handed over, and how many closure polls came with it.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is not a read.</b> SPEC.md §6 sets the working window running from the question
    /// being delivered, and says in as many words that an inbox drain is what records delivery —
    /// so calling this starts a four-hour clock on everything it returns, and after that §14
    /// ghosts what is unanswered. It is offered as something a person does on purpose, never as
    /// part of rendering a page.
    /// </para>
    /// <para>
    /// That is the protocol working rather than a wrinkle to route around. A question cannot be
    /// read without being owed, because a peer that has had its question read and is being
    /// ignored is in a different position from one whose question nobody ever picked up, and §6
    /// splits its two windows on exactly that difference.
    /// </para>
    /// </remarks>
    public async Task<(IReadOnlyList<Delivered> Messages, int ClosurePolls)> DrainAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _agents.DrainInboxAsync(
            new Wire.DrainInboxRequest { Agent = _seat.Name.Value, Limit = 0 },
            cancellationToken: cancellationToken);

        var taken = reply.Messages
            .Select(one => new Delivered(Read(one.Message)))
            .ToList();

        return (taken, reply.ClosurePolls.Count);
    }

    /// <summary>Drains the inbox and records what came back against the seat, as one act.</summary>
    /// <param name="hold">The seat to record against.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What was taken, and how many closure polls came with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in each surface, and that is the whole point of it existing.</b> The
    /// console drained and then recorded in two statements of its own, which worked while there
    /// was one surface. A second surface doing the same would be a second copy of the rule that
    /// a drain is followed by a record — and the day one of them grew a step the other did not,
    /// the two would disagree about what a person had taken delivery of, which is the class of
    /// failure SC-003 names and which this repository has shipped twice.
    /// </para>
    /// <para>
    /// It also closes something the extraction opened. <see cref="SeatHold.Record"/> is internal
    /// because only the code driving a seat should call it; when the seat moved to this assembly
    /// the console kept reaching it through a grant, and a second surface would have needed the
    /// same grant. With the drive beside the state it changes, neither does.
    /// </para>
    /// </remarks>
    public async Task<(IReadOnlyList<Delivered> Messages, int ClosurePolls)> TakeDeliveryAsync(
        SeatHold hold, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        var (messages, polls) = await DrainAsync(cancellationToken).ConfigureAwait(false);

        hold.Record(messages);

        return (messages, polls);
    }

    /// <summary>Answers an exchange and settles it against the seat when it closed.</summary>
    /// <param name="hold">The seat holding the delivered question.</param>
    /// <param name="exchangeId">The exchange being answered.</param>
    /// <param name="resolutions">What is said about each sub-question.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened, and whether the exchange closed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hold"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Settled only when the broker says the exchange closed. A surface that struck the question
    /// off its own list on sending would show a person nothing owed while the broker still
    /// believed otherwise — and the broker is the one whose opinion the working window of §6 is
    /// measured against.
    /// </remarks>
    public async Task<Answered> AnswerAsync(
        SeatHold hold,
        long exchangeId,
        IReadOnlyList<(string Id, string Text, string Outcome)> resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        var answered = await AnswerAsync(exchangeId, resolutions, cancellationToken)
            .ConfigureAwait(false);

        if (answered.Closed)
        {
            hold.Settled(exchangeId);
        }

        return answered;
    }

    /// <summary>The sub-questions an exchange asked, which an answer must resolve one for one.</summary>
    /// <param name="exchangeId">The exchange.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identifiers, in the order they were asked.</returns>
    /// <remarks>
    /// Asked of the broker rather than parsed out of the delivered payload. §5.3 answers by
    /// identifier and the store holds what was actually asked; a form built from a document this
    /// process parsed would be a second opinion about it, and the one that disagrees is the one
    /// that produces an answer resolving a sub-question nobody asked.
    /// </remarks>
    public async Task<IReadOnlyList<string>> SubQuestionsAsync(
        long exchangeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _agents.GetSubQuestionIdsAsync(
                new Wire.SubQuestionsRequest
                {
                    ExchangeId = exchangeId,
                    AskingAs = _seat.Name.Value
                },
                cancellationToken: cancellationToken);

            return [.. reply.Ids];
        }
        catch (RpcException)
        {
            return [];
        }
    }

    /// <summary>Answers a question this person owes.</summary>
    /// <param name="exchangeId">The exchange being closed.</param>
    /// <param name="resolutions">One entry per sub-question: its id, the answer, and the outcome.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A sentence saying what happened.</returns>
    /// <remarks>
    /// The aggregate is rolled up here rather than asked for, and takes the least resolved of
    /// the parts exactly as SPEC.md §5.3 requires. An answer that settled four sub-questions and
    /// left one unknown has not settled the question, and a person choosing the aggregate
    /// separately could say it had — which would make §15's telemetry describe a deployment that
    /// does not exist.
    /// </remarks>
    public async Task<Answered> AnswerAsync(
        long exchangeId,
        IReadOnlyList<(string Id, string Text, string Outcome)> resolutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);

        if (resolutions.Count == 0)
        {
            return new Answered(false, "That question asked nothing this could resolve.");
        }

        if (resolutions.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Text)) is { Id: { } blank })
        {
            return new Answered(
                false,
                $"Write something for {blank}. An empty resolution closes the exchange while "
                + "telling the asker nothing, and they cannot ask it again — §7 refuses a "
                + "repeat of a question already asked.");
        }

        // §5.3 answers by identifier, one for one with what was asked. The form is built from
        // the broker's own list so an honest post already matches; this refuses one that does
        // not, because an answer resolving a sub-question nobody asked is stored and closes the
        // exchange, leaving the asker's actual question unanswered and the exchange recording
        // that it was answered.
        var asked = await SubQuestionsAsync(exchangeId, cancellationToken).ConfigureAwait(false);

        if (asked.Count == 0)
        {
            return new Answered(
                false,
                "That exchange is no longer yours to answer — it has been answered, orphaned or "
                + "closed since it was handed to you.");
        }

        if (!asked.ToHashSet(StringComparer.Ordinal)
                .SetEquals(resolutions.Select(r => r.Id)))
        {
            return new Answered(
                false,
                $"That question asked {string.Join(", ", asked)}, and this answered something "
                + "else. §5.3 wants one resolution per sub-question asked.");
        }

        var payload = new JsonObject
        {
            ["resolutions"] = new JsonArray([.. resolutions.Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Id,
                ["text"] = r.Text,
                ["resolution"] = r.Outcome
            })])
        }.ToJsonString();

        if (TooLong(payload) is { } refused)
        {
            await RecordAsync(Wire.MessageType.Answer, payload.Length, cancellationToken)
                .ConfigureAwait(false);

            return new Answered(false, refused);
        }

        try
        {
            var reply = await _agents.AnswerAsync(
                new Wire.AnswerRequest
                {
                    From = _seat.Name.Value,
                    ExchangeId = exchangeId,
                    ResponderModel = _seat.Responder,
                    PayloadJson = payload,
                    Resolution = RollUp(resolutions),
                    IsDecision = false
                },
                cancellationToken: cancellationToken);

            return reply.Ok
                ? new Answered(true, "Answered, and the exchange is closed.")
                : new Answered(
                    false,
                    "That answer was refused: the exchange is not open, or is not yours to "
                    + "answer.");
        }
        catch (RpcException failure)
        {
            return new Answered(false, Failed(failure));
        }
    }

    /// <summary>The outcomes a person may give a sub-question.</summary>
    /// <remarks>
    /// Three, and <c>ghosted</c> is deliberately not among them: SPEC.md §5.3 reserves it for
    /// the broker recording an exchange nobody returned to, and an agent that could write it
    /// over its own unanswered work would be manufacturing the evidence §15 counts to find an
    /// unhealthy deployment.
    /// </remarks>
    public static IReadOnlyList<(string Value, string Says)> Outcomes { get; } =
    [
        ("answered", "I have answered this"),
        ("unknown", "I cannot determine this"),
        ("out_of_scope", "This belongs to somebody else")
    ];

    /// <summary>Takes the least resolved of the parts, per SPEC.md §5.3.</summary>
    /// <param name="resolutions">What was said about each sub-question.</param>
    /// <returns>The aggregate to stamp on the exchange.</returns>
    private static Wire.Resolution RollUp(
        IReadOnlyList<(string Id, string Text, string Outcome)> resolutions)
    {
        if (resolutions.Any(r => string.Equals(r.Outcome, "unknown", StringComparison.Ordinal)))
        {
            return Wire.Resolution.Unknown;
        }

        return resolutions.Any(
            r => string.Equals(r.Outcome, "out_of_scope", StringComparison.Ordinal))
            ? Wire.Resolution.OutOfScope
            : Wire.Resolution.Answered;
    }

    /// <summary>Maps one message off the wire.</summary>
    /// <param name="wire">The message as it arrived.</param>
    /// <returns>The message in the domain's terms.</returns>
    private static Message Read(Wire.Message wire) => new(
        wire.Id,
        wire.ExchangeId == 0 ? null : wire.ExchangeId,
        wire.Type switch
        {
            Wire.MessageType.Question => MessageType.Question,
            Wire.MessageType.Clarify => MessageType.Clarify,
            Wire.MessageType.Answer => MessageType.Answer,
            Wire.MessageType.Ghost => MessageType.Ghost,
            _ => MessageType.Notify
        },
        AgentName.Create(wire.From),
        AgentName.Create(wire.To),
        wire.ResponderModel,
        wire.PayloadJson,
        string.IsNullOrEmpty(wire.ConversationId) ? null : wire.ConversationId,
        DateTimeOffset.Parse(wire.CreatedUtc, CultureInfo.InvariantCulture),
        string.IsNullOrEmpty(wire.DeliveredUtc)
            ? null
            : DateTimeOffset.Parse(wire.DeliveredUtc, CultureInfo.InvariantCulture));

    /// <summary>Refuses a conversation name the store will refuse, before it is sent.</summary>
    /// <param name="conversationId">The name carried by the route.</param>
    /// <returns>A refusal, or null when the name is usable.</returns>
    /// <remarks>
    /// <para>
    /// The invariant is in the schema, where §9.3 argues it belongs, so nothing malformed can be
    /// stored whatever this does — a name reaches a filename when a discussion is exported, and
    /// the <c>CHECK</c> refuses path syntax and a run of dots. This is not that guard and does
    /// not replace it.
    /// </para>
    /// <para>
    /// It exists because of what the person sees without it. The constraint arrives as
    /// <c>Exception was thrown by handler</c>, which says nothing about what was wrong and does
    /// not distinguish a name they mistyped in the address bar from a broker that is broken.
    /// This is the same reason the adapter checks §8's framing rather than letting a
    /// <c>json_extract</c> constraint answer a model.
    /// </para>
    /// </remarks>
    private static string? BadConversation(string? conversationId) =>
        conversationId is { Length: > 0 and <= 64 }
        && !conversationId.Contains("..", StringComparison.Ordinal)
        && conversationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? null
            : $"'{conversationId}' cannot name a conversation. Up to 64 characters of letters, "
              + "digits, '.', '_' and '-', with no '..' — the name reaches a filename when a "
              + "discussion is exported.";

    /// <summary>Refuses a payload over the cap, in the same words the adapter uses.</summary>
    /// <param name="payload">The assembled document.</param>
    /// <returns>The refusal, or null when it fits.</returns>
    /// <remarks>
    /// Measured on the whole document rather than on what was typed, because the cap is on what
    /// reaches a peer's context — which is what the framing is part of. See the standing
    /// constraint: terseness is a context-poisoning defence, not a style preference.
    /// </remarks>
    private static string? TooLong(string payload) =>
        payload.Length <= MessageLimits.MaxPayloadCharacters
            ? null
            : $"That is {payload.Length} characters and the limit is "
              + $"{MessageLimits.MaxPayloadCharacters}. The cap is a defence rather than a "
              + "formatting rule: say the short version, and link to the long one.";

    /// <summary>Records that a message was refused for its size, so the cap has evidence.</summary>
    /// <param name="type">Which kind of message it would have been.</param>
    /// <param name="characters">How long it was. Never the text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task completing when it has been recorded, or given up on.</returns>
    /// <remarks>
    /// SPEC.md §17 question 1 leaves the cap provisional and says it moves on evidence rather
    /// than on taste. <c>parley refusals</c> is that evidence, and a refusal the console swallowed
    /// is a data point missing from the one place the number is ever argued from. Failures are
    /// ignored: the person has already been told, and a broker that cannot record a refusal must
    /// not turn it into a second error they can do nothing about.
    /// </remarks>
    private async Task RecordAsync(
        Wire.MessageType type, int characters, CancellationToken cancellationToken)
    {
        try
        {
            await _agents.RecordRefusalAsync(
                new Wire.RecordRefusalRequest
                {
                    Agent = _seat.Name.Value,
                    Type = type,
                    Characters = characters
                },
                cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // See the remarks.
        }
    }

    /// <summary>Puts a refused question into words.</summary>
    /// <param name="rejection">Why the broker refused it.</param>
    /// <param name="to">Who was being asked.</param>
    /// <returns>A sentence, and where there is one, the remedy.</returns>
    private static string Refusal(Wire.OpenRejection rejection, AgentName to) => rejection switch
    {
        Wire.OpenRejection.PairSaturated =>
            $"You already have as many questions open with {to.Value} as §7 allows. Conclude one "
            + "before opening another.",
        Wire.OpenRejection.DuplicateQuestion =>
            "You have already asked exactly this, and it is still open.",
        Wire.OpenRejection.WouldDeadlock =>
            $"Asking that would close a cycle: {to.Value} is already waiting on you, directly or "
            + "through somebody else. §6 refuses it rather than letting both sides wait.",
        Wire.OpenRejection.AnswererUnreachable =>
            $"{to.Value} is not registered here, or is retired.",
        Wire.OpenRejection.ConversationClosed =>
            "That conversation is closed, so nothing can be added to it.",
        Wire.OpenRejection.AskerRetired =>
            "Your seat has been retired. An operator reinstates it.",
        _ => "The broker refused it and did not say why."
    };

    /// <summary>Puts a transport failure into words.</summary>
    /// <param name="failure">What the call threw.</param>
    /// <returns>A sentence.</returns>
    private static string Failed(RpcException failure) =>
        failure.StatusCode == StatusCode.Unavailable
            ? "This project's broker is not answering, so nothing was sent."
            : $"The broker refused it: {failure.Status.Detail}";
}
