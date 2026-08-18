using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Parley.Core;

/// <summary>
/// A validated agent identifier: a short lowercase slug such as <c>orders</c> or
/// <c>billing</c>.
/// </summary>
/// <remarks>
/// <para>
/// An agent name is a <em>logical</em> identity. It is deliberately not a session
/// identifier and not a filesystem path. Session identifiers churn on every restart and
/// paths differ per machine; keying the registry and all exchange records on a stable
/// logical name is what allows an exchange to survive a reboot, be reassigned to a freshly
/// spawned agent, and be replayed into its inbox.
/// </para>
/// <para>
/// The grammar is <c>^[a-z][a-z0-9-]{0,31}$</c>. It is narrow on purpose: names appear
/// inside <see cref="PairKey"/> values, log lines, and adapter tool calls, so excluding
/// whitespace, case variation, and the pair separator removes whole categories of
/// ambiguity rather than handling them.
/// </para>
/// <para>
/// All comparison is ordinal. Culture-sensitive comparison of a value used in a lock key
/// would be a correctness bug, not a formatting preference.
/// </para>
/// </remarks>
public readonly partial struct AgentName : IEquatable<AgentName>, IComparable<AgentName>
{
    /// <summary>
    /// The maximum number of characters permitted in an agent name.
    /// </summary>
    public const int MaxLength = 32;

    private readonly string _value;

    private AgentName(string value) => _value = value;

    /// <summary>
    /// Gets the underlying name text.
    /// </summary>
    /// <value>The validated slug, for example <c>billing</c>.</value>
    /// <exception cref="InvalidOperationException">
    /// The instance is the default value of the struct and was never created through
    /// <see cref="Create"/> or <see cref="TryCreate"/>.
    /// </exception>
    public string Value => _value ?? throw new InvalidOperationException("Uninitialised AgentName.");

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern { get; }

    /// <summary>
    /// Attempts to create an <see cref="AgentName"/> from arbitrary text.
    /// </summary>
    /// <param name="candidate">
    /// The text to validate. May be <see langword="null"/>, which always fails.
    /// </param>
    /// <param name="name">
    /// When this method returns <see langword="true"/>, the validated name; otherwise the
    /// default value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="candidate"/> matches the agent name
    /// grammar; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Prefer this overload when the text originates outside the broker — a manifest file,
    /// an adapter handshake, or a message payload — where a malformed name is expected
    /// input rather than a programming error.
    /// </remarks>
    public static bool TryCreate(string? candidate, out AgentName name)
    {
        if (candidate is not null && Pattern.IsMatch(candidate))
        {
            name = new AgentName(candidate);
            return true;
        }

        name = default;
        return false;
    }

    /// <summary>
    /// Creates an <see cref="AgentName"/> from text, throwing if it is not valid.
    /// </summary>
    /// <param name="candidate">The text to validate.</param>
    /// <returns>The validated name.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="candidate"/> does not match the agent name grammar.
    /// </exception>
    /// <remarks>
    /// Use this when the name is known-good by construction, such as a literal in a test or
    /// a value already read back from a column that enforces the grammar. For untrusted
    /// input use <see cref="TryCreate"/>.
    /// </remarks>
    public static AgentName Create(string candidate) =>
        TryCreate(candidate, out var name)
            ? name
            : throw new ArgumentException(
                $"'{candidate}' is not a valid agent name. Expected lowercase slug matching " +
                $"^[a-z][a-z0-9-]{{0,{MaxLength - 1}}}$.",
                nameof(candidate));

    /// <summary>
    /// Compares this name with another using ordinal ordering.
    /// </summary>
    /// <param name="other">The name to compare against.</param>
    /// <returns>
    /// A negative value if this name sorts first, zero if the names are equal, and a
    /// positive value if this name sorts last.
    /// </returns>
    /// <remarks>
    /// <see cref="PairKey.For"/> depends on this ordering to produce the same key regardless
    /// of argument order, so the comparison must stay ordinal and stable.
    /// </remarks>
    public int CompareTo(AgentName other) => string.CompareOrdinal(_value, other._value);

    /// <summary>
    /// Determines whether this name equals another.
    /// </summary>
    /// <param name="other">The name to compare against.</param>
    /// <returns>
    /// <see langword="true"/> if both names hold identical text; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Equals(AgentName other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether this name equals another object.
    /// </summary>
    /// <param name="obj">The object to compare against.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="obj"/> is an <see cref="AgentName"/> holding
    /// identical text; otherwise <see langword="false"/>.
    /// </returns>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is AgentName other && Equals(other);

    /// <summary>
    /// Returns an ordinal hash code for this name.
    /// </summary>
    /// <returns>The hash code, or zero for an uninitialised instance.</returns>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>
    /// Returns the name text, or a placeholder for an uninitialised instance.
    /// </summary>
    /// <returns>
    /// The underlying slug, or <c>&lt;uninitialised&gt;</c> when the struct holds its
    /// default value. Unlike <see cref="Value"/> this never throws, so it stays safe to call
    /// from logging and diagnostics.
    /// </returns>
    public override string ToString() => _value ?? "<uninitialised>";

    /// <summary>Determines whether two names are equal.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if the names are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(AgentName left, AgentName right) => left.Equals(right);

    /// <summary>Determines whether two names differ.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if the names differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(AgentName left, AgentName right) => !left.Equals(right);

    /// <summary>Determines whether the left name sorts before the right.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if <paramref name="left"/> sorts first; otherwise <see langword="false"/>.</returns>
    public static bool operator <(AgentName left, AgentName right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether the left name sorts after the right.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if <paramref name="left"/> sorts last; otherwise <see langword="false"/>.</returns>
    public static bool operator >(AgentName left, AgentName right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether the left name sorts before the right, or is equal to it.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if <paramref name="left"/> does not sort last; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(AgentName left, AgentName right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether the left name sorts after the right, or is equal to it.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> if <paramref name="left"/> does not sort first; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(AgentName left, AgentName right) => left.CompareTo(right) >= 0;
}
