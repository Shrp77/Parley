using System.Globalization;
using System.Text.Json.Nodes;
using Grpc.Core;
using Parley.Core;
using Wire = Parley.Transport;

namespace Parley.Client.Seating;

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
                : "Sent.";
        }
        catch (RpcException failure)
        {
            return Failed(failure);
        }
    }

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
