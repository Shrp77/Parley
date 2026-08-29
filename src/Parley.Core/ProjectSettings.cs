namespace Parley.Core;

/// <summary>
/// One project: an independent deployment on a machine.
/// </summary>
/// <param name="Name">What a person calls it.</param>
/// <param name="Store">The database file this project's broker opens.</param>
/// <param name="Endpoint">The transport address that broker listens on.</param>
/// <param name="NodeConfig">This project's node catalogue, or null when this machine starts nothing for it.</param>
/// <param name="Description">One line for the operator's own benefit, or null.</param>
/// <remarks>
/// <para>
/// <b>A project is a deployment concept and never a protocol one.</b> SPEC.md §4.1: the protocol
/// has no notion of a project and must not acquire one. Nothing here is sent, stored in the
/// broker's database, or compared against anything a peer supplied — it exists so a person can
/// say which deployment they mean. Isolation is a property of <see cref="Store"/>, not of
/// <see cref="Name"/>: two projects are separate because their brokers open different files and
/// a connection cannot see another's.
/// </para>
/// <para>
/// <b>Validation here is deliberately partial, and the missing half is not an oversight.</b>
/// Whether <see cref="Store"/> is an absolute path is a question about a filesystem, and this
/// assembly has no I/O and no path handling — the same constraint that keeps
/// <see cref="WorkspacePath"/> purely syntactic. So this type validates what can be decided from
/// the values alone, which is also exactly what an adapter needs: a name it can look up and an
/// endpoint it can dial. The path rules belong to the tool that reads and writes the file, which
/// is on the machine those paths describe and may legitimately ask the filesystem about them.
/// </para>
/// </remarks>
public sealed record ProjectSettings(
    ProjectName Name,
    string Store,
    string Endpoint,
    string? NodeConfig,
    string? Description)
{
    /// <summary>
    /// Says what is wrong with this project, judged without touching a filesystem.
    /// </summary>
    /// <returns>One line per problem, empty when nothing is decidable-and-wrong.</returns>
    /// <remarks>
    /// Returns a list rather than throwing on the first fault, because a manifest is validated
    /// whole: somebody fixing one is better served by every problem at once than by discovering
    /// them one run at a time.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Store))
        {
            problems.Add("store is required: it names the database file this project's broker opens");
        }

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            problems.Add("endpoint is required: it is where this project's broker listens");
        }
        else if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            // Refused when the manifest is read rather than when a connection is attempted, so
            // the message arrives while the operator is looking at the file that caused it.
            problems.Add($"endpoint '{Endpoint}' is not an absolute address");
        }

        if (NodeConfig is { Length: 0 })
        {
            // Absent and empty mean different things: absent is "this machine starts nothing for
            // this project", which is a legitimate state. Empty is a field somebody began and
            // did not finish, and reading it as absent would hide that.
            problems.Add("node_config is present but empty: omit it, or give it a path");
        }

        if (Description is { Length: > MaxDescription })
        {
            problems.Add(
                $"description is {Description.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"characters; keep it to {MaxDescription.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        foreach (var (field, value) in new[]
                 {
                     ("store", Store),
                     ("endpoint", Endpoint),
                     ("node_config", NodeConfig),
                     ("description", Description),
                 })
        {
            if (value is not null && value.Any(char.IsControl))
            {
                problems.Add($"{field} contains a control character; remove it");
            }
        }

        return problems;
    }

    /// <summary>How long a description may be.</summary>
    /// <remarks>
    /// <para>
    /// One line, because the only place it is read is a listing where each project occupies a few
    /// lines and a paragraph would bury the paths beside it. The number is arbitrary in the way
    /// the message cap is not — nothing turns on it, and it is here so that the field cannot
    /// quietly become a place to keep notes that belong in a repository.
    /// </para>
    /// <para>
    /// Refusing control characters matters more than the length. A description is free text
    /// rendered straight to a terminal, so a newline in one lets a project's entry produce lines
    /// that look like another project's entry, and an escape sequence lets it produce anything at
    /// all. The listing is what an operator consults to find out what this machine is configured
    /// for; a field in it that can forge the rest of the report is worth closing while the field
    /// is new, whoever is expected to be writing it.
    /// </para>
    /// </remarks>
    public const int MaxDescription = 120;
}
