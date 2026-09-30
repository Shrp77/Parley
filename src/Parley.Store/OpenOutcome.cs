using Parley.Core;

namespace Parley.Store;

/// <summary>
/// Why a Request was refused (SPEC.md §7.3).
/// </summary>
/// <remarks>
/// Every refusal returns something the sender can act on now — the conversations it may name, the
/// exchange already carrying the Request, what it already holds open — and none of them asks it to try
/// again later. A model told to retry later loops or gives up silently.
/// </remarks>
public enum RequestRejection
{
    /// <summary>No refusal; the exchange was opened.</summary>
    None = 0,

    /// <summary>A request cap the deployment set has been reached with this receiver.</summary>
    /// <remarks>See <see cref="OpenOutcome.Outstanding"/> for what the sender already holds open with it.</remarks>
    Saturated,

    /// <summary>An identical Request from this sender to this receiver is still open.</summary>
    /// <remarks>See <see cref="OpenOutcome.Outstanding"/> for the exchange carrying it.</remarks>
    Duplicate,

    /// <summary>Nothing is registered under the receiver's name.</summary>
    /// <remarks>
    /// Almost always a misspelling, or a name remembered from before a re-registration. The roster in
    /// <c>agents()</c> is the remedy.
    /// </remarks>
    ReceiverNotRegistered,

    /// <summary>A person retired the receiver, so the work has to go to whoever owns it now (§14).</summary>
    ReceiverRetired,

    /// <summary>A person retired the sender, which may not open new work.</summary>
    SenderRetired,

    /// <summary>The conversation is closed, and says by whom.</summary>
    /// <remarks>Work worth resuming after a conversation closed is new work (§5.0).</remarks>
    ConversationClosed,

    /// <summary>
    /// The sender may not send into the conversation it named, or named none while holding Requests
    /// from several (§5.0).
    /// </summary>
    /// <remarks>See <see cref="OpenOutcome.Assignable"/> for the conversations it may name.</remarks>
    ConversationNotAssignable,

    /// <summary>A Request addressed a role nothing is registered under (§5.5).</summary>
    NoSuchRole,

    /// <summary>The Request was addressed to the facilitator, which receives nobody's work (§10.1).</summary>
    /// <remarks>
    /// Leaving it off <c>agents()</c> does not stop an agent naming it, and a Request it will never answer would sit
    /// flagged for ever. The facilitator asks; it does not do work (SPEC.md principle 2.11).
    /// </remarks>
    ReceiverIsTheFacilitator
}

/// <summary>
/// The result of trying to open an exchange: the exchange, or what the sender can do about the refusal.
/// </summary>
/// <remarks>
/// The constructor is private and the named factories are the only way to build one, so a refusal
/// carrying an exchange, or success without one, cannot be constructed. Callers read
/// <see cref="Succeeded"/> and then dereference <see cref="Exchange"/>.
/// </remarks>
public sealed record OpenOutcome
{
    private OpenOutcome(
        Exchange? exchange,
        RequestRejection rejection,
        IReadOnlyList<Exchange> outstanding,
        IReadOnlyList<AgentName> cyclePath,
        IReadOnlyList<string> assignable,
        ConversationCloser? closedBy)
    {
        Exchange = exchange;
        Rejection = rejection;
        Outstanding = outstanding;
        CyclePath = cyclePath;
        Assignable = assignable;
        ClosedBy = closedBy;
    }

    /// <summary>Gets the opened exchange.</summary>
    /// <value>Non-null exactly when <see cref="Succeeded"/> is <see langword="true"/>.</value>
    public Exchange? Exchange { get; }

    /// <summary>Gets why the Request was refused.</summary>
    /// <value><see cref="RequestRejection.None"/> on success.</value>
    public RequestRejection Rejection { get; }

    /// <summary>Gets what the sender already has open with this receiver.</summary>
    /// <value>
    /// On <see cref="RequestRejection.Saturated"/> every open exchange to the receiver; on
    /// <see cref="RequestRejection.Duplicate"/> the one already carrying the Request; otherwise empty.
    /// </value>
    public IReadOnlyList<Exchange> Outstanding { get; }

    /// <summary>Gets the cycle this Request closed, when it closed one.</summary>
    /// <value>
    /// On success, the participants in the order each waits on the next, starting with the sender —
    /// recorded on the conversation rather than refused (§7.2). Empty otherwise.
    /// </value>
    public IReadOnlyList<AgentName> CyclePath { get; }

    /// <summary>Gets the conversations the sender may name.</summary>
    /// <value>On <see cref="RequestRejection.ConversationNotAssignable"/>; empty otherwise.</value>
    public IReadOnlyList<string> Assignable { get; }

    /// <summary>Gets who closed the conversation.</summary>
    /// <value>
    /// On <see cref="RequestRejection.ConversationClosed"/>, when it was recorded; otherwise
    /// <see langword="null"/>.
    /// </value>
    public ConversationCloser? ClosedBy { get; }

    /// <summary>Gets a value indicating whether the exchange was opened.</summary>
    /// <value><see langword="true"/> when <see cref="Exchange"/> is populated.</value>
    public bool Succeeded => Rejection == RequestRejection.None;

    /// <summary>Creates a successful outcome.</summary>
    /// <param name="exchange">The opened exchange.</param>
    /// <param name="cyclePath">The cycle it closed, or empty.</param>
    /// <returns>The outcome.</returns>
    public static OpenOutcome Opened(Exchange exchange, IReadOnlyList<AgentName>? cyclePath = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return new(exchange, RequestRejection.None, [], cyclePath ?? [], [], null);
    }

    /// <summary>Creates a refusal for a request cap reached.</summary>
    /// <param name="outstanding">Everything the sender already holds open with the receiver.</param>
    /// <returns>The refusal.</returns>
    public static OpenOutcome Saturated(IReadOnlyList<Exchange> outstanding)
    {
        ArgumentNullException.ThrowIfNull(outstanding);
        return new(null, RequestRejection.Saturated, outstanding, [], [], null);
    }

    /// <summary>Creates a refusal for a Request already open.</summary>
    /// <param name="existing">The exchange already carrying it.</param>
    /// <returns>The refusal.</returns>
    public static OpenOutcome Duplicate(Exchange existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new(null, RequestRejection.Duplicate, [existing], [], [], null);
    }

    /// <summary>Creates a refusal of the kinds that carry nothing but their reason.</summary>
    /// <param name="rejection">The reason.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="ArgumentException">The reason carries data and has a factory of its own.</exception>
    public static OpenOutcome Refused(RequestRejection rejection) => rejection switch
    {
        RequestRejection.ReceiverNotRegistered or RequestRejection.ReceiverRetired or
            RequestRejection.SenderRetired or RequestRejection.NoSuchRole or
            RequestRejection.ReceiverIsTheFacilitator =>
            new(null, rejection, [], [], [], null),
        _ => throw new ArgumentException(
            $"{rejection} carries what the sender can act on; use its own factory.", nameof(rejection))
    };

    /// <summary>Creates a refusal for a closed conversation.</summary>
    /// <param name="closedBy">Who closed it, when recorded.</param>
    /// <returns>The refusal.</returns>
    public static OpenOutcome ConversationClosed(ConversationCloser? closedBy) =>
        new(null, RequestRejection.ConversationClosed, [], [], [], closedBy);

    /// <summary>Creates a refusal for a conversation the sender may not send into.</summary>
    /// <param name="assignable">The conversations it may name.</param>
    /// <returns>The refusal.</returns>
    public static OpenOutcome NotAssignable(IReadOnlyList<string> assignable)
    {
        ArgumentNullException.ThrowIfNull(assignable);
        return new(null, RequestRejection.ConversationNotAssignable, [], [], assignable, null);
    }
}

/// <summary>
/// Which conversation a Request goes into, as §5.0 decides it from what the sender holds.
/// </summary>
/// <param name="ConversationId">The conversation, when one could be assigned.</param>
/// <param name="Starts">Whether this Request starts it.</param>
/// <param name="Refusal">Why none could be assigned, or <see langword="null"/>.</param>
/// <remarks>
/// Decided once per Request, before any exchange is opened, so every exchange a Request to a role opens
/// (§5.5) lands in the same conversation — including one the broker names on the sender's behalf.
/// </remarks>
public sealed record ConversationAssignment(string? ConversationId, bool Starts, OpenOutcome? Refusal)
{
    /// <summary>Gets a value indicating whether a conversation was assigned.</summary>
    /// <value><see langword="true"/> when <see cref="ConversationId"/> is set.</value>
    public bool Succeeded => Refusal is null;
}
