using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parley.Core;

/// <summary>
/// Defines what makes two messages the same message, for the deduplication in SPEC.md §13.1.
/// </summary>
/// <remarks>
/// A protocol rule rather than a storage detail: §13.1 names the algorithm so independent
/// implementations sharing a database agree on what a duplicate is. An implementation hashing
/// different fields would populate the same column with values that never collide, and deduplication
/// would quietly stop working between them.
/// </remarks>
public static class MessageIdentity
{
    /// <summary>The window within which a byte-identical message is treated as a duplicate.</summary>
    /// <remarks>
    /// It absorbs transport retries rather than policing a participant repeating itself minutes later.
    /// The storage backstop derives its bucket from this same number, so changing it costs a migration.
    /// </remarks>
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(60);

    /// <summary>Computes the deduplication identity of a message.</summary>
    /// <param name="exchangeId">The exchange the message belongs to, or <see langword="null"/> when computing a Request's content identity.</param>
    /// <param name="exchangeVersion">The exchange's <c>version</c> as the message is written; zero for a Request's content identity.</param>
    /// <param name="type">The message type.</param>
    /// <param name="from">The sender.</param>
    /// <param name="to">The recipient.</param>
    /// <param name="payloadJson">The payload, exactly as it will be stored.</param>
    /// <returns>The SHA-256 digest as 64 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payloadJson"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Routing is part of the identity. The same text to a different participant, or on a different
    /// exchange, is a different message. Fields are joined with <see cref="PairKey.Separator"/>, which
    /// cannot occur in an agent name, so two different combinations cannot concatenate to one string.
    /// The envelope is deliberately not hashed: its token is fresh per delivery and would make every
    /// message unique.
    /// </para>
    /// <para>
    /// <b>So is the point in the exchange.</b> Without the version, a requester answering "yes" to a
    /// second clarification inside the window wrote the same identity as its first "yes": the reply was
    /// reported sent, the turn never moved, and since nothing times an exchange out it stayed stuck.
    /// Every move increments the version, so a transport retry of a move is at the version it was
    /// written at and a new move is not. A retry that arrives after its move applied is refused by the
    /// turn anyway, before deduplication is reached.
    /// </para>
    /// </remarks>
    public static string Compute(
        long? exchangeId,
        long exchangeVersion,
        MessageType type,
        AgentName from,
        AgentName to,
        string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var canonical = string.Join(
            PairKey.Separator,
            exchangeId?.ToString(CultureInfo.InvariantCulture) ?? "",
            exchangeVersion.ToString(CultureInfo.InvariantCulture),
            FormatType(type),
            from.Value,
            to.Value,
            payloadJson);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// Computes the content identity a Request is held to for as long as its exchange stays open (§7.1).
    /// </summary>
    /// <param name="from">The requester.</param>
    /// <param name="to">The receiver.</param>
    /// <param name="payloadJson">The Request payload, exactly as it will be stored.</param>
    /// <returns>The SHA-256 digest as 64 lowercase hexadecimal characters.</returns>
    /// <remarks>
    /// <para>
    /// The hash of §13.1 with the exchange identifier excluded and a version of zero, because the value
    /// is written as the exchange is created and there is no identifier yet. Folding one in afterwards
    /// would make every hash unique and silently disable the distinctness index.
    /// </para>
    /// <para>
    /// The whole payload is hashed, which since 0.7 is the ask (§5.1). Two Requests are the same Request when
    /// their asks are the same words; a payload written under an earlier version hashes everything it carried,
    /// and its hash is unchanged — nothing rewrote it.
    /// </para>
    /// </remarks>
    public static string ComputeRequest(AgentName from, AgentName to, string payloadJson) =>
        Compute(exchangeId: null, exchangeVersion: 0, MessageType.Request, from, to, payloadJson);

    /// <summary>Formats a message type as the token used in storage and in the identity hash.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The lowercase token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public static string FormatType(MessageType type) => type switch
    {
        MessageType.Request => "request",
        MessageType.Clarify => "clarify",
        MessageType.Result => "result",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>Parses a stored message type token.</summary>
    /// <param name="value">The token read from storage.</param>
    /// <returns>The corresponding type.</returns>
    /// <exception cref="InvalidOperationException">The token is not one the protocol defines.</exception>
    public static MessageType ParseType(string value) => value switch
    {
        "request" => MessageType.Request,
        "clarify" => MessageType.Clarify,
        "result" => MessageType.Result,
        _ => throw new InvalidOperationException($"Unrecognised message type '{value}'.")
    };
}
