using System.Diagnostics.CodeAnalysis;

namespace Parley.Core;

/// <summary>
/// The serialisation key identifying an unordered pair of agents. All traffic between two
/// agents is queued against this key, so that at most one exchange between them is open at
/// any time.
/// </summary>
/// <remarks>
/// <para>
/// The key is unordered: <c>PairKey.For(a, b)</c> and <c>PairKey.For(b, a)</c> produce the
/// same value. This is what makes the two-party deadlock impossible — if
/// <c>orders</c> asks <c>billing</c> while <c>billing</c> is asking <c>orders</c>, both
/// attempts contend for one key and the second is refused rather than blocking.
/// </para>
/// <para>
/// The construction is ordinal sort-and-concatenate with a separator. Multiplying or
/// XOR-ing the two names' hashes would be shorter and would collide; a collision here
/// silently serialises two unrelated pairs, or lets a genuine pair through. That is a
/// correctness bug rather than a performance one, so the key stays lossless and reversible.
/// </para>
/// <para>
/// The separator is excluded from the <see cref="AgentName"/> grammar, which is what makes
/// the concatenation unambiguous: without it, the pairs <c>("a", "bc")</c> and
/// <c>("ab", "c")</c> would produce the same key.
/// </para>
/// </remarks>
public readonly struct PairKey : IEquatable<PairKey>
{
    /// <summary>
    /// The character separating the two names within a key: ASCII Unit Separator
    /// (U+001F).
    /// </summary>
    /// <remarks>
    /// Chosen because it cannot appear in a valid <see cref="AgentName"/>, guaranteeing the
    /// concatenation is unambiguous.
    /// </remarks>
    public const char Separator = '\u001F';

    private readonly string _value;

    private PairKey(string value) => _value = value;

    /// <summary>
    /// Gets the underlying key text, in the form suitable for storage.
    /// </summary>
    /// <value>
    /// The two agent names in ordinal order, joined by <see cref="Separator"/>.
    /// </value>
    /// <exception cref="InvalidOperationException">
    /// The instance is the default value of the struct and was never created through
    /// <see cref="For"/> or <see cref="FromStorage"/>.
    /// </exception>
    public string Value => _value ?? throw new InvalidOperationException("Uninitialised PairKey.");

    /// <summary>
    /// Derives the pair key for two agents, independent of the order they are supplied in.
    /// </summary>
    /// <param name="a">One participant.</param>
    /// <param name="b">The other participant.</param>
    /// <returns>The key identifying this pair.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="a"/> and <paramref name="b"/> are the same agent. An agent cannot
    /// pair with itself; a Request to oneself has no receiver.
    /// </exception>
    public static PairKey For(AgentName a, AgentName b)
    {
        if (a == b)
        {
            throw new ArgumentException($"An agent cannot pair with itself ('{a}').", nameof(b));
        }

        var (low, high) = a < b ? (a, b) : (b, a);
        return new PairKey(string.Concat(low.Value, Separator, high.Value));
    }

    /// <summary>
    /// Rehydrates a key previously read from storage, without re-deriving it from names.
    /// </summary>
    /// <param name="value">The stored key text.</param>
    /// <returns>The rehydrated key.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, or is not a well-formed key containing exactly one
    /// separator in a non-leading position.
    /// </exception>
    /// <remarks>
    /// This validates shape only, not that the constituent names are still registered
    /// agents. That is the registry's concern and is enforced by foreign keys in the schema.
    /// </remarks>
    public static PairKey FromStorage(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        var separatorIndex = value.IndexOf(Separator);
        if (separatorIndex <= 0 || separatorIndex != value.LastIndexOf(Separator))
        {
            throw new ArgumentException($"Malformed pair key: '{value}'.", nameof(value));
        }

        return new PairKey(value);
    }

    /// <summary>
    /// Determines whether this key equals another.
    /// </summary>
    /// <param name="other">The key to compare against.</param>
    /// <returns>
    /// <see langword="true"/> if both keys hold identical text; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Equals(PairKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether this key equals another object.
    /// </summary>
    /// <param name="obj">The object to compare against.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="obj"/> is a <see cref="PairKey"/> holding
    /// identical text; otherwise <see langword="false"/>.
    /// </returns>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is PairKey other && Equals(other);

    /// <summary>
    /// Returns an ordinal hash code for this key.
    /// </summary>
    /// <returns>The hash code, or zero for an uninitialised instance.</returns>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>
    /// Returns a human-readable form of the key for logging and diagnostics.
    /// </summary>
    /// <returns>
    /// The two names joined by a pipe, for example <c>billing|orders</c>. The unprintable
    /// separator is substituted so the value survives a log file legibly. Use
    /// <see cref="Value"/> for storage and comparison.
    /// </returns>
    public override string ToString() => _value?.Replace(Separator, '|') ?? "<uninitialised>";

    /// <summary>Determines whether two keys are equal.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns><see langword="true"/> if the keys are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(PairKey left, PairKey right) => left.Equals(right);

    /// <summary>Determines whether two keys differ.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns><see langword="true"/> if the keys differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(PairKey left, PairKey right) => !left.Equals(right);
}
