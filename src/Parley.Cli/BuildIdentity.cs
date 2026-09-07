using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Parley.Core;
using Parley.Store;

namespace Parley.Cli;

/// <summary>
/// What this program is made of: the build, the commit it came from, the protocol it speaks, the
/// schema it migrates a store to, and the runtime underneath.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four different questions, and a log that names none of them cannot answer any.</b> The
/// build says which package is on PATH; the commit says which build, because a branch is packed
/// more than once and the number does not move for a fix; the protocol says what another
/// implementation must speak, and it is deliberately not the package version — SPEC.md is the
/// contract, so "which Parley is this" cannot be answered by a number that moves for a typo in a
/// help string; the schema says what shape this build expects a store to be in.
/// </para>
/// <para>
/// <b>The schema belongs on the same line as the rest even though every store line repeats
/// it</b>, because the case this exists for is the one where no store line is ever printed. A
/// store written by a newer build refuses to open with "this store is at schema 5 and this Parley
/// only knows 4", and the reader's next question is which Parley that was.
/// </para>
/// <para>
/// <b>Why this is not what <c>--version</c> prints.</b> That output is a number a person compares
/// against another number, and a forty-character suffix on it is noise. A log line is read months
/// later by somebody deciding whether a defect is still present in what they are running, and the
/// number alone cannot settle that. Both come from here, so they cannot disagree about the part
/// they share.
/// </para>
/// </remarks>
internal static class BuildIdentity
{
    /// <summary>The package version, without the source-control suffix.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The commit this was built from, shortened, or <see langword="null"/> when the build
    /// carried none.
    /// </summary>
    /// <remarks>
    /// Absent from a build made outside source control, which is a fact about that build rather
    /// than an error: the line says less, and says it truthfully, instead of inventing an
    /// identifier that would send somebody to a commit that does not exist.
    /// </remarks>
    public static string? Commit { get; } = ReadCommit();

    /// <summary>The one line naming every version involved in running this.</summary>
    public static string Line { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"parley {Version}{(Commit is null ? "" : $" ({Commit})")}, " +
        $"protocol {ProtocolVersion.Current}, schema {SchemaMigrator.LatestVersion}, " +
        $"on {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}).");

    /// <summary>Reads the informational version and drops any suffix.</summary>
    /// <returns>The version, or <c>unknown</c> when the assembly carries none.</returns>
    private static string ReadVersion()
    {
        var build = Informational();

        if (build is null)
        {
            return "unknown";
        }

        var plus = build.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? build : build[..plus];
    }

    /// <summary>Reads the commit out of the informational version's suffix.</summary>
    /// <returns>The first seven characters of it, or <see langword="null"/>.</returns>
    private static string? ReadCommit()
    {
        var build = Informational();
        var plus = build?.IndexOf('+', StringComparison.Ordinal) ?? -1;

        if (build is null || plus < 0)
        {
            return null;
        }

        var revision = build[(plus + 1)..];

        // Seven, the length git itself abbreviates to, and only when there is that much to take.
        // A suffix that is not a hash -- some builds put a branch name there -- is left whole
        // rather than cut in the middle, because half a word identifies nothing.
        return revision.Length >= 40 ? revision[..7] : revision;
    }

    /// <summary>The raw informational version, suffix and all.</summary>
    /// <returns>What the assembly declares, or <see langword="null"/>.</returns>
    private static string? Informational() =>
        typeof(BuildIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
