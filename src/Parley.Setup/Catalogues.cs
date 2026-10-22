using Parley.Configuration;

namespace Parley.Setup;

/// <summary>
/// The machine's one catalogue: who may write to it, and the one enrolment it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is one catalogue, keyed by the agent's identity, so "catalogued twice" cannot be written
/// down.</b> The rule is a property of the file's shape, not a scan across projects.
/// </para>
/// <para>
/// <b>The one refusal is about a copy, not a project.</b> One identifier is one startable thing, so a
/// checkout copied to a second path carries an identifier already catalogued against the first, and
/// cataloguing it again would point one identity at two working directories (two models editing one
/// checkout). It is refused naming both paths, because the person who did it is the only one who knows
/// which of the two they meant.
/// </para>
/// </remarks>
internal static class Catalogues
{
    /// <summary>Takes the machine-wide gate on writing the catalogue.</summary>
    /// <param name="manifestPath">The machine's project manifest, or null when it has none.</param>
    /// <param name="cancellationToken">Stops the waiting.</param>
    /// <returns>The gate, held until it is disposed, or null when there is nothing to gate.</returns>
    /// <remarks>
    /// <para>
    /// <b>It guards a read-decide-write.</b> <see cref="Copied"/>'s refusal is decided from what the
    /// catalogue says a moment before the entry is written into it, so two runs must not interleave.
    /// The lock goes when the catalogue has a single long-lived owner, and not before.
    /// </para>
    /// <para>
    /// <b>The manifest is what is held, because it is the one file every project on the machine
    /// shares</b> — and it is never written here, only taken.
    /// </para>
    /// </remarks>
    public static async Task<IDisposable?> GateAsync(
        string? manifestPath,
        CancellationToken cancellationToken)
    {
        // Directory.Exists, because the argument is the root: `File.Exists` on a directory is false, so
        // the gate would silently never be taken. A guard that fails open is worse than no guard.
        if (manifestPath is not { Length: > 0 } path || !Directory.Exists(path))
        {
            return null;
        }

        // The projects' own lock file, not any configuration file: holding a configuration file with
        // `FileShare.None` would lock out every reader for the length of a bulk enrolment.
        // `ProjectManifest` already keeps a lock for serialising its own writes, so this takes that one:
        // readers are untouched, and an enrolment and a project rewrite exclude each other.
        var lockPath = ProjectManifest.LockFor(path);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 40)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What to tell somebody enrolling a copy of a checkout this machine already starts.</summary>
    /// <param name="catalogued">Where the catalogue says that identity lives.</param>
    /// <param name="offered">Where this enrolment says it lives.</param>
    /// <returns>One sentence, naming both paths and what to do about it.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both paths, because neither this tooling nor anybody reading the message can tell which one was
    /// meant.</b> A moved checkout and a copied one are the same two facts from here — an identifier
    /// catalogued at one path, offered at another — and they want opposite repairs. Naming one would be
    /// picking, and picking wrong silently repoints a machine's only way of starting that agent.
    /// </para>
    /// <para>
    /// <b>Both values came out of a file, and this is read in a terminal</b>, so both go through
    /// <see cref="Quoting.Flat"/>. That matters more here than in most places, because the line around it
    /// is a refusal — which is what somebody reads before deciding whether a path is safe to point a tool
    /// at. This repository has fixed the shape twice before: the transcript's column-zero rule, and the
    /// destroy dialog quoting a manifest's endpoint raw.
    /// </para>
    /// </remarks>
    public static string Copied(string? catalogued, string? offered) =>
        $"this machine already starts that agent from {Quoting.Flat(catalogued)}, and this enrolment "
        + $"offers {Quoting.Flat(offered)}. One agent is one checkout, so the two cannot both be it: if "
        + "the repository moved, repoint the catalogue entry; if this is a copy, it is a different agent "
        + "and needs its own identity.";
}
