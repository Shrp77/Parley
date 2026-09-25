using Parley.Core;
using Parley.Setup;

namespace Parley.Cli;

/// <summary>
/// `parley use`: which project a terminal's commands act on, and the line that chooses one.
/// </summary>
/// <remarks>
/// <para>
/// <b>It prints the line rather than setting anything, because it cannot.</b> A terminal's project is its
/// <c>PARLEY_PROJECT</c>, and a process cannot change the environment of the shell that started it. So it prints the line
/// for each shell, and <c>--shell</c> prints that one line alone, for the shell to run:
/// <c>Invoke-Expression (parley use alpha --shell powershell)</c> or <c>eval "$(parley use alpha --shell posix)"</c>.
/// </para>
/// <para>
/// <b>Per terminal, and never saved.</b> The owner chose this over a saved "current project": chosen in one window, a saved
/// one would quietly apply to a purge in another. A terminal's choice ends with the terminal, and <c>--project</c> on any
/// one command still wins over it.
/// </para>
/// <para>
/// With no project it says which project a command run here would act on, and what chose it — the same resolution every
/// command applies, so it cannot disagree with them.
/// </para>
/// </remarks>
internal static class UseCommand
{
    private const int Usage = 64;
    private const int Config = 78;

    private static readonly string[] Shells = ["powershell", "posix", "cmd"];

    /// <summary>Runs <c>parley use</c>.</summary>
    /// <param name="rest">The words after the verb: a project, or nothing.</param>
    /// <param name="shell">The shell to print the one line for, or null for every shell.</param>
    /// <param name="clear">True to print the line that stops the terminal choosing a project.</param>
    /// <param name="named">A project given with <c>--project</c>, or null.</param>
    /// <param name="projects">What the manifest describes.</param>
    /// <param name="fromVariable">The terminal's <c>PARLEY_PROJECT</c>, or null.</param>
    /// <param name="fromRepository">The projects the repository this runs in names.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="error">Where a refusal goes.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> rest,
        string? shell,
        bool clear,
        string? named,
        IReadOnlyList<ProjectSettings> projects,
        string? fromVariable,
        IReadOnlyList<string> fromRepository,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (shell is not null && !Shells.Contains(shell, StringComparer.Ordinal))
        {
            await error.WriteLineAsync($"--shell is powershell, posix or cmd, and '{shell}' is none of them.")
                .ConfigureAwait(false);

            return Usage;
        }

        if (rest.Count > 1 || (clear && rest.Count > 0) || (named is not null && (clear || rest.Count > 0)))
        {
            await error.WriteLineAsync(
                "`parley use` takes one project, or --clear, or nothing to say which project is in use.")
                .ConfigureAwait(false);

            return Usage;
        }

        if (clear)
        {
            await WriteAsync(output, shell, null).ConfigureAwait(false);

            return 0;
        }

        if (rest.Count == 1)
        {
            var project = rest[0];

            if (!projects.Any(p => string.Equals(p.Name.Value, project, StringComparison.Ordinal)))
            {
                await error.WriteLineAsync(projects.Count == 0
                    ? $"No project named '{project}' — this machine has none configured. Name one in `parley terminal`."
                    : $"No project named '{project}'. Configured: "
                      + string.Join(", ", projects.Select(p => p.Name.Value).Order(StringComparer.Ordinal)) + ".")
                    .ConfigureAwait(false);

                return Config;
            }

            await WriteAsync(output, shell, project).ConfigureAwait(false);

            return 0;
        }

        if (shell is not null)
        {
            await error.WriteLineAsync(
                $"--shell prints the line for a project, or for --clear: `parley use <name> --shell {shell}`.")
                .ConfigureAwait(false);

            return Usage;
        }

        if (!ProjectResolution.TrySelect(named, fromVariable, fromRepository, projects, out var chosen, out var source, out var problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);

            return Config;
        }

        await output.WriteLineAsync(chosen is null
            ? "No project: this machine names none, so a command reaches the local broker. "
              + "Name the first one in `parley terminal`."
            : $"{chosen.Name.Value} ({source}).").ConfigureAwait(false);

        return 0;
    }

    private static async Task WriteAsync(TextWriter output, string? shell, string? project)
    {
        const string Variable = EnrollmentPlan.ProjectVariable;

        (string Shell, string Label, string Line)[] lines =
        [
            ("powershell", "PowerShell", project is null
                ? $"Remove-Item Env:{Variable} -ErrorAction SilentlyContinue"
                : $"$env:{Variable} = '{project}'"),
            ("posix", "bash, zsh", project is null ? $"unset {Variable}" : $"export {Variable}={project}"),
            ("cmd", "cmd", project is null ? $"set {Variable}=" : $"set {Variable}={project}"),
        ];

        if (shell is not null)
        {
            await output.WriteLineAsync(lines.Single(l => l.Shell == shell).Line).ConfigureAwait(false);

            return;
        }

        var what = project ?? "--clear";

        await output.WriteLineAsync(project is null
            ? "To stop this terminal choosing a project, run the line for its shell:"
            : $"To act on {project} from this terminal, run the line for its shell:").ConfigureAwait(false);

        foreach (var (_, label, line) in lines)
        {
            await output.WriteLineAsync($"  {label,-11} {line}").ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"Or have the shell run it: Invoke-Expression (parley use {what} --shell powershell), "
            + $"or eval \"$(parley use {what} --shell posix)\".").ConfigureAwait(false);

        await output.WriteLineAsync(project is null
            ? "Commands then take their project from --project, the repository they run in, or the only project there is."
            : "It lasts until the terminal closes, and --project on any one command still wins.").ConfigureAwait(false);
    }
}
