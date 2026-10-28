using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Parley.Core;
using Parley.Client;
using Parley.Client.Rendering;
using Parley.Client.Seating;
using Parley.Client.Views;
using Parley.Setup;
using Parley.Web.Seating;

namespace Parley.Web.Pages;

/// <summary>One project's row, and what its broker said when asked.</summary>
/// <param name="Project">What the manifest says about it.</param>
/// <param name="Health">What it speaks, or why it did not answer; null while it has not been asked.</param>
/// <param name="Summary">What is in it; null while it has not been asked.</param>
/// <param name="FileProblems">How many file problems wait for the person; null when not asked or not known.</param>
public sealed record ProjectRowState(ProjectSettings Project, ProjectHealth? Health, ProjectSummary? Summary, int? FileProblems = null);

/// <summary>One agent as the grid draws it.</summary>
/// <param name="Project">The project it is registered in.</param>
/// <param name="Agent">The agent.</param>
/// <param name="State">What it is doing, in words.</param>
public sealed record AgentRow(string Project, AgentRecord Agent, string State);

/// <summary>What one project's agents are, as a fragment of the agents grid draws them.</summary>
/// <param name="Project">The project.</param>
/// <param name="Rows">The agents the filters leave in; empty when none, or when the registry could not be read.</param>
/// <param name="Unread">Whether the registry could not be read, so the agents are not known.</param>
/// <param name="Problem">Why it could not be read, in the broker's own words, when it could not.</param>
public sealed record AgentGroup(string Project, IReadOnlyList<AgentRow> Rows, bool Unread, string? Problem = null);

/// <summary>
/// The projects this machine is configured for.
/// </summary>
/// <remarks>
/// The only page that is not scoped to one project, because it is the page you choose one from.
/// Everything below it names the project it belongs to: SPEC.md §4.1 gives each its own store and
/// endpoint, so two projects may each hold an agent called <c>orders</c> and a view that merged
/// them would show colliding names with nothing to say which environment each belonged to.
/// </remarks>
/// <param name="directory">The projects and their brokers.</param>
/// <param name="seats">The seats this console is holding.</param>
public sealed class IndexModel(BrokerDirectory directory, SeatBook seats) : PageModel
{
    /// <summary>The projects named in this machine's manifest.</summary>
    public IReadOnlyList<ProjectSettings> Projects { get; private set; } = [];

    /// <summary>Why the manifest could not be read, when it could not.</summary>
    public string? ManifestProblem { get; private set; }

    /// <summary>Which tab is showing: <c>projects</c> (the default) or <c>agents</c>.</summary>
    /// <remarks>
    /// A query value rather than script, so a tab is a link that works with none, survives the page's own
    /// reload and can be bookmarked.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    /// <summary>Whether the agents tab is the one showing.</summary>
    public bool AgentsTab => string.Equals(Tab, "agents", StringComparison.Ordinal);

    /// <summary>Text the rows must contain, in any of their columns; empty for every row.</summary>
    /// <remarks>
    /// The filters are a form that reads the page again with a query string, so they work with no script, survive the
    /// page's own reload and can be bookmarked. They narrow what is drawn and nothing else.
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>Projects: <c>up</c> for those whose broker answered, <c>down</c> for those that did not.</summary>
    /// <remarks>Applied by the page as the answers arrive, because the server does not wait to be told.</remarks>
    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    /// <summary>Agents: the one project to show, or empty for all.</summary>
    /// <remarks>Not called <c>project</c>, which the frame reads as "the page is inside that project".</remarks>
    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    /// <summary>Agents: the one state to show, or empty for all.</summary>
    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    /// <summary>The project the agents view is inside, when it is: the one named by <c>project</c>, as every project page names it.</summary>
    /// <remarks>
    /// Registering an agent is done in the context of a project, so the register form is offered only here. Without
    /// it the view is every project's agents, to look at and filter.
    /// </remarks>
    public ProjectSettings? InProject { get; private set; }

    /// <summary>The projects the text filter leaves in.</summary>
    public IReadOnlyList<ProjectSettings> ShownProjects => [.. Projects.Where(Matches)];

    /// <summary>The states an agent can be shown in, for the filter.</summary>
    public static IReadOnlyList<string> States { get; } =
    [
        .. Enum.GetValues<AgentPresence>()
            .Where(presence => presence is not (AgentPresence.Here or AgentPresence.Away))
            .Select(presence => presence.ToString().ToLowerInvariant()),
        "retired"
    ];

    /// <summary>The projects whose agents the agents grid asks about, in the machine's order.</summary>
    public IReadOnlyList<ProjectSettings> ScopedProjects =>
        [.. Projects.Where(p => string.IsNullOrEmpty(Scope) || Scope == p.Name.Value)];

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    private bool Matches(ProjectSettings project) =>
        string.IsNullOrWhiteSpace(Q)
        || Contains(project.Name.Value, Q.Trim()) || Contains(project.Description, Q.Trim()) || Contains(project.Endpoint, Q.Trim());

    private bool Matches(AgentRow row)
    {
        if (!string.IsNullOrEmpty(State) && State != row.State)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Q)
               || Contains(row.Agent.Name.Value, Q.Trim()) || Contains(row.Agent.Owns, Q.Trim()) || Contains(row.Project, Q.Trim());
    }

    /// <summary>The name typed in the create form.</summary>
    [BindProperty, FromForm]
    public string? NewProject { get; set; }

    /// <summary>The description typed in the create form, or empty.</summary>
    [BindProperty, FromForm]
    public string? NewDescription { get; set; }

    /// <summary>What creating a project said, when one was tried.</summary>
    [TempData]
    public string? Outcome { get; set; }

    /// <summary>Whether <see cref="Outcome"/> is a refusal.</summary>
    [TempData]
    public bool Refused { get; set; }

    /// <summary>When this page was built.</summary>
    /// <remarks>
    /// A page that refreshes itself and does not say when it last did leaves a reader unable to
    /// tell a live tab from one left open since yesterday — and the two look identical, which is
    /// the whole problem. Said on every list view for that reason, including this one, where what
    /// goes stale is the manifest rather than a broker's answer.
    /// </remarks>
    public DateTimeOffset AsOf { get; private set; }

    /// <summary>The protocol version this console was built against.</summary>
    public static string ConsoleSpeaks => ProtocolVersion.Current;

    /// <summary>Reads the manifest and draws the page, without asking any broker anything.</summary>
    /// <remarks>
    /// <para>
    /// A broker that is down costs the page its timeout, and a machine with several of them down would make the
    /// page wait for the slowest. Nothing needed to draw a row depends on what a broker says, so the rows are drawn
    /// at once with an intermediate status and the page fills each in as that project answers
    /// (<see cref="OnGetRowAsync"/>, <see cref="OnGetAgentsAsync"/>).
    /// </para>
    /// <para>
    /// Whether a project answers is a fact about now, so the status filter is applied by the page as the answers
    /// arrive: a server that had to ask before it could filter would be back to waiting.
    /// </para>
    /// </remarks>
    public void OnGet()
    {
        Projects = directory.Projects;
        ManifestProblem = directory.ManifestProblem;
        AsOf = DateTimeOffset.Now;

        if (AgentsTab && directory.Find(Request.Query["project"].ToString()) is { } inside)
        {
            InProject = inside;
            Scope = inside.Name.Value;
        }
    }

    /// <summary>Asks one project's broker what it says, and draws that project's row.</summary>
    /// <param name="name">The project.</param>
    /// <returns>The row, as a fragment to put in place of the one drawn without an answer.</returns>
    /// <remarks>
    /// <para>
    /// <c>implements</c> is asked here, before any page offers a read that could be misread. A contract whose version
    /// cannot be discovered is one nobody can rely on, and without it a broker of the wrong version is
    /// indistinguishable from one that is switched off.
    /// </para>
    /// <para>
    /// A mismatch is named rather than refused: what a version disagreement means for any particular read is not
    /// knowable from the numbers, and a console that locked the operator out would remove the view at the moment it is
    /// most needed. The questions are asked at once, so a project that is down costs one timeout rather than the sum
    /// of them.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnGetRowAsync(string? name)
    {
        if (directory.Find(name) is not { } project)
        {
            return NotFound();
        }

        var token = HttpContext.RequestAborted;
        var broker = new BrokerClient(directory.ClientFor(project));

        var registry = BrokerDirectory.ReadAsync(async () => await broker.RegistryAsync(token).ConfigureAwait(false));

        var conversations = BrokerDirectory.ReadAsync(async () =>
            await broker.ConversationsAsync(includeArchived: true, cancellationToken: token).ConfigureAwait(false));

        // What agents have put forward for the person to decide. Unknown when the broker cannot say, which
        // is not the same as none.
        var proposals = BrokerDirectory.ReadAsync(async () =>
            await broker.ProposalCountAsync(ProposalState.Waiting, token).ConfigureAwait(false));

        var implemented = BrokerDirectory.ReadAsync(async () =>
            await broker.ImplementsAsync(token).ConfigureAwait(false));

        // What sweeps of the file location found, in the row because this page does not wait for the frame's banner
        // (SPEC.md §9.5). Unknown by the same rule.
        var problems = Parley.Client.Files.FileProblemSaid.WaitingAsync(broker, token);

        await Task.WhenAll(registry, conversations, proposals, implemented, problems).ConfigureAwait(false);

        var agents = await registry.ConfigureAwait(false);
        var counted = await conversations.ConfigureAwait(false);
        var said = await implemented.ConfigureAwait(false);

        var summary = ProjectsView.Summarise(
            agents.Value?.Where(agent => !agent.Facilitator && agent.Name.Role != "human-user").ToList(),
            counted.Value?.Items.Count,
            counted.Value?.Items.Count(c => c.Archived) ?? 0,
            agents.Problem ?? counted.Problem,
            await proposals.ConfigureAwait(false) is { Reachable: true } waiting ? waiting.Value : null);

        var health = new ProjectHealth(said.Value?.Protocol, said.Problem, said.Value?.BrokerVersion, said.Value?.StoreProblems);

        return Partial("_ProjectRow", new ProjectRowState(project, health, summary, await problems.ConfigureAwait(false)));
    }

    /// <summary>Takes the person's seats and counts what is waiting for them, for the page to show once it has loaded.</summary>
    /// <returns>The count, and whether every project answered.</returns>
    /// <remarks>
    /// The frame does this while it draws, on every page that waits for it; this page does not wait, so it asks here
    /// instead. A render counts as the person being at the console unless it is the page reloading itself.
    /// </remarks>
    public async Task<IActionResult> OnGetPresenceAsync()
    {
        var (waiting, certain) = await PersonSeats.PresenceAsync(
            seats, directory, HttpContext.RequestAborted,
            person: !Request.Query.ContainsKey(PersonSeats.Reloaded)).ConfigureAwait(false);

        return new JsonResult(new { waiting, certain });
    }

    /// <summary>Lists the folders in one directory of this machine, for the folder browser on the register form.</summary>
    /// <param name="path">The directory; empty for the drives, or the root on a machine without them.</param>
    /// <returns>The directory, the one above it, and the folders in it with whether each is a repository.</returns>
    /// <remarks>
    /// Folders only, never a file's name or contents, and hidden and system folders are left out. The console answers only
    /// loopback host names, so the person at this machine is the only reader; the page's other handlers rest on the same
    /// premise. The names are sent as data and drawn as text.
    /// </remarks>
    public IActionResult OnGetFolders(string? path)
    {
        const int Cap = 500;

        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = OperatingSystem.IsWindows()
                ? DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.Name)
                : ["/"];

            return new JsonResult(new
            {
                path = string.Empty,
                parent = (string?)null,
                folders = roots.Select(name => new { name, full = name, repo = false }),
                error = (string?)null,
            });
        }

        var wanted = path.Trim();

        if (!Path.IsPathRooted(wanted) || !Directory.Exists(wanted))
        {
            return new JsonResult(new { path = wanted, parent = (string?)null, folders = Array.Empty<object>(), error = "There is no such folder." });
        }

        var here = Path.GetFullPath(wanted);
        var parent = Path.GetDirectoryName(here.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            var folders = Directory.EnumerateDirectories(here, "*", options)
                .Select(full => new
                {
                    name = Path.GetFileName(full),
                    full,
                    repo = Directory.Exists(Path.Combine(full, ".git")) || System.IO.File.Exists(Path.Combine(full, ".git")),
                })
                .OrderBy(folder => folder.name, StringComparer.OrdinalIgnoreCase)
                .Take(Cap)
                .ToList();

            return new JsonResult(new { path = here, parent, folders, error = (string?)null });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JsonResult(new { path = here, parent, folders = Array.Empty<object>(), error = "That folder cannot be read." });
        }
    }

    /// <summary>Asks one project's broker for its agents, and draws their rows.</summary>
    /// <param name="name">The project.</param>
    /// <returns>The rows the filters leave in, as a fragment to put in place of the project's placeholder.</returns>
    public async Task<IActionResult> OnGetAgentsAsync(string? name)
    {
        if (directory.Find(name) is not { } project)
        {
            return NotFound();
        }

        var token = HttpContext.RequestAborted;

        var registry = await BrokerDirectory.ReadAsync(async () =>
            await new BrokerClient(directory.ClientFor(project)).RegistryAsync(token).ConfigureAwait(false)).ConfigureAwait(false);

        if (registry.Value is null)
        {
            return Partial("_AgentRows", new AgentGroup(project.Name.Value, [], Unread: true, registry.Problem));
        }

        var rows = registry.Value
            .Where(agent => !agent.Facilitator && agent.Name.Role != "human-user")
            .OrderBy(agent => agent.Name.Machine, StringComparer.Ordinal)
            .ThenBy(agent => agent.Name.Role, StringComparer.Ordinal)
            .Select(agent => new AgentRow(project.Name.Value, agent, Roster.State(agent)))
            .Where(Matches)
            .ToList();

        return Partial("_AgentRows", new AgentGroup(project.Name.Value, rows, Unread: false));
    }

    /// <summary>Names a new project on this machine.</summary>
    /// <returns>A redirect back to the projects tab, saying what came of it.</returns>
    /// <remarks>
    /// <para>
    /// Through <see cref="ProjectCommand"/>, the one implementation <c>parley</c> and <c>parley-terminal</c> share, so
    /// the console cannot disagree with them about what a project is. Creating is not destructive, so it takes no typed
    /// name; removing and renaming stay in the terminal.
    /// </para>
    /// <para>
    /// A refusal comes back in the command's own words, as the terminal does, because a summary of "invalid" would
    /// drop the rule that was broken.
    /// </para>
    /// </remarks>
    public async Task<IActionResult> OnPostCreateAsync()
    {
        var manifest = directory.ManifestPath;

        if (!ProjectName.TryCreate(NewProject, out var name))
        {
            Refused = true;
            Outcome = $"{NewProject} is not a name a project can have. Nothing has been created.";
        }
        else if (manifest is null)
        {
            Refused = true;
            Outcome = "This console cannot name a project.";
        }
        else
        {
            var said = new StringWriter();
            var complained = new StringWriter();
            var description = string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim();

            var code = await ProjectCommand.RunAsync(
                ["add", name.Value], manifest, new ProjectFields(description), said, complained,
                cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);

            Refused = code != 0;
            Outcome = code == 0
                ? $"Created {name.Value}. The broker starts it within seconds."
                : string.Join(' ', complained.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim()));
        }

        return RedirectToPage("/Index");
    }
}
