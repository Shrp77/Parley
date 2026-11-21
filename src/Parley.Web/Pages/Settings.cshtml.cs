using Microsoft.AspNetCore.Mvc;
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
/// <b>The Inbox's closed window is the one setting kept for the machine</b> (spec 036): how far back the Closed view looks,
/// in <c>&lt;root&gt;/settings.json</c>, because the terminal's Inbox reads the same number. It changes what is shown, never
/// anything a broker holds.
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

    /// <summary>How many days the Inbox's Closed view covers, as this machine's settings say.</summary>
    public int ClosedDays { get; private set; } = Parley.Configuration.ParleySettings.DefaultClosedDays;

    /// <summary>The window the person asked for.</summary>
    [BindProperty, FromForm]
    public int? Days { get; set; }

    /// <summary>What saving the window came to, when it was just saved or refused.</summary>
    public string? InboxSaid { get; private set; }

    /// <summary>Whether that was a refusal.</summary>
    public bool InboxRefused { get; private set; }

    /// <summary>Sets how far back the Inbox's Closed view looks.</summary>
    /// <returns>The page, saying what happened.</returns>
    public async Task<IActionResult> OnPostInboxAsync()
    {
        if (directory.ManifestPath is not { Length: > 0 } root)
        {
            InboxRefused = true;
            InboxSaid = "This console was given its projects directly, so it has no deployment folder to keep the window in.";
        }
        else if (Days is not { } days)
        {
            InboxRefused = true;
            InboxSaid = $"Enter a whole number of days from {Parley.Configuration.ParleySettings.FewestClosedDays} to {Parley.Configuration.ParleySettings.MostClosedDays}.";
        }
        else if (Parley.Configuration.ParleySettings.WriteClosedDays(root, days) is { } refused)
        {
            InboxRefused = true;
            InboxSaid = refused;
        }
        else
        {
            InboxSaid = $"Saved. The Inbox's Closed view covers {days} {(days == 1 ? "day" : "days")}, here and in the terminal.";
        }

        await OnGetAsync().ConfigureAwait(false);

        return Page();
    }

    /// <summary>Asks every project's broker which build and protocol it is.</summary>
    /// <returns>A task that completes when every broker answered or could not.</returns>
    public async Task OnGetAsync()
    {
        Root = directory.ManifestPath;
        ClosedDays = Root is { Length: > 0 } root
            ? Parley.Configuration.ParleySettings.ReadClosedDays(root)
            : Parley.Configuration.ParleySettings.DefaultClosedDays;
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
