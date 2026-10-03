using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Parley.Core;

/// <summary>
/// A validated agent identifier: a role and the machine it runs on, such as
/// <c>orders@server-b</c>.
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
/// <b>Two parts, because one was not enough.</b> The same repository checked out on five
/// machines gives five agents, and under a single-token grammar they collided into one
/// registry row and silently shared an identity. The role says what the agent is for; the
/// machine says which one is being addressed. See SPEC.md §4.1.1.
/// </para>
/// <para>
/// The machine half is detected by the node that reports the agent, never configured, because
/// a machine that can be told what it is called can be told the wrong thing — and two boxes
/// answering to the same name is a failure that shows up as messages arriving somewhere
/// nobody expected, long after the mistake.
/// </para>
/// <para>
/// The grammar is <c>^[a-z][a-z0-9-]{0,31}@[a-z0-9][a-z0-9.-]{0,63}$</c>. Narrow on purpose:
/// names appear inside <see cref="PairKey"/> values, log lines, and adapter tool calls, so
/// excluding whitespace, case variation, the pair separator, and a second <c>@</c> removes
/// whole categories of ambiguity rather than handling them. A dot is admitted in the machine
/// half alone, because a fully qualified host name is a reasonable thing for a node to detect.
/// </para>
/// <para>
/// All comparison is ordinal. Culture-sensitive comparison of a value used in a lock key
/// would be a correctness bug, not a formatting preference.
/// </para>
/// </remarks>
public readonly partial struct AgentName : IEquatable<AgentName>, IComparable<AgentName>
{
    /// <summary>
    /// The maximum number of characters permitted in the role half of a name.
    /// </summary>
    public const int MaxRoleLength = 32;

    /// <summary>
    /// The maximum number of characters permitted in the machine half of a name.
    /// </summary>
    /// <remarks>
    /// Longer than the role because it is detected rather than chosen, and a fully qualified
    /// host name is a reasonable thing for a node to find itself called.
    /// </remarks>
    public const int MaxMachineLength = 64;

    /// <summary>
    /// The maximum number of characters permitted in a whole name.
    /// </summary>
    public const int MaxLength = MaxRoleLength + 1 + MaxMachineLength;

    /// <summary>The character separating the role from the machine.</summary>
    public const char Separator = '@';

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

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}@[a-z0-9][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern { get; }

    /// <summary>The grammar a role half must match on its own.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern { get; }

    /// <summary>The grammar a machine half must match on its own.</summary>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MachinePattern { get; }

    /// <summary>
    /// Gets the role half: what this agent is for.
    /// </summary>
    /// <value>The text before the separator, for example <c>orders</c>.</value>
    /// <exception cref="InvalidOperationException">The instance is the default value.</exception>
    /// <remarks>
    /// A capability rather than a scope. Five agents sharing a role own the same thing and can
    /// execute different parts of it, which is why <c>owns</c> stays per agent — see §4.1.1.
    /// </remarks>
    public string Role => Value[..Value.IndexOf(Separator, StringComparison.Ordinal)];

    /// <summary>
    /// Gets the machine half: which one is being addressed.
    /// </summary>
    /// <value>The text after the separator, for example <c>server-b</c>.</value>
    /// <exception cref="InvalidOperationException">The instance is the default value.</exception>
    public string Machine => Value[(Value.IndexOf(Separator, StringComparison.Ordinal) + 1)..];

    /// <summary>
    /// Whether a role, on its own, could name a group of agents.
    /// </summary>
    /// <param name="candidate">The text to check.</param>
    /// <returns><see langword="true"/> if it is a well-formed role.</returns>
    /// <remarks>
    /// Used to tell <c>infra</c> from <c>infra@server-b</c> at the point a question is addressed:
    /// the first opens one exchange per registered agent under that role (§5.5), the second
    /// opens one. Deliberately not a method that guesses — a caller asks which shape it has and
    /// gets a straight answer, because "it had no @ so we treated it as a group" is the sort of
    /// inference that sends a question to nineteen machines by accident.
    /// </remarks>
    public static bool IsRole([NotNullWhen(true)] string? candidate) =>
        candidate is not null && RolePattern.IsMatch(candidate);

    /// <summary>
    /// Builds a name from its two halves.
    /// </summary>
    /// <param name="role">What the agent is for.</param>
    /// <param name="machine">The machine reporting it.</param>
    /// <param name="name">The validated name, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if both halves are well formed.</returns>
    public static bool TryJoin(string? role, string? machine, out AgentName name)
    {
        if (role is not null && machine is not null
            && RolePattern.IsMatch(role) && MachinePattern.IsMatch(machine))
        {
            name = new AgentName(role + Separator + machine);
            return true;
        }

        name = default;
        return false;
    }

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
            // The message names the grammar TryCreate actually enforces, which it did not: it
            // described a bare lowercase slug of up to 97 characters, and a bare slug is exactly
            // what this type refuses. A name is role@machine (§4.1), so somebody following the
            // old message to the letter produced another value that failed for the same reason
            // and was told the same thing again.
            : throw new ArgumentException(
                $"'{candidate}' is not a valid agent name. Expected role@machine, matching " +
                $"^[a-z][a-z0-9-]{{0,{MaxRoleLength - 1}}}@[a-z0-9][a-z0-9.-]"
                + $"{{0,{MaxMachineLength - 1}}}$.",
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
