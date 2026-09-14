using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A flat projection of a row in the <c>messages</c> table, and the translation between that row and
/// <see cref="Message"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ExchangeRow"/>, for the same reason: materialising a query result straight into a
/// type built from validated values would route around the validation. <c>dedup_bucket</c> and
/// <c>content_hash</c> are absent: the schema derives the one and the other is deduplication machinery
/// <see cref="Message"/> deliberately does not carry.
/// </remarks>
public sealed class MessageRow
{
    /// <summary>Gets the <c>id</c> column.</summary>
    public long Id { get; init; }

    /// <summary>Gets the <c>exchange_id</c> column.</summary>
    public long ExchangeId { get; init; }

    /// <summary>Gets the <c>type</c> column, as its stored token.</summary>
    public string Type { get; init; } = "";

    /// <summary>Gets the <c>from_agent</c> column.</summary>
    public string FromAgent { get; init; } = "";

    /// <summary>Gets the <c>to_agent</c> column.</summary>
    public string ToAgent { get; init; } = "";

    /// <summary>Gets the <c>responder_model</c> column.</summary>
    public string ResponderModel { get; init; } = "";

    /// <summary>Gets the <c>payload_json</c> column.</summary>
    public string PayloadJson { get; init; } = "";

    /// <summary>Gets the <c>conversation_id</c> column.</summary>
    public string ConversationId { get; init; } = "";

    /// <summary>Gets the <c>created_utc</c> column, round-trip formatted.</summary>
    public string CreatedUtc { get; init; } = "";

    /// <summary>Gets the <c>delivered_utc</c> column, or <see langword="null"/> while pending.</summary>
    public string? DeliveredUtc { get; init; }

    /// <summary>Translates this row into the domain record.</summary>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">A stored agent name does not satisfy the grammar.</exception>
    /// <exception cref="InvalidOperationException">The stored type is not a recognised token.</exception>
    /// <exception cref="FormatException">A stored timestamp is not in round-trip format.</exception>
    public Message ToDomain() => new(
        Id,
        ExchangeId,
        MessageIdentity.ParseType(Type),
        AgentName.Create(FromAgent),
        AgentName.Create(ToAgent),
        ResponderModel,
        PayloadJson,
        ConversationId,
        ExchangeRow.ParseTimestamp(CreatedUtc),
        DeliveredUtc is null ? null : ExchangeRow.ParseTimestamp(DeliveredUtc));
}
