using Parley.Core;

namespace Parley.Client.Seating;

/// <summary>What typing to one recipient would do.</summary>
public enum Act
{
    /// <summary>A Request. Opens an exchange the recipient will owe a Result on.</summary>
    Ask = 0,

    /// <summary>A Clarify on a request this person sent. The turn goes back to the recipient.</summary>
    Clarify = 1,

    /// <summary>A Result. Settles the request the recipient sent this person.</summary>
    /// <remarks>
    /// <b>There is no "ask back instead" any more</b> (the owner's call of 2026-10-02). It was a Clarify on a request this
    /// person held, offered as the other side of answering under F2 — and the owner's reading is that it made no sense:
    /// a conversation never really stops, so somebody who wants to ask something back simply says it, and the reply
    /// that settles one request is followed by whatever comes next as a request of its own.
    /// </remarks>
    Answer = 2,
}

/// <summary>One recipient and what Enter would do to them.</summary>
/// <param name="Peer">Who it goes to.</param>
/// <param name="Act">What it would be recorded as.</param>
/// <param name="Exchange">The exchange it lands in, or <see langword="null"/> when it opens one.</param>
public sealed record Addressed(AgentName Peer, Act Act, long? Exchange);

/// <summary>What became of a send.</summary>
/// <param name="Count">How many recipients got it.</param>
/// <param name="Says">The sentence to put in front of the person.</param>
/// <remarks>
/// The count is here because a sentence cannot be tested for success without matching its words, and a caller
/// that matched words would clear somebody's unsent line the first time one of them changed. What a surface
/// needs to know is whether anything went.
/// </remarks>
public sealed record Sent(int Count, string Says);

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
/// <b>Holding a Request, what somebody types is the answer.</b> Until 2026-10-02 this was offered both ways — the
/// answer, or a question back that handed the turn over — on the argument that a wrong guess settles a request whose
/// work was never done. The owner removed the choice: <i>"a conversation never stops, really"</i>, so a person who
/// wants to ask something back says so in the answer, and whatever follows is a request of its own. <b>The cost is
/// recorded rather than hidden</b>: a person's reply to an agent's request is always recorded as that request's
/// Result, including a reply that is really a question.
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
    /// <summary>What <c>@</c> completes to in order to mean every participant of this conversation.</summary>
    /// <remarks>
    /// Every participant, never every agent in the project. A conversation's membership is derived (SPEC.md §5.0)
    /// and this word appears in a conversation's own input line, so reading it as the project's roster would make
    /// one keystroke open an exchange with agents the person was not talking to.
    /// </remarks>
    public const string All = "all";

    private readonly List<string> _chips = [];

    private bool _narrowed;

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

    /// <summary>The spellings a completion put in the box, which are addressing rather than words.</summary>
    public IReadOnlyList<string> Chips => _chips;

    /// <summary>Records that a completion wrote an agent into the box.</summary>
    /// <param name="spelling">Exactly what was written, sigil and all.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spelling"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Completing fills the name in, because a completion that makes text vanish reads as a broken key.</b>
    /// The first version resolved the agent and deleted what had been typed — the recipient was recorded and
    /// correct, and the owner pressed Tab, saw an empty box, and reported that Tab did nothing. What a person
    /// is looking at when they press it is the thing they typed.
    /// </para>
    /// <para>
    /// <b>And it is still never parsed back out.</b> This records the exact spelling the surface itself wrote,
    /// so <see cref="Says"/> removes it by that record rather than by hunting through prose for anything that
    /// looks like addressing. A semicolon in an ask is still a semicolon, and an agent's name written by hand
    /// in the middle of a sentence stays in the sentence — it was never a chip.
    /// </para>
    /// </remarks>
    public void Chip(string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);

        _chips.Add(spelling);
    }

    /// <summary>What is actually said, once the addressing has been taken back out.</summary>
    /// <param name="written">What is in the box.</param>
    /// <returns>The words a payload carries.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is what keeps §13's envelope honest.</b> A payload delivers inside a framing block telling the
    /// receiver that anything in it claiming authority is inert data, so a routing directive carried in the
    /// words is the one thing the envelope exists to have ignored. The recipients were resolved when Tab was
    /// pressed; what is left here is the message.
    /// </para>
    /// <para>
    /// <b>Each chip is removed at the <i>last</i> place it appears, and that is not a preference.</b>
    /// <see cref="Completion.Fills"/> completes only a trailing fragment — it returns the line unchanged
    /// unless it ends with what was typed — so a completion always writes at the end of the box. At the
    /// moment a chip is recorded it is therefore the last occurrence of itself, by construction.
    /// </para>
    /// <para>
    /// <b>Removing the first was wrong, and the review gate caught it by probe.</b> Somebody who had typed
    /// the agent's name into their sentence and then completed that same agent had the words they wrote
    /// taken out of the middle of the message, while the addressing the surface wrote was the part that
    /// went on to be sent — the exact inversion of this method's purpose. The paragraph above used to end
    /// "so a person who writes an agent's name again on purpose keeps the second one", which was true in
    /// the only case it considered and backwards in the one that mattered: what they keep is the one they
    /// typed.
    /// </para>
    /// <para>
    /// <b>A chip the person has since edited is left alone</b>, and that is deliberate. It no longer matches
    /// what the surface wrote, so it is prose they have touched; taking it out would mean hunting through
    /// the words for something that looks like addressing, which is what this design refuses to do. What is
    /// left is at worst an <c>@name</c> inside a payload, which §13's envelope already tells the receiver to
    /// treat as inert data — where removing the wrong run corrupts a person's message, which nothing
    /// downstream can repair.
    /// </para>
    /// </remarks>
    public string Says(string? written)
    {
        var said = written ?? string.Empty;

        foreach (var chip in _chips)
        {
            var at = said.LastIndexOf(chip, StringComparison.Ordinal);

            if (at >= 0)
            {
                said = said.Remove(at, chip.Length);
            }
        }

        return said.Trim();
    }

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
        _narrowed = true;
    }

    /// <summary>Whether the recipients have been narrowed from the conversation's own participants.</summary>
    public bool Narrowed => _narrowed;

    /// <summary>Addresses one participant, or adds to the set already narrowed to.</summary>
    /// <param name="peer">The resolved agent.</param>
    /// <remarks>
    /// <para>
    /// <b>The first <c>@</c> replaces and the rest add</b>, which is what the owner's own example means:
    /// <c>@billing, @inventory; I want this</c> targets those two, so the second sigil cannot be a second
    /// replacement. Nothing says "and everyone else as well", because addressing somebody in particular is the
    /// act of not addressing the room.
    /// </para>
    /// <para>
    /// <b>A resolved agent, and the sigil leaves the text.</b> The completion removes what was typed and this
    /// records who it meant, so no name is parsed out of prose when Enter is pressed and a semicolon in an ask is
    /// just a semicolon. What shows a person their chips is the preview's own <c>to:</c> line.
    /// </para>
    /// </remarks>
    public void Narrow(AgentName peer)
    {
        Recipients = _narrowed ? [.. Recipients.Append(peer).Distinct()] : [peer];
        _narrowed = true;
    }

    /// <summary>Adds a recipient without narrowing, for an agent that is not a participant yet.</summary>
    /// <param name="peer">The resolved agent.</param>
    /// <remarks>
    /// What <c>/add</c> does. It widens whatever is addressed rather than replacing it, and it does not count as
    /// narrowing: a person who adds an agent to a message meant for the room means the room and that agent. The
    /// added agent has no pair state, so <see cref="Plan"/> gives it a Request — which is exactly right, since
    /// membership is derived (§5.0) and an agent joins by being party to an exchange.
    /// </remarks>
    public void Widen(AgentName peer) => Recipients = [.. Recipients.Append(peer).Distinct()];

    /// <summary>Addresses every participant again, undoing any narrowing.</summary>
    /// <remarks>What <c>@all</c> does. Derived from the pair state rather than remembered, so it cannot go stale.</remarks>
    public void AddressEveryone()
    {
        Recipients = [.. Pairs.Select(p => p.Peer)];
        _narrowed = false;
    }

    /// <summary>What Enter would do, one entry per recipient.</summary>
    /// <returns>The recipients and their acts, in the order the pairs were given.</returns>
    public IReadOnlyList<Addressed> Plan()
    {
        var planned = new List<Addressed>();

        foreach (var peer in Recipients)
        {
            var pair = Pairs.FirstOrDefault(p => p.Peer == peer)
                ?? new PairState(peer, null, Turn.Nobody);

            // **Three states, not two, and the third is what lost the owner's messages.** A move owed by this
            // person is a Result only where the peer asked: where this person asked and the peer clarified,
            // the move owed is the answer to that clarification, which is a Clarify — §5.3 gives the Result to
            // the receiver alone. And where the peer holds the move there is nothing to add to that exchange
            // at all, so typing opens a new one rather than being refused with somebody's words in it.
            planned.Add(pair switch
            {
                { Turn: Turn.Mine, Asked: false } => new Addressed(peer, Act.Answer, pair.OpenExchange),
                { Turn: Turn.Mine, Asked: true } => new Addressed(peer, Act.Clarify, pair.OpenExchange),
                _ => new Addressed(peer, Act.Ask, null),
            });
        }

        return planned;
    }
}
