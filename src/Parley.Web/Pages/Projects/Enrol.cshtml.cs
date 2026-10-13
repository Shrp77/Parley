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
public sealed class EnrolModel(BrokerDirectory directory) : PageModel
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

    /// <summary>What a single repository owns and does not; may be empty.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Owns { get; set; }

    /// <summary>A folder whose repositories are offered, when that is what is being done.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Folder { get; set; }

    /// <summary>The repositories ticked in a folder's list.</summary>
    [BindProperty]
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
    public async Task<IActionResult> OnPostApplyAsync()
    {
        if (!Prepare())
        {
            return RedirectToPage("/Index", new { tab = "agents" });
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

            var owns = (Owns ?? string.Empty).Trim();

            wanted =
            [
                new EnrollmentEntry(
                    Path.GetFullPath(Workspace!.Trim()), Role!, owns.Length == 0 ? null : owns, Unattended: true)
                {
                    Enroll = true,
                }
            ];
        }

        var said = new StringWriter();
        var complained = new StringWriter();
        var outcome = new EnrolmentOutcome();
        var manifest = directory.ManifestPath;

        var code = await EnrollCommand.EnrolAsync(
            wanted,
            describe: false,
            ProjectResolution.NodeConfig(null, Environment.GetEnvironmentVariable, manifest),
            said,
            complained,
            Found,
            directory.Projects,
            manifest,
            HttpContext.RequestAborted,
            outcome).ConfigureAwait(false);

        Attempted = true;
        Outcome = outcome;

        var lines = complained.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (code != 0 || outcome.EnrolledAgents.Count == 0)
        {
            lines.Add("Nothing was enrolled.");
        }

        Said = lines;

        return Page();
    }

    /// <summary>Finds the project and the catalogue file; false when the project is not one this machine knows.</summary>
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
            : manifest is { Length: > 0 } && Path.GetDirectoryName(Path.GetFullPath(manifest)) is { Length: > 0 } where
                ? Path.Combine(where, NodeCatalogue.DefaultFileName)
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

        if (!OwnershipStatement.IsValid((Owns ?? string.Empty).Trim(), out var error))
        {
            problems.Add(error ?? "The ownership line cannot be used.");
        }

        return problems;
    }

    private static string Normal(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
}
