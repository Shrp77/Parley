using Parley.Configuration;
using Parley.Core;

namespace Parley.Setup;

/// <summary>Where a resolved value came from, so a command can say why it is about to act.</summary>
public enum ValueSource
{
    /// <summary>Given on the command line.</summary>
    Argument,

    /// <summary>Read from an environment variable.</summary>
    Environment,

    /// <summary>Supplied by the selected project.</summary>
    Project,

    /// <summary>Nothing supplied one, so the built-in default.</summary>
    Default,
}

/// <summary>One resolved value and where it came from.</summary>
/// <param name="Value">The value, or null when nothing supplied one.</param>
/// <param name="Source">Which input won.</param>
/// <param name="Detail">The project's name when <see cref="Source"/> is a project, else null.</param>
public sealed record Resolved(string? Value, ValueSource Source, string? Detail = null)
{
    /// <summary>Describes the value and its origin, for a command about to write.</summary>
    /// <returns>Something like <c>C:/x/node.json   (project "orders")</c>.</returns>
    public override string ToString() => Source switch
    {
        ValueSource.Project => $"{Value}   (project \"{Detail}\")",
        ValueSource.Argument => $"{Value}   (given)",
        ValueSource.Environment => $"{Value}   ({Detail})",
        _ => $"{Value}   (default)",
    };
}

/// <summary>
/// Decides which store, endpoint and node catalogue a command will use.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pure function, separated from <see cref="ProjectManifest"/> on purpose.</b> Precedence is
/// where the defects will be, and it can only be tested exhaustively if it does not need a
/// filesystem — the same reason <c>AutostartPlan</c> is separate from <c>AutostartCommand</c> and
/// <c>EnrollmentPlan</c> from <c>EnrollCommand</c>. Reading files is the caller's job; choosing
/// between what they found is this.
/// </para>
/// <para>
/// <b>A project is consulted only where the default would have applied</b>, so a machine with no
/// project resolves to the defaults, including the working-directory fallback for the node
/// catalogue.
/// </para>
/// <para>
/// <b>It never guesses which project was meant.</b> A project is named, set for the terminal, named by the repository a
/// command runs in, or the only one — and several configured with nothing of those choosing is a
/// refusal, not a choice, and a named project the manifest does not describe is a refusal rather
/// than a fall-back. Falling back there would connect to a different deployment — plausibly a
/// working one, silently wrong, and invisible from both ends of a message.
/// </para>
/// </remarks>
public static class ProjectResolution
{
    /// <summary>
    /// Chooses the project a command should act on.
    /// </summary>
    /// <param name="requested">The name asked for, or null.</param>
    /// <param name="projects">What the manifest described.</param>
    /// <param name="project">The chosen project, when this returns true. May be null when none applies.</param>
    /// <param name="problem">Why no choice could be made, when this returns false.</param>
    /// <returns><see langword="true"/> when a project was chosen, or none was needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    public static bool TrySelect(
        string? requested,
        IReadOnlyList<ProjectSettings> projects,
        out ProjectSettings? project,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(projects);

        project = null;
        problem = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            project = projects.FirstOrDefault(p =>
                string.Equals(p.Name.Value, requested, StringComparison.Ordinal));

            if (project is not null)
            {
                return true;
            }

            problem = projects.Count == 0
                ? $"No project named '{requested}' — this machine has none configured. " +
                  "Name one in `parley`."
                : $"No project named '{requested}'. Configured: {Names(projects)}.";

            return false;
        }

        if (projects.Count == 0)
        {
            // Nothing configured. Every value falls back to what it was before, and nothing is
            // reported: a tool that mentions a missing optional file on every run has made it
            // mandatory in practice.
            return true;
        }

        if (projects.Count == 1)
        {
            project = projects[0];
            return true;
        }

        problem =
            $"This machine has {projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"projects configured and none was named: {Names(projects)}. " +
            "Name one with --project.";

        return false;
    }

    /// <summary>
    /// Chooses the project a command should act on from its context, and says what chose it.
    /// </summary>
    /// <param name="requested">The name given with <c>--project</c>, or null.</param>
    /// <param name="fromVariable">The terminal's <c>PARLEY_PROJECT</c>, or null.</param>
    /// <param name="fromRepository">The projects the repository the command runs in names; see <see cref="RepositoryProjects"/>.</param>
    /// <param name="projects">What the manifest described.</param>
    /// <param name="project">The chosen project, when this returns true. May be null when none applies.</param>
    /// <param name="source">What chose it, as a phrase, when one was chosen.</param>
    /// <param name="problem">Why no choice could be made, when this returns false.</param>
    /// <returns><see langword="true"/> when a project was chosen, or none was needed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fromRepository"/> or <paramref name="projects"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>In this order: <c>--project</c>, the terminal's <c>PARLEY_PROJECT</c>, the repository, the only project.</b> A
    /// saved "current project" was offered and declined: chosen in one window yesterday, it would quietly apply to a purge
    /// in another today. A terminal's choice ends with the terminal, and a repository names what it is.
    /// </para>
    /// <para>
    /// <b>A context that names a project this machine does not describe is not silently passed over.</b> A terminal's
    /// variable naming an unknown project is refused, because somebody set it and would otherwise act somewhere else. A
    /// repository naming only projects that live elsewhere falls through to the rest of the order, because a machine with
    /// no manifest — or with one project — must keep resolving exactly as it did; the command says which project it then
    /// took. A repository naming two projects both described here is refused: which one is meant is not its to say.
    /// </para>
    /// </remarks>
    public static bool TrySelect(
        string? requested,
        string? fromVariable,
        IReadOnlyList<string> fromRepository,
        IReadOnlyList<ProjectSettings> projects,
        out ProjectSettings? project,
        out string? source,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(fromRepository);
        ArgumentNullException.ThrowIfNull(projects);

        source = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var chosen = TrySelect(requested, projects, out project, out problem);

            source = chosen ? "named with --project" : null;

            return chosen;
        }

        if (!string.IsNullOrWhiteSpace(fromVariable))
        {
            var named = fromVariable.Trim();

            project = projects.FirstOrDefault(p => string.Equals(p.Name.Value, named, StringComparison.Ordinal));

            if (project is not null)
            {
                source = "from PARLEY_PROJECT";
                problem = null;

                return true;
            }

            problem = projects.Count == 0
                ? $"PARLEY_PROJECT is '{named}', and this machine has no projects configured. "
                  + "Clear it with `parley use --clear`, or name the project in `parley`."
                : $"PARLEY_PROJECT is '{named}', and this machine names {Names(projects)}. "
                  + "Choose one with `parley use <name>`, clear it with `parley use --clear`, or name one with --project.";

            return false;
        }

        var present = projects
            .Where(p => fromRepository.Contains(p.Name.Value, StringComparer.Ordinal))
            .ToList();

        if (present.Count == 1)
        {
            project = present[0];
            source = "from this repository's .mcp.json";
            problem = null;

            return true;
        }

        if (present.Count > 1)
        {
            project = null;
            problem = $"This repository answers in {Names(present)}, and which one is meant is not its to say. "
                      + "Name one with --project, or choose one for this terminal with `parley use <name>`.";

            return false;
        }

        var selected = TrySelect(null, projects, out project, out problem);

        if (selected && project is not null)
        {
            source = "the only project on this machine";
        }
        else if (!selected)
        {
            problem = problem?.Replace(
                "Name one with --project.",
                "Name one with --project, or choose one for this terminal with `parley use <name>`.",
                StringComparison.Ordinal);
        }

        return selected;
    }

    /// <summary>
    /// Resolves the broker a command should connect to.
    /// </summary>
    /// <param name="argument">An address given on the command line, or null.</param>
    /// <param name="variable">Reads an environment variable.</param>
    /// <param name="project">The selected project, or null.</param>
    /// <returns>The value and its source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    public static Resolved Endpoint(string? argument, Func<string, string?> variable, ProjectSettings? project)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return Choose(
            argument,
            variable,
            BrokerEndpoint.Variable,
            project?.Endpoint,
            project?.Name.Value,
            BrokerEndpoint.Default);
    }

    /// <summary>
    /// Resolves this machine's node catalogue: the flag, then the variable, then beside the manifest.
    /// </summary>
    /// <param name="argument">A path given on the command line, or null.</param>
    /// <param name="variable">Reads an environment variable.</param>
    /// <param name="manifestPath">The machine's project manifest, or null when it has none.</param>
    /// <returns>The value and its source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>There is no project in this.</b> What this machine can start is one catalogue per machine,
    /// not a per-project fact.
    /// </para>
    /// <para>
    /// <b>The fallback is in the root rather than the bare file name.</b> A bare name resolves
    /// against the working directory, and a broker started by a logon entry starts somewhere nobody
    /// chose. The node resolves it the same way and must:
    /// writing consent into a file the supervisor does not read is the defect this whole feature exists
    /// to remove, and changing one side of the resolution alone would reintroduce it in the opposite
    /// direction. With no manifest at all it is still the bare name, which is what the node falls back
    /// to as well.
    /// </para>
    /// </remarks>
    public static Resolved NodeConfig(string? argument, Func<string, string?> variable, string? manifestPath)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return Choose(
            argument,
            variable,
            NodeCatalogue.PathVariable,
            null,
            null,
            BesideTheManifest(manifestPath));
    }

    /// <summary>Where this machine's catalogue sits when nothing names one.</summary>
    /// <param name="root">The machine's root, or null.</param>
    /// <returns>The path.</returns>
    /// <remarks>
    /// <b>At the root itself</b>, not its parent: a catalogue beside somebody's <c>Parley</c> folder rather
    /// than in it would not be found. The root holds exactly two files that are not a project, and this is
    /// one of them.
    /// </remarks>
    private static string BesideTheManifest(string? root) =>
        root is { Length: > 0 } configured
            ? Path.Combine(Path.GetFullPath(configured), NodeCatalogue.DefaultFileName)
            : NodeCatalogue.DefaultFileName;

    private static Resolved Choose(
        string? argument,
        Func<string, string?> variable,
        string variableName,
        string? fromProject,
        string? projectName,
        string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            return new Resolved(argument, ValueSource.Argument);
        }

        var configured = variable(variableName);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new Resolved(configured, ValueSource.Environment, variableName);
        }

        if (!string.IsNullOrWhiteSpace(fromProject))
        {
            return new Resolved(fromProject, ValueSource.Project, projectName);
        }

        return new Resolved(fallback, ValueSource.Default);
    }

    private static string Names(IReadOnlyList<ProjectSettings> projects) =>
        string.Join(", ", projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal));
}
