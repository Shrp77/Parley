using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>What one round of asking which questions are still open came to.</summary>
/// <param name="Asks">Every ask of the round: sent, kept from an earlier round still waiting on its reply, or refused.</param>
/// <param name="Asked">How many Requests went.</param>
/// <param name="About">How many questions they listed.</param>
/// <param name="Already">How many agents were not asked again, because an earlier ask is still waiting on its reply.</param>
/// <param name="Problems">Each Request that did not go, with why.</param>
public sealed record StillOpenRound(
    IReadOnlyList<StillOpenAsk> Asks, int Asked, int About, int Already, IReadOnlyList<string> Problems);

/// <summary>The Request a person sends an agent to ask which of its questions to them are still open.</summary>
/// <remarks>
/// <para>
/// <b>An ordinary Request, and no new protocol</b> (SPEC.md §5.6). The agent, never the broker or a surface, decides a
/// question is answered: it names the settled ones in <c>settles</c> on its reply, and the broker closes those. The
/// surface only lists what is open; it infers nothing from what anybody said.
/// </para>
/// <para>
/// <b>One a conversation for each agent</b>, because a reply can settle only exchanges in its own conversation. The
/// words are fixed and shown to the person before they send them, since they go out under the person's name.
/// </para>
/// </remarks>
public static class StillOpen
{
    /// <summary>What the Request says before it lists the questions.</summary>
    public const string Opening =
        "Are these questions still waiting on me? I may have answered them somewhere else, or they may no longer " +
        "matter. Name each one that is settled in settles and say so. For each one still open, say what you still need " +
        "from me.";

    /// <summary>How much of each question is quoted back, so the agent can tell which one is meant.</summary>
    public const int Quoted = 200;

    /// <summary>Writes the Request for one agent's questions in one conversation.</summary>
    /// <param name="questions">Each open exchange and what the agent last said on it.</param>
    /// <returns>The words of the Request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="questions"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The exchange is named because <c>settles</c> takes exchanges; the quote is so the agent knows what each was without
    /// reading back. It is flattened to one line so a question cannot break the list it sits in.
    /// </remarks>
    public static string Ask(IEnumerable<(long Exchange, string Said)> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        var lines = questions.Select(question => $"- Exchange {question.Exchange}: {Quote(question.Said)}");

        return Opening + "\n\n" + string.Join('\n', lines);
    }

    /// <summary>Asks each agent in one project which of its questions to the person are still open.</summary>
    /// <param name="broker">The project's broker.</param>
    /// <param name="voice">The person's seat in it, which the Requests go out under.</param>
    /// <param name="project">The project's name.</param>
    /// <param name="earlier">What this surface asked before, so an agent still to reply is not asked twice.</param>
    /// <param name="cancellationToken">Cancels the reads and sends.</param>
    /// <returns>Every ask of the round, sent, kept from before, or refused, and how many of each.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>One implementation for every surface</b> (owner, 2026-11-06), so the console and the terminal ask the same agents
    /// the same thing. What is asked about is read from the broker here, never from what a surface last drew, so a screen
    /// left open cannot ask about a question already closed. Parley's own questions are left out: the facilitator receives
    /// nobody's work.
    /// </para>
    /// <para>
    /// <b>A second press does not ask twice.</b> An agent is asked again only once it has replied, or when a question has
    /// arrived since that its earlier ask did not list; then the new ask lists them all.
    /// </para>
    /// </remarks>
    public static async Task<StillOpenRound> AskAsync(
        BrokerClient broker,
        SeatVoice voice,
        string project,
        IReadOnlyList<StillOpenAsk> earlier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(earlier);

        var me = voice.Name;
        var owed = await WaitingOnThePerson.ReadAsync(broker, me, cancellationToken).ConfigureAwait(false);
        var openNow = (await broker.WaitingAsync(cancellationToken).ConfigureAwait(false))
            .Select(edge => edge.ExchangeId)
            .ToHashSet();

        var asks = new List<StillOpenAsk>();
        var problems = new List<string>();
        var (asked, about, already) = (0, 0, 0);

        var groups = owed
            .Where(item => item.Message.ConversationId is { Length: > 0 } && !Facilitation.Is(item.Message.From))
            .GroupBy(item => (item.Message.From, Conversation: item.Message.ConversationId!));

        foreach (var group in groups)
        {
            var questions = group.Select(item => item.Message.ExchangeId).ToList();

            if (Pending(earlier, project, group.Key.From, group.Key.Conversation, questions, openNow) is { } waiting)
            {
                asks.Add(waiting);
                already++;
                continue;
            }

            var ask = Ask(group.Select(item => (item.Message.ExchangeId, PayloadShape.Spoken(item.Message.PayloadJson))));
            var opened = await voice
                .RequestOpeningAsync(group.Key.From, ask, group.Key.Conversation, cancellationToken)
                .ConfigureAwait(false);

            asks.Add(new StillOpenAsk(
                project, group.Key.From, group.Key.Conversation, questions, opened.Exchange, opened.Reached ?? opened.Says));

            if (opened.Sent)
            {
                asked++;
                about += questions.Count;
            }
            else
            {
                problems.Add($"{group.Key.From.Value} in {group.Key.Conversation}: {opened.Says}");
            }
        }

        return new StillOpenRound(asks, asked, about, already, problems);
    }

    /// <summary>An earlier ask that still covers these questions and is still waiting on its reply, if there is one.</summary>
    /// <param name="earlier">What was asked last.</param>
    /// <param name="project">The project.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="conversation">The conversation.</param>
    /// <param name="questions">The questions open now.</param>
    /// <param name="openNow">Every exchange open in the project now.</param>
    /// <returns>The earlier ask, or <see langword="null"/> to ask again.</returns>
    internal static StillOpenAsk? Pending(
        IReadOnlyList<StillOpenAsk> earlier,
        string project,
        AgentName agent,
        string conversation,
        IReadOnlyCollection<long> questions,
        IReadOnlySet<long> openNow) =>
        earlier.FirstOrDefault(ask =>
            ask.Exchange is { } exchange
            && openNow.Contains(exchange)
            && ask.Project == project
            && ask.Agent == agent
            && ask.Conversation == conversation
            && questions.All(ask.Questions.Contains));

    /// <summary>What a surface says after asking.</summary>
    /// <param name="round">The round, or several summed.</param>
    /// <returns>The sentence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="round"/> is <see langword="null"/>.</exception>
    public static string Says(StillOpenRound round)
    {
        ArgumentNullException.ThrowIfNull(round);

        var sent = round.Asked == 0
            ? "Nobody was asked."
            : $"Asked about {round.About} {(round.About == 1 ? "question" : "questions")} in {round.Asked} " +
              $"{(round.Asked == 1 ? "request" : "requests")}. Each agent closes the ones already answered when it replies.";

        if (round.Already > 0)
        {
            sent += round.Already == 1
                ? " One agent was already asked and has not replied yet, so it was not asked again."
                : $" {round.Already} agents were already asked and have not replied yet, so they were not asked again.";
        }

        return round.Problems.Count == 0 ? sent : sent + " Not sent: " + string.Join(" ", round.Problems);
    }

    /// <summary>Where each ask stands now, read from its conversation.</summary>
    /// <param name="asks">What was asked.</param>
    /// <param name="brokerFor">The broker of a project by name, or <see langword="null"/> when it is no longer known.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>One standing for each ask, in the order asked.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>One read per conversation, all at once.</remarks>
    public static async Task<IReadOnlyList<StillOpenStanding>> StandingAsync(
        IReadOnlyList<StillOpenAsk> asks,
        Func<string, BrokerClient?> brokerFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asks);
        ArgumentNullException.ThrowIfNull(brokerFor);

        if (asks.Count == 0)
        {
            return [];
        }

        var reads = asks
            .Where(ask => ask.Exchange is not null)
            .Select(ask => (ask.Project, ask.Conversation))
            .Distinct()
            .ToDictionary(
                key => key,
                key => brokerFor(key.Project) is { } broker
                    ? BrokerDirectory.ReadAsync(async () =>
                        await broker.ExportAsync(key.Conversation, cancellationToken).ConfigureAwait(false))
                    : Task.FromResult(ProjectRead<ConversationExport>.Unreachable($"{key.Project} is no longer on this machine.")));

        await Task.WhenAll(reads.Values).ConfigureAwait(false);

        var rows = new List<StillOpenStanding>();

        foreach (var ask in asks)
        {
            if (ask.Exchange is not { } exchange)
            {
                rows.Add(new StillOpenStanding(ask, 0, "Not sent", ask.Said));
                continue;
            }

            var read = await reads[(ask.Project, ask.Conversation)].ConfigureAwait(false);

            rows.Add(read.Value is { } export
                ? Standing(ask, exchange, export)
                : new StillOpenStanding(ask, 0, "Could not be read", read.Problem ?? string.Empty));
        }

        return rows;
    }

    /// <summary>Where one ask stands, from its conversation.</summary>
    /// <param name="ask">What was asked.</param>
    /// <param name="exchange">The exchange the Request opened.</param>
    /// <param name="export">Its conversation as it stands.</param>
    /// <returns>The standing.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The Request's exchange says whether the agent has replied, and each question's says whether it is still open,
    /// whoever closed it.
    /// </remarks>
    public static StillOpenStanding Standing(StillOpenAsk ask, long exchange, ConversationExport export)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(export);

        var open = export.Exchanges
            .Where(snapshot => snapshot.State == ExchangeState.Open)
            .Select(snapshot => snapshot.ExchangeId)
            .ToHashSet();

        var closed = ask.Questions.Count(question => !open.Contains(question));
        var stillOpen = ask.Questions.Count - closed;

        if (!open.Contains(exchange))
        {
            return new StillOpenStanding(ask, closed, "Replied",
                stillOpen == 0 ? "Every question is closed." : $"{stillOpen} still open.");
        }

        var last = export.Messages.LastOrDefault(message => message.ExchangeId == exchange);

        return last is { Type: MessageType.Clarify } && last.From == ask.Agent
            ? new StillOpenStanding(ask, closed, "Asked you something back", "Answer it where it was asked.")
            : new StillOpenStanding(ask, closed, "Waiting for its reply", ask.Said);
    }

    private static string Quote(string said)
    {
        var text = string.Join(' ', (said ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length > Quoted ? text[..Quoted] + "…" : text;
    }
}
