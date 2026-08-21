using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parley.Core;

/// <summary>
/// Defines what makes two messages the same message, for the deduplication in SPEC.md §13.1.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the domain rather than the store because it is a protocol rule, not a
/// storage detail. SPEC.md §13.1 names the algorithm so that independent implementations
/// sharing a database agree on what a duplicate is; an implementation that hashed different
/// fields, or joined them differently, would populate the same column with values that never
/// collide, and deduplication would quietly stop working between them.
/// </para>
/// <para>
/// Deduplication guards delivery, not intent. A repeated question is already refused by the
/// pair slot, a repeated clarification by the turn check, and a repeated answer by the
/// exchange no longer being open. <see cref="MessageType.Notify"/> is the one type with no
/// such guard, and is where this does the most work.
/// </para>
/// </remarks>
public static class MessageIdentity
{
    /// <summary>
    /// The window within which a byte-identical message is treated as a duplicate.
    /// </summary>
    /// <remarks>
    /// A starting estimate, in the spirit of <see cref="ExchangePolicy.Default"/>.
    /// It exists to absorb transport retries, not to police an agent repeating itself minutes
    /// later, and should be tuned against real traffic rather than argued about in advance.
    /// The storage backstop derives its bucket from this same number, so the two must move
    /// together — which is why changing it costs a migration.
    /// </remarks>
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Computes the deduplication identity of a message.
    /// </summary>
    /// <param name="exchangeId">
    /// The exchange this message belongs to, or <see langword="null"/> for a notification.
    /// </param>
    /// <param name="type">The message type.</param>
    /// <param name="from">The sending agent.</param>
    /// <param name="to">The receiving agent.</param>
    /// <param name="payloadJson">The message payload, exactly as it will be stored.</param>
    /// <returns>The SHA-256 digest as 64 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payloadJson"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Routing is part of the identity, not just content. The same text to a different peer,
    /// or on a different exchange, is a different message and must not be suppressed as a
    /// duplicate of one the recipient never saw.
    /// </para>
    /// <para>
    /// Fields are joined with <see cref="PairKey.Separator"/>, reusing the single definition
    /// of that delimiter rather than restating it, and for the same reason the pair key needs
    /// one: without an unambiguous separator two different field combinations can concatenate
    /// to identical text and collide. The separator cannot occur in an agent name, which the
    /// schema enforces.
    /// </para>
    /// <para>
    /// The trust envelope is deliberately not hashed. It carries a token that is fresh for
    /// every delivery, so including it would make every message unique and defeat this
    /// mechanism entirely.
    /// </para>
    /// </remarks>
    public static string Compute(
        long? exchangeId,
        MessageType type,
        AgentName from,
        AgentName to,
        string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var canonical = string.Join(
            PairKey.Separator,
            exchangeId?.ToString(CultureInfo.InvariantCulture) ?? "",
            FormatType(type),
            from.Value,
            to.Value,
            payloadJson);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// Computes the content identity a question is held to for as long as its exchange stays
    /// open, per SPEC.md §7.1.
    /// </summary>
    /// <param name="from">The asker.</param>
    /// <param name="to">The answerer.</param>
    /// <param name="payloadJson">The question payload, exactly as it will be stored.</param>
    /// <returns>The SHA-256 digest as 64 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payloadJson"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// §7.1 says identity here is the content hash of §13.1, and this is that hash with the
    /// exchange identifier excluded. It has to be: the value is written <em>as</em> the exchange
    /// is created, so there is no identifier yet, and folding one in afterwards would make every
    /// question hash unique and silently disable the distinctness index — the failure mode §13.1
    /// warns about for <c>dedup_bucket</c>, reached from the other direction. Two agents asking
    /// the same thing would both be admitted while the index reported no conflict.
    /// </para>
    /// <para>
    /// The whole payload is hashed, not the question text alone. Rule 1 makes an asker state
    /// what it will do with each possible answer, and that statement is part of what is being
    /// asked: the same words with different consequences ask the answerer to weigh different
    /// things. Hashing only the text would also put the store in the business of knowing the
    /// payload's shape, which is the adapter's concern.
    /// </para>
    /// <para>
    /// The scope differs from §13.1 even though the algorithm does not. Deduplication guards
    /// delivery for sixty seconds, absorbing transport retries; this guards intent for as long
    /// as the exchange is open, which under §6 may be hours. An agent that asks, forgets, and
    /// asks again is what this catches.
    /// </para>
    /// </remarks>
    public static string ComputeQuestion(AgentName from, AgentName to, string payloadJson) =>
        Compute(exchangeId: null, MessageType.Question, from, to, payloadJson);

    /// <summary>
    /// Formats a message type as the token used in storage and in the identity hash.
    /// </summary>
    /// <param name="type">The type to format.</param>
    /// <returns>The lowercase token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="type"/> is not a defined <see cref="MessageType"/> value.
    /// </exception>
    /// <remarks>
    /// Written out rather than persisting the enum's numeric value, so the schema's
    /// <c>CHECK</c> constraint can validate it and the table stays legible in a SQLite
    /// client. Because the same token feeds the hash, renumbering the enum cannot silently
    /// change what counts as a duplicate.
    /// </remarks>
    public static string FormatType(MessageType type) => type switch
    {
        MessageType.Question => "question",
        MessageType.Clarify => "clarify",
        MessageType.Answer => "answer",
        MessageType.Notify => "notify",
        MessageType.Ghost => "ghost",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// Parses a stored message type token.
    /// </summary>
    /// <param name="value">The token read from storage.</param>
    /// <returns>The corresponding type.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> is not a recognised token, indicating a corrupted or
    /// externally modified database.
    /// </exception>
    public static MessageType ParseType(string value) => value switch
    {
        "question" => MessageType.Question,
        "clarify" => MessageType.Clarify,
        "answer" => MessageType.Answer,
        "notify" => MessageType.Notify,
        "ghost" => MessageType.Ghost,
        _ => throw new InvalidOperationException($"Unrecognised message type '{value}'.")
    };
}
