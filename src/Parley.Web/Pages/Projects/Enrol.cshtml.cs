using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Client;
using Parley.Configuration;
using Parley.Core;
using Parley.Setup;

namespace Parley.Web.Pages.Projects;

/// <summary>
/// Making repositories on this machine agents of a project: one repository named here, or the repositories in a folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same act the terminal does, through the same code.</b> What is written is the repository's launch
/// configuration, its instructions file and a line in the machine's catalogue, all by <see cref="EnrollCommand"/>, which
/// the terminal and <c>parley enroll</c> also run. The console writes no registration, and cannot: an agent registers
/// itself when it is started under the identity its launch configuration fixes (SPEC.md §12.3), so the console asks
/// for a start and then looks.
/// </para>
/// <para>
/// <b>Nothing is written until the person has seen what would be.</b> A GET only describes. The files are written by
/// the POST it leads to, which says how many repositories and what that costs before it is pressed.
/// </para>
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="runs">Enrolments in progress, so the page can say what each is doing.</param>
public sealed class EnrolModel(BrokerDirectory directory, EnrolmentRuns runs) : PageModel
{
    /// <summary>The project the agents join.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    /// <summary>A single repository's directory on this machine.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Workspace { get; set; }

    /// <summary>The role peers will address a single repository by.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    /// <summary>A folder whose repositories are offered, when that is what is being done.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }

    /// <summary>The repositories ticked in a folder's list.</summary>
    [BindProperty, FromForm]
    public List<string> Chosen { get; set; } = [];

    /// <summary>The project, once found.</summary>
    public ProjectSettings? Found { get; private set; }

    /// <summary>Whether a folder, rather than one repository, is the subject.</summary>
    public bool FolderMode => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Why the request cannot go ahead, in words.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>The repositories a folder holds.</summary>
    public IReadOnlyList<EnrollmentEntry> Entries { get; private set; } = [];

    /// <summary>The file this machine starts its agents from.</summary>
    public string Catalogue { get; private set; } = string.Empty;

    /// <summary>What came of enrolling, once it has been done.</summary>
    public IReadOnlyList<string> Said { get; private set; } = [];

    /// <summary>The enrolment under way, which the page asks about until it is over.</summary>
    public string? RunId { get; private set; }

    /// <summary>The repositories being enrolled, so the page can list them before any has said anything.</summary>
    public IReadOnlyList<string> Working { get; private set; } = [];

    /// <summary>Whether enrolling was attempted on this request.</summary>
    public bool Attempted { get; private set; }

    /// <summary>Which of the agents enrolled had registered by the time the page was drawn.</summary>
    public EnrolmentOutcome? Outcome { get; private set; }

    /// <summary>Describes what would be written, without writing it.</summary>
    /// <returns>The page, or a redirect when the project is not one this machine knows.</returns>
    public IActionResult OnGet()
    {
        if (!Prepare())
        {
            return RedirectToPage("/Index", new { tab = "agents" });
        }

        if (OffThisMachine())
        {
            return Page();
        }

        if (FolderMode)
        {
            Entries = Directory.Exists(Folder!.Trim()) ? EnrollCommand.Find(Folder.Trim()) : [];

            if (!Directory.Exists(Folder.Trim()))
            {
                Problems = [$"There is no directory at {Folder.Trim()}."];
            }
            else if (Entries.Count == 0)
            {
                Problems = [$"No git repositories directly under {Folder.Trim()}."];
            }
        }
        else
        {
            Problems = SingleProblems();
        }

        return Page();
    }

    /// <summary>Writes the files, starts each agent once and says what became of it.</summary>
    /// <returns>The page, saying what happened.</returns>
    /// <remarks>
    /// Slow on purpose to report: each agent is started and waited for in turn, up to a time limit, because a node
    /// reporting that it launched something says nothing about whether the agent attached and registered.
    /// </remarks>
    public IActionResult OnPostApply()
    {
        if (!Prepare())
        {
            return RedirectToPage("/Index", new { tab = "agents" });
        }

        if (OffThisMachine())
        {
            return Page();
        }

        List<EnrollmentEntry> wanted;

        if (FolderMode)
        {
            var ticked = Chosen.Select(Normal).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Found again rather than carried from the form: what a repository is called and what it owns come from
            // the folder, never from the browser.
            wanted =
            [
                .. (Directory.Exists(Folder!.Trim()) ? EnrollCommand.Find(Folder.Trim()) : [])
                    .Where(entry => ticked.Contains(Normal(entry.Workspace)))
                    .Select(entry => entry with { Enroll = true })
            ];

            if (wanted.Count == 0)
            {
                Problems = ["Nothing was ticked, so nothing was enrolled."];

                return Page();
            }
        }
        else
        {
            Problems = SingleProblems();

            if (Problems.Count > 0)
            {
                return Page();
            }

            // No ownership line: the agent reports what it owns once it has attached, and a person can change it.
            wanted =
            [
                new EnrollmentEntry(
                    Path.GetFullPath(Workspace!.Trim()), Role!, null, Unattended: true)
                {
                    Enroll = true,
                }
            ];
        }

        var manifest = directory.ManifestPath;
        var project = Found;

        // Begun here and finished beyond this request: each agent is started and waited for in turn, and a page that said
        // nothing until the end left a browser's tab spinner as the only sign anything was happening.
        var id = runs.Start(
            wanted.Select(entry => entry.Agent),
            async outcome =>
            {
                var said = new StringWriter();
                var complained = new StringWriter();

                var code = await EnrollCommand.EnrolAsync(
                    wanted,
                    describe: false,
                    ProjectResolution.NodeConfig(null, Environment.GetEnvironmentVariable, manifest),
                    said,
                    complained,
                    project,
                    manifest,
                    CancellationToken.None,
                    outcome).ConfigureAwait(false);

                var lines = complained.ToString().Split((char)10, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();

                if (code != 0 || outcome.EnrolledAgents.Count == 0)
                {
                    lines.Add("Nothing was enrolled.");
                }

                return lines;
            });

        // A redirect, so reloading the page asks about the run again rather than enrolling a second time.
        return RedirectToPage(new { handler = "Running", run = id, project = Found!.Name.Value });
    }

    /// <summary>The page an enrolment is watched from while it runs.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The page listing what each repository is doing, or back to the agents when there is no such run.</returns>
    public IActionResult OnGetRunning(string? run)
    {
        if (!Prepare() || runs.Progress(run) is not { } progress)
        {
            return RedirectToPage("/Index", new { tab = "agents" });
        }

        RunId = run;
        Working = [.. progress.Activity.Select(pair => pair.Key)];

        return Page();
    }

    /// <summary>What a run has said so far, for the page to show while it waits.</summary>
    /// <param name="run">The run.</param>
    /// <returns>Whether it is over, and what each repository is doing.</returns>
    public IActionResult OnGetProgress(string? run)
    {
        if (runs.Progress(run) is not { } progress)
        {
            return NotFound();
        }

        return new JsonResult(new
        {
            done = progress.Done,
            activity = progress.Activity.Select(pair => new { role = pair.Key, says = pair.Value }),
        });
    }

    /// <summary>What a finished run came to.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The page saying what happened, or back to the agents when there is no such run.</returns>
    public IActionResult OnGetResult(string? run)
    {
        if (!Prepare() || runs.Finished(run) is not { } finished)
        {
            return RedirectToPage("/Index", new { tab = "agents" });
        }

        // Everything came up and nothing needs saying: the agents are what the person wants to see, so go there. The
        // page stays for the other case, where something did not register and a person has to know which and why.
        if (finished.Said.Count == 0
            && finished.Outcome.Unregistered.Count == 0
            && finished.Outcome.RegisteredAgents.Count > 0)
        {
            return RedirectToPage("/Index", new { tab = "agents", project = Found!.Name.Value });
        }

        Attempted = true;
        Outcome = finished.Outcome;
        Said = finished.Said;

        return Page();
    }

    /// <summary>Finds the project and the catalogue file; false when the project is not one this machine knows.</summary>
    // Before any lookup, because the lookup is what reaches the network: a link in an agent's message can carry either
    // field, and a GET would otherwise ask another machine about it with the person's credentials (LocalPath).
    private bool OffThisMachine()
    {
        if (!LocalPath.IsOffThisMachine(Folder) && !LocalPath.IsOffThisMachine(Workspace))
        {
            return false;
        }

        Problems = [LocalPath.OffThisMachine];
        Entries = [];

        return true;
    }

    private bool Prepare()
    {
        Found = directory.Find(Project);

        if (Found is null)
        {
            return false;
        }

        var manifest = directory.ManifestPath;
        var resolved = ProjectResolution.NodeConfig(null, Environment.GetEnvironmentVariable, manifest);

        Catalogue = resolved.Value is { Length: > 0 } named
            ? named
            : manifest is { Length: > 0 }
                // In the root itself, as `ProjectResolution.BesideTheManifest` puts it: the manifest path is the root.
                ? Path.Combine(Path.GetFullPath(manifest), NodeCatalogue.DefaultFileName)
                : NodeCatalogue.DefaultFileName;

        Role = string.IsNullOrWhiteSpace(Role) ? Role : EnrollmentPlan.CorrectName(Role);

        return true;
    }

    /// <summary>Why one repository cannot be made an agent, or nothing when it can.</summary>
    /// <returns>The reasons.</returns>
    /// <remarks>
    /// Only the facts a directory and a name carry. Whether the repository's own files allow it is settled by the plan
    /// when it writes, since those files can change between this page and the button.
    /// </remarks>
    private List<string> SingleProblems()
    {
        var problems = new List<string>();
        var workspace = Workspace?.Trim() ?? string.Empty;

        if (workspace.Length == 0)
        {
            problems.Add("Name a repository directory on this machine.");
        }
        else if (!Directory.Exists(workspace))
        {
            problems.Add($"There is no directory at {workspace}.");
        }
        else if (!Directory.Exists(Path.Combine(workspace, ".git")) && !System.IO.File.Exists(Path.Combine(workspace, ".git")))
        {
            problems.Add($"{workspace} is not a repository: it holds no .git.");
        }

        if (!EnrollmentPlan.IsWellFormedName(Role))
        {
            problems.Add("The role must be lowercase letters, digits and hyphens, starting with a letter.");
        }

        return problems;
    }

    private static string Normal(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
