using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Parley.Core;

/// <summary>
/// The name of a project: one independent deployment on a machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>The protocol never sees one.</b> SPEC.md §4.1 is explicit that the protocol has no concept
/// of a project and must not acquire one — membership is which endpoint an agent connected to,
/// and a broker's connection cannot see another's file. A project name exists so that a person,
/// and the tools a person runs, can say <i>which deployment</i> they mean. It is never sent, never
/// stored, and never compared against anything a peer supplied.
/// </para>
/// <para>
/// <b>Deliberately the same grammar as an agent's role</b> — lowercase letters, digits and
/// hyphens, starting with a letter, at most 32 characters. Not because anything requires it, but
/// because an operator who has learned one naming rule should not have to learn a second that
/// differs in some detail they will discover by being refused.
/// </para>
/// <para>
/// Names collide freely <i>across</i> projects and must remain unique <i>within</i> a manifest.
/// Two projects may each contain an <c>orders</c> agent, which is the point: a single shared
/// registry would have forced a global naming convention across unrelated codebases.
/// </para>
/// </remarks>
public readonly partial struct ProjectName : IEquatable<ProjectName>, IComparable<ProjectName>
{
    /// <summary>The longest a project name may be.</summary>
    public const int MaxLength = 32;

    private readonly string? _value;

    private ProjectName(string value) => _value = value;

    /// <summary>The name as written.</summary>
    /// <exception cref="InvalidOperationException">The value was never created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("Uninitialised ProjectName.");

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern { get; }

    /// <summary>
    /// Says whether a candidate is a well-formed project name.
    /// </summary>
    /// <param name="candidate">The text to check.</param>
    /// <returns><see langword="true"/> when it is well formed.</returns>
    public static bool IsValid([NotNullWhen(true)] string? candidate) =>
        candidate is not null && Pattern.IsMatch(candidate);

    /// <summary>
    /// Creates a project name if the candidate is well formed.
    /// </summary>
    /// <param name="candidate">The text to check.</param>
    /// <param name="name">The name, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the candidate was accepted.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? candidate, out ProjectName name)
    {
        if (!IsValid(candidate))
        {
            name = default;
            return false;
        }

        name = new ProjectName(candidate);
        return true;
    }

    /// <summary>
    /// Creates a project name, or throws.
    /// </summary>
    /// <param name="candidate">The text to check.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentException">The candidate is not well formed.</exception>
    public static ProjectName Create(string candidate) =>
        TryCreate(candidate, out var name)
            ? name
            : throw new ArgumentException(
                $"'{candidate}' is not a project name. Names are lowercase letters, digits and " +
                $"hyphens, starting with a letter, at most {MaxLength} characters.",
                nameof(candidate));

    /// <inheritdoc/>
    public bool Equals(ProjectName other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ProjectName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc/>
    public int CompareTo(ProjectName other) => string.CompareOrdinal(_value, other._value);

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;

    /// <summary>Says whether two names are the same.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(ProjectName left, ProjectName right) => left.Equals(right);

    /// <summary>Says whether two names differ.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(ProjectName left, ProjectName right) => !left.Equals(right);

    /// <summary>Orders one name before another.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the first sorts first.</returns>
    public static bool operator <(ProjectName left, ProjectName right) => left.CompareTo(right) < 0;

    /// <summary>Orders one name after another.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the first sorts last.</returns>
    public static bool operator >(ProjectName left, ProjectName right) => left.CompareTo(right) > 0;

    /// <summary>Orders one name before another, or equal.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the first does not sort last.</returns>
    public static bool operator <=(ProjectName left, ProjectName right) => left.CompareTo(right) <= 0;

    /// <summary>Orders one name after another, or equal.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the first does not sort first.</returns>
    public static bool operator >=(ProjectName left, ProjectName right) => left.CompareTo(right) >= 0;
}
