using Parley.Core;

namespace Parley.Configuration;

/// <summary>
/// The name a parley server sits under in a repository's launch configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository may answer in more than one project, so one repository holds one entry per
/// project.</b> SPEC.md §4.1: the protocol has no concept of a project, membership is which endpoint
/// an agent connected to, and names collide freely across projects. One key holds one entry, so each
/// project needs a key of its own.
/// </para>
/// <para>
/// <b>The key carries the prefix rather than being the bare project name, and that is a safety
/// property rather than a style.</b> Withdrawal is the only path in this tooling that deletes
/// something out of somebody's own file, and it must be able to decide from the key alone whether an
/// entry is one this program wrote. A bare project name would make that decision an inspection of what
/// is inside the entry, which risks silently replacing a person's own MCP configuration.
/// </para>
/// <para>
/// <b>The grammar is asked of <see cref="ProjectName"/> rather than repeated.</b> One rule with two
/// implementations is a rule that survives until somebody edits one of them.
/// </para>
/// <para>
/// <see cref="Unnamed"/> (<c>parley</c>) is the key for an enrolment that names no project: such an
/// enrolment has nothing to derive a key from. It is always read. It is never renamed in place,
/// because the key is a component of every tool name the agent behind it sees.
/// </para>
/// <para>
/// <b>It lives in <c>Parley.Configuration</c> rather than beside the enrolment that writes it</b>
/// because both the enrolment and the node's reader of each workspace need the same two spellings, and
/// this is the project both already reference. <c>KeyRuleTests</c> guards that it stays the one
/// implementation.
/// </para>
/// </remarks>
public static class LaunchKey
{
    /// <summary>The key an enrolment that names no project writes.</summary>
    public const string Unnamed = "parley";

    /// <summary>What a key derived from a project name begins with.</summary>
    public const string Prefix = Unnamed + "-";

    /// <summary>The key a project's entry sits under.</summary>
    /// <param name="project">The project, or null when the deployment names none.</param>
    /// <returns><see cref="Unnamed"/>, or the project's own key.</returns>
    /// <exception cref="ArgumentException"><paramref name="project"/> is not a project name.</exception>
    public static string For(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return Unnamed;
        }

        if (!ProjectName.IsValid(project))
        {
            throw new ArgumentException(
                $"'{project}' is not a project name, so it cannot name a launch configuration entry.",
                nameof(project));
        }

        return Prefix + project;
    }

    /// <summary>Whether a key is one this tooling writes.</summary>
    /// <param name="key">The name a server sits under.</param>
    /// <returns><see langword="true"/> when this tooling owns the key.</returns>
    /// <remarks>
    /// This answers <i>may something be written here</i>, which is a different question from whether
    /// what is already there is ours — an entry at a key we own may still have been written by hand.
    /// Conflating the two either refuses to clean up our own entry or deletes a stranger's.
    /// </remarks>
    public static bool IsOurs(string? key) =>
        string.Equals(key, Unnamed, StringComparison.Ordinal) || ProjectOf(key) is not null;

    /// <summary>The project a key names, when it names one.</summary>
    /// <param name="key">The name a server sits under.</param>
    /// <returns>The project, or null for <see cref="Unnamed"/> and for a key that is not ours.</returns>
    /// <remarks>
    /// Null means two different things and both are handled by the caller rather than distinguished
    /// here: an unnamed entry, whose project is in its environment if it has one at all, and a server
    /// belonging to something else. <see cref="IsOurs"/> is what separates them.
    /// </remarks>
    public static string? ProjectOf(string? key)
    {
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var project = key[Prefix.Length..];

        return ProjectName.IsValid(project) ? project : null;
    }
}
