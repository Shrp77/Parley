using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>Where a machine has already consented to start a repository with nobody watching.</summary>
/// <param name="Project">The project whose catalogue names it.</param>
/// <param name="Catalogue">That catalogue's path.</param>
/// <param name="Role">The role it is catalogued under there.</param>
public sealed record Consent(string Project, string Catalogue, string Role);

/// <summary>
/// The one asymmetry of this feature: many launch entries, at most one unattended start.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository may answer in several projects and may be started unattended by only one.</b> The
/// two halves are not the same risk. A launch entry does nothing at all until a person opens the
/// repository in an editor, and a person opens one; a catalogue entry is standing consent for a
/// machine to start a turn with nobody watching, and <c>ResidentAgent</c> starts one with its
/// workspace as its working directory. Two nodes acting on that is two models editing one working
/// tree, with no coordination and nobody there.
/// </para>
/// <para>
/// <b>Checked here rather than in <c>ProjectManifest.Validate</c>, which is where it looks like it
/// belongs.</b> Validation is a pure function of the manifest and a workspace is not in the manifest —
/// it is in each project's catalogue, a separate file per project. <c>ProjectManifest</c> already says
/// why no store can carry this: <i>"the one thing SPEC.md §4.1's isolation cannot supply from storage
/// because the catalogue is not in the store."</i> Two projects are two databases by design, so there
/// is no schema either could hold the constraint in, and the constitution's fifth principle is
/// deviated from here with that argument written down rather than assumed.
/// </para>
/// <para>
/// <b>The comparison is <see cref="WorkspacePath.SameRepository"/> and must be.</b> It is what
/// SPEC.md §4.1.1 defines, it is textual, and it is explicitly forbidden from consulting a filesystem.
/// A second comparison written here would be a second answer to one question, and the two would
/// disagree first about the case that matters — the same checkout spelled two ways.
/// </para>
/// </remarks>
public static class Catalogues
{
    /// <summary>Takes the machine-wide gate on granting an unattended start.</summary>
    /// <param name="manifestPath">The machine's project manifest, or null when it has none.</param>
    /// <param name="cancellationToken">Stops the waiting.</param>
    /// <returns>The gate, held until it is disposed, or null when there is nothing to gate.</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="Elsewhere"/> reads every other project's catalogue and its caller then writes a
    /// different file, so no amount of holding the file being written can make the pair atomic.</b>
    /// Two runs granting consent at the same moment each looked, saw nothing, and both wrote — found
    /// by a probe that passed six times in isolation and failed on the third round under the load of
    /// the full suite, which is the difference between a race being present and a race being visible.
    /// </para>
    /// <para>
    /// <b>The manifest is what is held, because it is the one file every project on the machine
    /// shares</b> — and it is never written here, only taken. Where there is no manifest there is
    /// also no <c>deployment</c> to compare against, so the check is a no-op and the gate is nothing
    /// to hold.
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

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException) when (attempt < 40 && File.Exists(path))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether another project on this machine already starts this workspace.</summary>
    /// <param name="deployment">Every project the machine's manifest names.</param>
    /// <param name="workspace">The repository about to be catalogued.</param>
    /// <param name="catalogue">The catalogue about to be written, which is not consulted.</param>
    /// <returns>The consent that already exists, or null when there is none.</returns>
    /// <remarks>
    /// A catalogue that cannot be read is skipped rather than treated as empty. Treating it as empty
    /// would let a second consent through on exactly the machine whose configuration nobody can
    /// currently see; skipping it means this says nothing about a file it could not read, which is
    /// the honest half of the two.
    /// </remarks>
    public static Consent? Elsewhere(
        IReadOnlyList<ProjectSettings>? deployment,
        string workspace,
        string? catalogue)
    {
        if (deployment is null || string.IsNullOrWhiteSpace(workspace))
        {
            return null;
        }

        foreach (var project in deployment)
        {
            if (project.NodeConfig is not { Length: > 0 } path
                || Same(path, catalogue)
                || !NodeCatalogue.TryReadFrom(path, out var read, out _))
            {
                continue;
            }

            foreach (var role in read.Roles)
            {
                // **A role no agent name can hold is skipped, and that is the rule rather than a
                // gap.** It looks like a blind spot — a hand-edited catalogue hiding its workspace
                // from this check — and a probe was written expecting exactly that. It is not:
                // FR-013 exists because two nodes *starting* one working tree is two models editing
                // it, and such an entry is unstartable by construction, since
                // `NodesService.RequestStart` compares against `agent.Role` of a parsed `AgentName`.
                // Refusing a second project on the strength of a line that can never be acted on
                // would be the wrong answer, not a safer one.
                if (!AgentName.TryCreate($"{role}@{NodeCatalogue.Machine}", out var agent))
                {
                    continue;
                }

                if (WorkspacePath.SameRepository(read.WorkspaceFor(agent), workspace))
                {
                    return new Consent(project.Name.Value, path, role);
                }
            }
        }

        return null;
    }

    /// <summary>What to tell somebody whose repository is already started elsewhere.</summary>
    /// <param name="consent">The consent that already exists.</param>
    /// <returns>One sentence, naming both projects' worth of detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="consent"/> is null.</exception>
    public static string Refusal(Consent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);

        return $"'{consent.Project}' already starts this workspace unattended, as '{consent.Role}' in "
            + $"{consent.Catalogue}. A repository may answer in several projects and may be started "
            + "unattended by only one: a node starts an agent with its workspace as the working "
            + "directory, so two of them is two models editing one checkout.";
    }

    /// <summary>Whether two catalogue paths are the same file.</summary>
    private static bool Same(string left, string? right) =>
        right is not null
        && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
