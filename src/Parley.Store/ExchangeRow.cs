using System.Globalization;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A flat projection of a row in the <c>exchanges</c> table, and the translation between that row and
/// the <see cref="Exchange"/> aggregate.
/// </summary>
/// <remarks>
/// The aggregate is deliberately not mappable by an ORM. <see cref="AgentName"/> and
/// <see cref="PairKey"/> are validated value types and <see cref="Exchange"/> guards every transition,
/// so materialising a query result straight into the aggregate would route around every invariant the
/// domain enforces. The row stays primitive, the translation stays visible, and validation runs on the
/// way back in. Every property mirrors a column exactly.
/// </remarks>
public sealed class ExchangeRow
{
    /// <summary>Gets the <c>id</c> column.</summary>
    public long Id { get; init; }

    /// <summary>Gets the <c>pair_key</c> column.</summary>
    public string PairKey { get; init; } = "";

    /// <summary>Gets the <c>requester</c> column.</summary>
    public string Requester { get; init; } = "";

    /// <summary>Gets the <c>receiver</c> column.</summary>
    public string Receiver { get; init; } = "";

    /// <summary>Gets the <c>owed_by</c> column.</summary>
    public string OwedBy { get; init; } = "";

    /// <summary>Gets the <c>state</c> column: <c>OPEN</c> or <c>CLOSED</c>.</summary>
    public string State { get; init; } = "";

    /// <summary>Gets the <c>clarify_depth</c> column.</summary>
    public int ClarifyDepth { get; init; }

    /// <summary>Gets the <c>clarify_depth_cap</c> column.</summary>
    public int ClarifyDepthCap { get; init; }

    /// <summary>Gets the <c>uncollected_flag_ms</c> column, in whole milliseconds.</summary>
    public long UncollectedFlagMs { get; init; }

    /// <summary>Gets the <c>stalled_flag_ms</c> column, in whole milliseconds.</summary>
    public long StalledFlagMs { get; init; }

    /// <summary>Gets the <c>opened_utc</c> column, round-trip formatted.</summary>
    public string OpenedUtc { get; init; } = "";

    /// <summary>Gets the <c>delivered_utc</c> column, or <see langword="null"/> before collection.</summary>
    public string? DeliveredUtc { get; init; }

    /// <summary>Gets the <c>closed_utc</c> column, or <see langword="null"/> while open.</summary>
    public string? ClosedUtc { get; init; }

    /// <summary>Gets the <c>outcome</c> column, or <see langword="null"/> while open.</summary>
    public string? Outcome { get; init; }

    /// <summary>Gets the <c>version</c> column: the optimistic concurrency stamp.</summary>
    public long Version { get; init; }

    /// <summary>Translates this row into the domain aggregate.</summary>
    /// <returns>The rehydrated exchange.</returns>
    /// <exception cref="ArgumentException">A stored agent name does not satisfy the grammar.</exception>
    /// <exception cref="InvalidOperationException">
    /// A stored token is not recognised, or the stored pair key is not the one derived from the
    /// participants.
    /// </exception>
    /// <exception cref="FormatException">A stored timestamp is not in round-trip format.</exception>
    /// <remarks>
    /// These indicate a corrupted or externally modified database rather than ordinary bad input. The
    /// schema's constraints are the first line of defence; this is the second.
    /// </remarks>
    public Exchange ToDomain()
    {
        var requester = AgentName.Create(Requester);
        var receiver = AgentName.Create(Receiver);

        var stored = Core.PairKey.FromStorage(PairKey);
        var derived = Core.PairKey.For(requester, receiver);

        if (stored != derived)
        {
            throw new InvalidOperationException(
                $"Exchange {Id} has pair key '{stored}', which is not the key derived from " +
                $"'{requester}' and '{receiver}' ('{derived}'). The row was modified outside the " +
                "application.");
        }

        return Exchange.Rehydrate(
            Id,
            requester,
            receiver,
            AgentName.Create(OwedBy),
            ParseState(State),
            ClarifyDepth,
            ClarifyDepthCap,
            TimeSpan.FromMilliseconds(UncollectedFlagMs),
            TimeSpan.FromMilliseconds(StalledFlagMs),
            ParseTimestamp(OpenedUtc),
            DeliveredUtc is null ? null : ParseTimestamp(DeliveredUtc),
            ClosedUtc is null ? null : ParseTimestamp(ClosedUtc),
            OutcomeTokens.ParseExchange(Outcome),
            Version);
    }

    /// <summary>Formats an instant for storage.</summary>
    /// <param name="value">The instant. Converted to UTC first.</param>
    /// <returns>The round-trip ("O") representation.</returns>
    /// <remarks>
    /// The format is load-bearing. Round-trip strings sort lexicographically in the same order as the
    /// instants they represent, which is what lets the flag thresholds and the retention rules compare
    /// timestamps as plain strings and still be correct.
    /// </remarks>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Parses a stored timestamp.</summary>
    /// <param name="value">The round-trip formatted text.</param>
    /// <returns>The parsed instant.</returns>
    /// <exception cref="FormatException"><paramref name="value"/> is not in round-trip format.</exception>
    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Formats a lifecycle state for storage.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The uppercase token the schema constrains.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public static string FormatState(ExchangeState state) => state switch
    {
        ExchangeState.Open => "OPEN",
        ExchangeState.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    /// <summary>Parses a stored lifecycle state.</summary>
    /// <param name="value">The token.</param>
    /// <returns>The state.</returns>
    /// <exception cref="InvalidOperationException">The token is not one the schema admits.</exception>
    internal static ExchangeState ParseState(string value) => value switch
    {
        "OPEN" => ExchangeState.Open,
        "CLOSED" => ExchangeState.Closed,
        _ => throw new InvalidOperationException($"Unrecognised exchange state '{value}'.")
    };
}
