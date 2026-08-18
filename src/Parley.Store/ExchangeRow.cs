using System.Globalization;
using Parley.Core;

namespace Parley.Store;

/// <summary>
/// A flat projection of a row in the <c>exchanges</c> table, and the translation between
/// that row and the <see cref="Exchange"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate is deliberately not mappable by an ORM. <see cref="AgentName"/> and
/// <see cref="PairKey"/> are validated value types with private constructors, and
/// <see cref="Exchange"/> guards every transition. Materialising a query result straight
/// into the aggregate would route around every invariant the domain exists to enforce, so
/// the row stays primitive, the translation stays visible, and validation runs on the way
/// back in.
/// </para>
/// <para>
/// Every property mirrors a column exactly. Nothing is computed here.
/// </para>
/// </remarks>
public sealed class ExchangeRow
{
    /// <summary>Gets the <c>id</c> column: the exchange's primary key.</summary>
    public long Id { get; init; }

    /// <summary>Gets the <c>pair_key</c> column: the unordered participant pair key.</summary>
    public string PairKey { get; init; } = "";

    /// <summary>Gets the <c>asker</c> column: the name of the agent that opened the exchange.</summary>
    public string Asker { get; init; } = "";

    /// <summary>Gets the <c>answerer</c> column: the name of the agent that owes the closing answer.</summary>
    public string Answerer { get; init; } = "";

    /// <summary>Gets the <c>owed_by</c> column: the name of the participant who must respond next.</summary>
    public string OwedBy { get; init; } = "";

    /// <summary>
    /// Gets the <c>state</c> column: <c>OPEN</c>, <c>CLOSED</c>, or <c>ORPHANED</c>.
    /// </summary>
    public string State { get; init; } = "";

    /// <summary>Gets the <c>clarify_depth</c> column: clarification requests spent so far.</summary>
    public int ClarifyDepth { get; init; }

    /// <summary>Gets the <c>clarify_depth_cap</c> column: the clarification request limit.</summary>
    public int ClarifyDepthCap { get; init; }

    /// <summary>Gets the <c>ttl_seconds</c> column: the inactivity window, in whole seconds.</summary>
    public long TtlSeconds { get; init; }

    /// <summary>Gets the <c>opened_utc</c> column: the creation timestamp, round-trip formatted.</summary>
    public string OpenedUtc { get; init; } = "";

    /// <summary>Gets the <c>expires_utc</c> column: the current deadline, round-trip formatted.</summary>
    public string ExpiresUtc { get; init; } = "";

    /// <summary>
    /// Gets the <c>closed_utc</c> column: the closing timestamp, or <see langword="null"/>
    /// while the exchange remains unclosed.
    /// </summary>
    public string? ClosedUtc { get; init; }

    /// <summary>
    /// Gets the <c>is_decision</c> column: <c>1</c> when the answer was flagged for promotion
    /// to a decision record, otherwise <c>0</c>. SQLite has no boolean type.
    /// </summary>
    public long IsDecision { get; init; }

    /// <summary>
    /// Gets the <c>decision_ref</c> column: a pointer to the promoted decision record, or
    /// <see langword="null"/> if none was written.
    /// </summary>
    public string? DecisionRef { get; init; }

    /// <summary>
    /// Translates this row into the domain aggregate.
    /// </summary>
    /// <returns>The rehydrated exchange.</returns>
    /// <exception cref="ArgumentException">
    /// A stored agent name does not satisfy the <see cref="AgentName"/> grammar.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stored state is not a recognised value.
    /// </exception>
    /// <exception cref="FormatException">
    /// A stored timestamp is not in round-trip format.
    /// </exception>
    /// <remarks>
    /// The exceptions above indicate a corrupted or externally modified database rather than
    /// ordinary bad input. The schema's <c>CHECK</c> constraints and foreign keys are the
    /// first line of defence; this is the second.
    /// </remarks>
    public Exchange ToDomain() => Exchange.Rehydrate(
        Id,
        AgentName.Create(Asker),
        AgentName.Create(Answerer),
        AgentName.Create(OwedBy),
        ParseState(State),
        ClarifyDepth,
        ClarifyDepthCap,
        TimeSpan.FromSeconds(TtlSeconds),
        ParseTimestamp(OpenedUtc),
        ParseTimestamp(ExpiresUtc),
        ClosedUtc is null ? null : ParseTimestamp(ClosedUtc),
        IsDecision != 0);

    /// <summary>
    /// Formats an instant for storage.
    /// </summary>
    /// <param name="value">The instant to format. Converted to UTC first.</param>
    /// <returns>The round-trip ("O") representation.</returns>
    /// <remarks>
    /// The format choice is load-bearing, not cosmetic. Round-trip strings sort
    /// lexicographically in the same order as the instants they represent, which is what
    /// allows the reaper's <c>expires_utc &lt;= @Now</c> predicate to be a plain string
    /// comparison and still be correct, and what makes the <c>ix_open_expiry</c> index
    /// usable. Changing it would silently break both.
    /// </remarks>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a stored timestamp.
    /// </summary>
    /// <param name="value">The round-trip formatted text.</param>
    /// <returns>The parsed instant.</returns>
    /// <exception cref="FormatException">
    /// <paramref name="value"/> is not in round-trip format.
    /// </exception>
    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// Formats a lifecycle state for storage.
    /// </summary>
    /// <param name="state">The state to format.</param>
    /// <returns>The uppercase token stored in the <c>state</c> column.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="state"/> is not a defined <see cref="ExchangeState"/> value.
    /// </exception>
    /// <remarks>
    /// The tokens are written out rather than persisting the enum's numeric value, so the
    /// schema's <c>CHECK</c> constraint can validate them and so the table stays legible to
    /// anyone inspecting it with a SQLite client.
    /// </remarks>
    public static string FormatState(ExchangeState state) => state switch
    {
        ExchangeState.Open => "OPEN",
        ExchangeState.Closed => "CLOSED",
        ExchangeState.Orphaned => "ORPHANED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static ExchangeState ParseState(string value) => value switch
    {
        "OPEN" => ExchangeState.Open,
        "CLOSED" => ExchangeState.Closed,
        "ORPHANED" => ExchangeState.Orphaned,
        _ => throw new InvalidOperationException($"Unrecognised exchange state '{value}'.")
    };
}
