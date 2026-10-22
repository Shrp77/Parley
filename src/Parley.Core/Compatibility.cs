using System.Globalization;
using System.Reflection;

namespace Parley.Core;

/// <summary>
/// Whether two builds of Parley can be used together, and what a build is called.
/// </summary>
/// <remarks>
/// <para>
/// <b>The major and the minor, and nothing finer.</b> While the major is zero the minor carries breaking changes and the
/// patch carries everything else (CLAUDE.md, Versioning), so two builds agree exactly when those two numbers do. A
/// patch difference is a fix that has not reached every machine, which is worth knowing and not worth a warning.
/// </para>
/// <para>
/// This is the build's own number and not the protocol's. The protocol is what an agent's adapter announces and is
/// refused on at attach; this is the question the protocol cannot answer, which is whether the programs a person
/// installed are all from one release.
/// </para>
/// </remarks>
public static class Compatibility
{
    /// <summary>The build an assembly belongs to, without the source-control suffix.</summary>
    /// <param name="assembly">Any assembly of this repository; they share one version.</param>
    /// <returns>The version, or <c>unknown</c> when the assembly carries none.</returns>
    public static string VersionOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString();

        if (string.IsNullOrEmpty(informational))
        {
            return "unknown";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? informational : informational[..plus];
    }

    /// <summary>Whether two builds are from one release family.</summary>
    /// <param name="a">One version, such as <c>0.42.1</c>.</param>
    /// <param name="b">The other.</param>
    /// <returns>
    /// Whether the major and minor agree. A version that cannot be read agrees with nothing, because a
    /// build that cannot say what it is has not been shown to match.
    /// </returns>
    public static bool Agree(string? a, string? b) =>
        Family(a) is { } left && Family(b) is { } right && string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>The major and minor of a version.</summary>
    /// <param name="version">A version, such as <c>0.42.1</c> or <c>0.42.1-beta</c>.</param>
    /// <returns>For example <c>0.42</c>, or null when the text is not a version.</returns>
    public static string? Family(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split('.', 3);

        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? parts[0] + "." + parts[1]
            : null;
    }
}
