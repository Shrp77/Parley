using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A flat projection of a row in the <c>messages</c> table, and the translation between that
/// row and <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ExchangeRow"/>, and for the same reason: <see cref="AgentName"/> is a
/// validated value type with a private constructor, so materialising a query result straight
/// into the domain type would route around the validation. The row stays primitive and the
/// translation stays visible. <c>dedup_bucket</c> and <c>content_hash</c> are both absent:
/// the schema derives the bucket and a writer may not supply one, and the hash is
/// deduplication machinery that <see cref="Message"/> deliberately does not carry, so
/// surfacing it here would put a storage concern back into the shape callers read.
/// </remarks>
public sealed class MessageRow
{
    /// <summary>Gets the <c>id</c> column: the message's primary key.</summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the <c>exchange_id</c> column, or <see langword="null"/> for a notification.
    /// </summary>
    public long? ExchangeId { get; init; }

    /// <summary>Gets the <c>type</c> column: the message kind as its stored token.</summary>
    public string Type { get; init; } = "";

    /// <summary>Gets the <c>from_agent</c> column: the sender's name.</summary>
    public string FromAgent { get; init; } = "";

    /// <summary>Gets the <c>to_agent</c> column: the recipient's name.</summary>
    public string ToAgent { get; init; } = "";

    /// <summary>Gets the <c>responder_model</c> column: what produced this message.</summary>
    public string ResponderModel { get; init; } = "";

    /// <summary>Gets the <c>payload_json</c> column.</summary>
    public string PayloadJson { get; init; } = "";

    /// <summary>
    /// Gets the <c>session_tag</c> column, or <see langword="null"/> for untagged traffic.
    /// </summary>
    public string? SessionTag { get; init; }

    /// <summary>Gets the <c>created_utc</c> column, round-trip formatted.</summary>
    public string CreatedUtc { get; init; } = "";

    /// <summary>
    /// Gets the <c>delivered_utc</c> column, or <see langword="null"/> while pending.
    /// </summary>
    public string? DeliveredUtc { get; init; }

    /// <summary>
    /// Translates this row into the domain record.
    /// </summary>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">
    /// A stored agent name does not satisfy the <see cref="AgentName"/> grammar.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stored message type is not a recognised token.
    /// </exception>
    /// <exception cref="FormatException">
    /// A stored timestamp is not in round-trip format.
    /// </exception>
    /// <remarks>
    /// These indicate a corrupted or externally modified database rather than ordinary bad
    /// input. The schema's constraints are the first line of defence; this is the second.
    /// </remarks>
    public Message ToDomain() => new(
        Id,
        ExchangeId,
        MessageIdentity.ParseType(Type),
        AgentName.Create(FromAgent),
        AgentName.Create(ToAgent),
        ResponderModel,
        PayloadJson,
        SessionTag,
        ExchangeRow.ParseTimestamp(CreatedUtc),
        DeliveredUtc is null ? null : ExchangeRow.ParseTimestamp(DeliveredUtc));
}
