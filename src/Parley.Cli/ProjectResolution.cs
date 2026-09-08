using Parley.Configuration;
using Parley.Core;

namespace Parley.Cli;

/// <summary>Where a resolved value came from, so a command can say why it is about to act.</summary>
public enum ValueSource
{
    /// <summary>Given on the command line.</summary>
    Argument,

    /// <summary>Read from an environment variable.</summary>
    Environment,

    /// <summary>Supplied by the selected project.</summary>
    Project,

    /// <summary>The behaviour that applied before any of this existed.</summary>
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
/// <b>Additive by construction.</b> A project is consulted only where the previous default would
/// have applied, so a machine with no manifest resolves exactly as it did before this existed —
/// value for value, including the working-directory fallback for the node catalogue. That is what
/// lets an existing deployment upgrade without migrating.
/// </para>
/// <para>
/// <b>It never guesses which project was meant.</b> Several configured and none named is a
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
                  "Add one with `parley project add`."
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
    /// Resolves the store this command should open.
    /// </summary>
    /// <param name="argument">A path given on the command line, or null.</param>
    /// <param name="variable">Reads an environment variable.</param>
    /// <param name="project">The selected project, or null.</param>
    /// <returns>The value and its source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    public static Resolved Store(string? argument, Func<string, string?> variable, ProjectSettings? project)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return Choose(
            argument,
            variable,
            DeploymentEnvironment.DatabaseVariable,
            project?.Store,
            project?.Name.Value,
            fallback: null);
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
    /// Resolves the node catalogue a command should read or write.
    /// </summary>
    /// <param name="argument">A path given on the command line, or null.</param>
    /// <param name="variable">Reads an environment variable.</param>
    /// <param name="project">The selected project, or null.</param>
    /// <returns>The value and its source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    /// <remarks>
    /// The fallback is deliberately <see cref="NodeCatalogue.DefaultFileName"/> — a bare name,
    /// resolved against the working directory — because that is what the node itself does, and
    /// parity matters more than tidiness. Writing consent into a file the supervisor does not read
    /// is the defect this whole feature exists to remove; changing only one side of that
    /// resolution would reintroduce it in the opposite direction.
    /// </remarks>
    public static Resolved NodeConfig(string? argument, Func<string, string?> variable, ProjectSettings? project)
    {
        ArgumentNullException.ThrowIfNull(variable);

        return Choose(
            argument,
            variable,
            NodeCatalogue.PathVariable,
            project?.NodeConfig,
            project?.Name.Value,
            NodeCatalogue.DefaultFileName);
    }

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
