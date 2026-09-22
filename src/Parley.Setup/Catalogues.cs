using Parley.Configuration;

namespace Parley.Setup;

/// <summary>
/// The machine's one catalogue: who may write to it, and the one enrolment it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>This held FR-013, and 018 deleted the rule rather than the file.</b> 009 allowed a repository to
/// answer in several projects and to be started unattended by only one, because a catalogue belonged to a
/// project and two of them consenting was two nodes launching one working directory — two models editing
/// one checkout, with no coordination and nobody there. Enforcing it meant <c>Elsewhere</c>, a scan across
/// every other project's catalogue, and 009 recorded the deviation from the constitution's fifth principle
/// that the scan was: two projects are two databases by design, so there was no schema either could hold
/// the constraint in.
/// </para>
/// <para>
/// <b>There is one catalogue now, keyed by the agent's identity, so "catalogued twice" cannot be written
/// down.</b> The rule is a property of the file's shape rather than a scan somebody has to remember to
/// run — which also removes the failure the scan kept having: three routes to "I could not read that
/// catalogue", each of which granted the second start in silence.
/// </para>
/// <para>
/// <b>What replaces it is a narrower refusal, and it is about a copy rather than a project</b> (018 R2).
/// One identifier is one startable thing, so a checkout copied to a second path carries an identifier
/// already catalogued against the first — and cataloguing it again would point one identity at two
/// working directories, which is the same hazard FR-013 was about, reached by the one route that is still
/// open. It is refused naming both paths, because the person who did it is the only one who knows which
/// of the two they meant.
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
    /// <b>It was a scan across every other project's catalogue whose caller then wrote a different file,
    /// so no amount of holding the file being written could make the pair atomic.</b> Two runs granting
    /// consent at the same moment each looked, saw nothing, and both wrote — found by a probe that passed
    /// six times in isolation and failed on the third round under the load of the full suite, which is the
    /// difference between a race being present and a race being visible.
    /// </para>
    /// <para>
    /// <b>018 deleted the scan and kept this</b>, which corrects 018's own research. There is one catalogue
    /// now, so the pair this could not make atomic no longer exists — but the read-decide-write it guards
    /// does: <see cref="Copied"/>'s refusal is decided from what the catalogue says a moment before the
    /// entry is written into it. The lock goes when 012's D6 gives the catalogue a single long-lived owner,
    /// and not before.
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
        if (manifestPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return null;
        }

        // **The manifest's own lock file, not the manifest.** The first version held the manifest
        // itself with `FileShare.None`, which locked out every *reader* for the length of a bulk
        // enrolment — the terminal, the web UI, the node and every operator command begin by reading
        // it, and each would have reported it unreadable rather than busy. `ProjectManifest` already
        // keeps a lock beside it for serialising its own writes, so this takes that one: readers are
        // untouched, and a grant and a manifest rewrite exclude each other, which they should.
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
