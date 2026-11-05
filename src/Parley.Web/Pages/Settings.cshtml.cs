using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;

namespace Parley.Web.Pages;

/// <summary>One project as the Parley-wide settings page lists it.</summary>
/// <param name="Project">What this machine's manifest says about it.</param>
/// <param name="Health">What its broker said when asked what it speaks and which build it is.</param>
public sealed record MachineProject(ProjectSettings Project, ProjectHealth Health);

/// <summary>
/// Settings for Parley as a whole rather than one project: how this browser draws the console, and what is on this
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing on this page changes the deployment.</b> The theme and density are preferences of this browser, kept in its
/// storage beside the navigation's collapsed state, so no surface owes them and no broker holds them. A setting that
/// changes the machine (autostart, defaults for new projects) is a decision about the deployment, which the terminal
/// owns; it would come here only with the same setting on every surface.
/// </para>
/// <para>
/// <b>What is on this machine is read, never acted on</b>: the root the console follows, each project's address and
/// store, and the build each broker says it is, so a person can tell from one page whether an install took everywhere.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
public sealed class ParleySettingsModel(BrokerDirectory directory) : PageModel
{
    /// <summary>The root this console follows, or null when it was handed a fixed list.</summary>
    public string? Root { get; private set; }

    /// <summary>Why the manifest could not be read, when it could not.</summary>
    public string? ManifestProblem { get; private set; }

    /// <summary>Each project and what its broker said.</summary>
    public IReadOnlyList<MachineProject> Projects { get; private set; } = [];

    /// <summary>Asks every project's broker which build and protocol it is.</summary>
    /// <returns>A task that completes when every broker answered or could not.</returns>
    public async Task OnGetAsync()
    {
        Root = directory.ManifestPath;
        ManifestProblem = directory.ManifestProblem;

        var asked = directory.Projects
            .Select(async project =>
            {
                var said = await BrokerDirectory.ReadAsync(async () =>
                    await new BrokerClient(directory.ClientFor(project))
                        .ImplementsAsync(HttpContext.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);

                return new MachineProject(
                    project,
                    new ProjectHealth(said.Value?.Protocol, said.Problem, said.Value?.BrokerVersion, said.Value?.StoreProblems));
            })
            .ToList();

        Projects = [.. (await Task.WhenAll(asked).ConfigureAwait(false)).OrderBy(p => p.Project.Name.Value, StringComparer.Ordinal)];
    }
}
