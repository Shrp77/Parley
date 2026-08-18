using Parley.Core;

namespace Parley.Store;

/// <summary>
/// The reason an attempt to open an exchange was refused.
/// </summary>
public enum OpenRejection
{
    /// <summary>No refusal; the exchange was opened.</summary>
    None = 0,

    /// <summary>
    /// The pair already holds an open exchange.
    /// </summary>
    /// <remarks>
    /// Inspect <see cref="OpenOutcome.ReciprocalDebt"/> and
    /// <see cref="OpenOutcome.Blocking"/> to distinguish the two situations this covers:
    /// owing the peer an answer, versus already waiting on one.
    /// </remarks>
    PairBusy,

    /// <summary>
    /// Admitting the question would close a cycle in the wait-for graph, deadlocking every
    /// agent in it. See <see cref="OpenOutcome.CyclePath"/> for the agents involved.
    /// </summary>
    WouldDeadlock
}

/// <summary>
/// The result of attempting to open an exchange, carrying either the new exchange or enough
/// context for the caller to act on the refusal.
/// </summary>
/// <param name="Exchange">
/// The newly opened exchange, or <see langword="null"/> if the attempt was refused.
/// </param>
/// <param name="Rejection">
/// Why the attempt was refused, or <see cref="OpenRejection.None"/> on success.
/// </param>
/// <param name="ReciprocalDebt">
/// On <see cref="OpenRejection.PairBusy"/> where the asker is the party that owes a
/// response: the exchange it must resolve first. Otherwise <see langword="null"/>.
/// </param>
/// <param name="Blocking">
/// On <see cref="OpenRejection.PairBusy"/> where the asker is already waiting on this peer:
/// the exchange it is waiting on. Otherwise <see langword="null"/>.
/// </param>
/// <param name="CyclePath">
/// On <see cref="OpenRejection.WouldDeadlock"/>: the agents forming the cycle, starting with
/// the asker. Empty otherwise.
/// </param>
/// <remarks>
/// <para>
/// The two flavours of <see cref="OpenRejection.PairBusy"/> matter because they call for
/// completely different responses. When the asker owes a debt, the broker returns the
/// pending question rather than a retry instruction: instructing a language model to try
/// again later produces either a token-burning loop or silent abandonment, whereas handing
/// back the question it already owes converts a block into forward progress — and the answer
/// frequently makes the intended question unnecessary or reshapes it.
/// </para>
/// <para>
/// When the asker is merely waiting, there is no work to hand back and the outcome reports
/// what it is waiting on instead.
/// </para>
/// </remarks>
public sealed record OpenOutcome(
    Exchange? Exchange,
    OpenRejection Rejection,
    Exchange? ReciprocalDebt,
    Exchange? Blocking,
    IReadOnlyList<AgentName> CyclePath)
{
    /// <summary>
    /// Gets a value indicating whether the exchange was opened.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <see cref="Exchange"/> is populated; otherwise
    /// <see langword="false"/>.
    /// </value>
    public bool Succeeded => Rejection == OpenRejection.None;

    /// <summary>
    /// Creates a successful outcome.
    /// </summary>
    /// <param name="exchange">The newly opened exchange.</param>
    /// <returns>An outcome carrying <paramref name="exchange"/>.</returns>
    public static OpenOutcome Opened(Exchange exchange) =>
        new(exchange, OpenRejection.None, null, null, []);

    /// <summary>
    /// Creates a refusal indicating the asker must answer the peer before it may ask.
    /// </summary>
    /// <param name="debt">The exchange the asker owes a response to.</param>
    /// <returns>A refusal carrying the outstanding debt.</returns>
    public static OpenOutcome OweFirst(Exchange debt) =>
        new(null, OpenRejection.PairBusy, debt, null, []);

    /// <summary>
    /// Creates a refusal indicating the asker already has a question outstanding with this
    /// peer.
    /// </summary>
    /// <param name="blocking">The exchange the asker is currently waiting on.</param>
    /// <returns>A refusal carrying the blocking exchange.</returns>
    public static OpenOutcome AlreadyWaiting(Exchange blocking) =>
        new(null, OpenRejection.PairBusy, null, blocking, []);

    /// <summary>
    /// Creates a refusal indicating the question would deadlock.
    /// </summary>
    /// <param name="cyclePath">The agents forming the cycle, starting with the asker.</param>
    /// <returns>A refusal carrying the cycle path.</returns>
    public static OpenOutcome Deadlock(IReadOnlyList<AgentName> cyclePath) =>
        new(null, OpenRejection.WouldDeadlock, null, null, cyclePath);
}
