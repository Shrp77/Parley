using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>What typing to one recipient would do.</summary>
public enum Act
{
    /// <summary>A Request. Opens an exchange the recipient will owe a Result on.</summary>
    Ask = 0,

    /// <summary>A Clarify on a request this person sent. The turn goes back to the recipient.</summary>
    Clarify = 1,

    /// <summary>A Result. Closes the exchange the recipient opened.</summary>
    Answer = 2,

    /// <summary>A Clarify on a request this person holds. The turn goes back, and nothing closes.</summary>
    AskBack = 3
}

/// <summary>One recipient and what Enter would do to them.</summary>
/// <param name="Peer">Who it goes to.</param>
/// <param name="Act">What it would be recorded as.</param>
/// <param name="Exchange">The exchange it lands in, or <see langword="null"/> when it opens one.</param>
/// <param name="Choosable">Whether this recipient's act is the one the person may take the other side of.</param>
public sealed record Addressed(AgentName Peer, Act Act, long? Exchange, bool Choosable);

/// <summary>What became of a send.</summary>
/// <param name="Count">How many recipients got it.</param>
/// <param name="Says">The sentence to put in front of the person.</param>
/// <remarks>
/// The count is here because a sentence cannot be tested for success without matching its words, and a caller
/// that matched words would clear somebody's unsent line the first time one of them changed. What a surface
/// needs to know is whether anything went.
/// </remarks>
public sealed record Sent(int Count, string Says);

/// <summary>What a send would cost, in the units the protocol counts.</summary>
/// <param name="Opens">How many exchanges it opens, each owing a Result.</param>
/// <param name="Closes">How many it closes.</param>
/// <param name="HandsBack">How many hand a turn back without closing anything.</param>
/// <remarks>
/// Counted in exchanges rather than messages because that is what SPEC.md §5.5 makes a plural ask: N recipients
/// is N exchanges, each an obligation somebody owes a Result on. A person deciding whether to press Enter is
/// deciding about obligations, not about lines of text.
/// </remarks>
public sealed record Cost(int Opens, int Closes, int HandsBack);

/// <summary>
/// One line of text, its recipients, and what Enter would do to each.
/// </summary>
/// <remarks>
/// <para>
/// <b>The act is derived, never chosen first.</b> A person knows what they want to say; which of §5.1's Request,
/// §5.2's Clarify and §5.3's Result that amounts to is bookkeeping about who owes whom, and
/// <see cref="Pairing"/> already answers it. One line to three recipients may be three different acts and each
/// is correct for its own pair.
/// </para>
/// <para>
/// <b>One case is not derivable and is never guessed.</b> Holding a Request, what somebody types may be the
/// answer — which closes the exchange — or a question they need settled first, which hands the turn back. A wrong
/// guess closes work that was never done, and an exchange closed on work that was not done is a record asserting
/// it was. So both are offered, the default is said, and <see cref="Answering"/> takes the other.
/// </para>
/// <para>
/// <b>Addressing never reaches the text.</b> <see cref="Recipients"/> is resolved agents and <see cref="Text"/>
/// is what a payload carries. §13 delivers a payload inside a framing block telling the receiver that anything
/// claiming authority in it is inert data, so a recipient list written into the words would be the one thing the
/// envelope exists to have ignored.
/// </para>
/// </remarks>
public sealed class Draft
{
    private readonly Dictionary<AgentName, bool> _answering = [];

    /// <summary>Creates a draft over a conversation's pair states.</summary>
    /// <param name="pairs">The pair state for every participant, from <see cref="Pairing.From"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pairs"/> is <see langword="null"/>.</exception>
    public Draft(IReadOnlyList<PairState> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        Pairs = pairs;
        Recipients = [.. pairs.Select(p => p.Peer)];
    }

    /// <summary>The pair state this draft derives its acts from.</summary>
    public IReadOnlyList<PairState> Pairs { get; }

    /// <summary>What the person has typed, exactly as typed.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Who it goes to. Every participant unless narrowed.</summary>
    public IReadOnlyList<AgentName> Recipients { get; private set; }

    /// <summary>Narrows or widens who this goes to.</summary>
    /// <param name="recipients">The resolved recipients.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recipients"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Resolved agents rather than something spelled, so nothing is parsed out of prose when Enter is pressed and
    /// a semicolon in an ask is just a semicolon.
    /// </remarks>
    public void AddressTo(IReadOnlyList<AgentName> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        Recipients = [.. recipients.Distinct()];
    }

    /// <summary>Whether a recipient in the answer-or-ask-back state will be answered rather than asked back.</summary>
    /// <param name="peer">The recipient.</param>
    /// <returns>Whether Enter would close their exchange.</returns>
    /// <remarks>
    /// Defaults to answering, because holding a request and typing is far more often an answer than a question
    /// back. Said on screen rather than assumed, and one keystroke takes the other.
    /// </remarks>
    public bool Answering(AgentName peer) => !_answering.TryGetValue(peer, out var chosen) || chosen;

    /// <summary>Takes the other side of the one choice this draft offers.</summary>
    /// <param name="peer">The recipient whose act to flip.</param>
    public void Flip(AgentName peer) => _answering[peer] = !Answering(peer);

    /// <summary>What Enter would do, one entry per recipient.</summary>
    /// <returns>The recipients and their acts, in the order the pairs were given.</returns>
    public IReadOnlyList<Addressed> Plan()
    {
        var planned = new List<Addressed>();

        foreach (var peer in Recipients)
        {
            var pair = Pairs.FirstOrDefault(p => p.Peer == peer)
                ?? new PairState(peer, null, Turn.Nobody);

            planned.Add(pair.Turn switch
            {
                Turn.Mine => new Addressed(
                    peer,
                    Answering(peer) ? Act.Answer : Act.AskBack,
                    pair.OpenExchange,
                    Choosable: true),
                Turn.Theirs => new Addressed(peer, Act.Clarify, pair.OpenExchange, Choosable: false),
                _ => new Addressed(peer, Act.Ask, null, Choosable: false),
            });
        }

        return planned;
    }

    /// <summary>What the plan costs, in exchanges.</summary>
    /// <returns>How many exchanges open, close, and have a turn handed back.</returns>
    public Cost Costs()
    {
        var plan = Plan();

        return new Cost(
            plan.Count(a => a.Act == Act.Ask),
            plan.Count(a => a.Act == Act.Answer),
            plan.Count(a => a.Act is Act.Clarify or Act.AskBack));
    }
}
