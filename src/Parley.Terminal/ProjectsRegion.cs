using System.Globalization;
using Parley.Client;
using Parley.Client.Seating;
using Parley.Core;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Parley.Terminal;

/// <summary>
/// The projects this machine knows, and whether each one's broker is answering.
/// </summary>
/// <remarks>
/// <para>
/// The region SC-001 starts from: a person reaches a conversation without typing an identifier,
/// so the list is chosen from rather than typed into, and the first project is selected on open
/// so that the other three regions have something to show without a key being pressed.
/// </para>
/// <para>
/// Every project is asked at once and each is contained. Asked in sequence the cost of the screen
/// is the sum of the timeouts rather than the longest of them — a machine with a dozen projects
/// and two of them down would take twenty seconds to say so, which is a screen nobody waits for
/// and therefore a fact nobody reads.
/// </para>
/// </remarks>
internal sealed class ProjectsRegion : FrameView
{
    /// <summary>What this region is called, in the status line and to a test.</summary>
    internal const string RegionTitle = "Projects";

    /// <summary>A broker that did not answer within the window.</summary>
    internal const string NotAnswering = "not answering";

    /// <summary>A broker that answered.</summary>
    internal const string Answering = "answering";

    /// <summary>A broker nothing has asked yet.</summary>
    internal const string Asking = "asking";

    /// <summary>What is shown when the machine names none.</summary>
    internal const string Nothing =
        "This machine names no projects yet. Press c to create one, or ? for everything this "
        + "region can do.";

    private readonly GuardedList _list = Lists.Filling(Colours.Projects);

    private List<ProjectSettings> _projects = [];
    private List<string> _lines = [];

    internal ProjectsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        SetScheme(Colours.Region(Colours.Projects));
        Add(_list);

        // Read back off the drawn line rather than kept beside it. What the colour says is exactly
        // what the word says, so a row whose text and tint could disagree is a row where one of
        // them is lying — and the words are the half every test reads.
        Lists.Tint(_list, Tint);

        // Moving the highlight is choosing. There is no second key to press, because the other
        // three regions are already on screen showing whatever is selected — asking a person to
        // confirm a selection whose consequences they can already see is the wizard this replaced.
        _list.ValueChanged += (_, _) => Announce();
    }

    /// <summary>Raised when the person moves to a different project.</summary>
    internal event EventHandler<ProjectSettings>? Chose;

    /// <summary>The projects as drawn, in order.</summary>
    internal IReadOnlyList<ProjectSettings> Projects => _projects;

    /// <summary>Whichever project the person is on, or null when there are none.</summary>
    internal ProjectSettings? Selected =>
        _list.SelectedItem is { } at && at >= 0 && at < _projects.Count
            ? _projects[at]
            : null;

    /// <summary>The list, so a test can pick a row by its text.</summary>
    internal ListView List => _list;

    /// <summary>Draws the projects and what each broker said.</summary>
    /// <param name="projects">This machine's projects, in name order.</param>
    /// <param name="health">What each broker said, by project name.</param>
    /// <param name="manifestProblem">Why the projects could not be read, or null when they could.</param>
    /// <remarks>
    /// Projects that could not be read empty the list rather than leaving the last good ones on screen.
    /// Somebody who has just broken a project's configuration needs to be told; a surface that went on
    /// showing what it read a minute ago would hide the edit that caused it.
    /// </remarks>
    /// <param name="figures">What is in each project, by name; a project absent from it is drawn without counts.</param>
    internal void Show(
        IReadOnlyList<ProjectSettings> projects,
        IReadOnlyDictionary<string, ProjectHealth> health,
        string? manifestProblem,
        IReadOnlyDictionary<string, ProjectFigures>? figures = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(health);

        var keep = Selected?.Name.Value;

        _projects = manifestProblem is null ? [.. projects] : [];

        // Names and addresses are padded to the widest of each, so the columns line up and the eye can run
        // down one fact across every project.
        var names = _projects.Count == 0 ? 0 : _projects.Max(project => project.Name.Value.Length);
        var addresses = _projects.Count == 0 ? 0 : _projects.Max(project => Address(project).Length);

        var lines = manifestProblem is not null
            ? [$"This machine's projects could not be read. {manifestProblem}"]
            : _projects.Count == 0
                ? new List<string> { Nothing }
                : [.. _projects.Select(project => Line(project, health, figures, names, addresses))];

        var at = keep is null ? -1 : _projects.FindIndex(p => p.Name.Value == keep);

        // The rows and the lines their colour is read back from go in together, under the list's
        // own guard. Assigned separately they would disagree for as long as a draw takes.
        _list.Replace([.. lines], at >= 0 ? at : 0, also: () => _lines = [.. lines]);
    }

    /// <summary>What colour a row earns from what it says about its broker.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>The colour, or null for a row that says nothing about a broker.</returns>
    private Color? Tint(int row)
    {
        if (row < 0 || row >= _lines.Count)
        {
            return null;
        }

        var line = _lines[row];

        // The bracketed phrase Line wrote, read back. A project whose broker answered with a
        // protocol this build does not speak is neither: it warns, because that is a deployment
        // somebody has to go and look at rather than one that is simply down.
        return line.Contains($"[{Answering}]", StringComparison.Ordinal) ? Colours.Good
            : line.Contains($"[{NotAnswering}]", StringComparison.Ordinal) ? Colours.Bad
            : line.Contains($"[{Asking}]", StringComparison.Ordinal) ? Colours.Quiet
            : line.Contains("speaks ", StringComparison.Ordinal) ? Colours.Warn
            : null;
    }

    /// <summary>Tells whoever is listening which project is selected now.</summary>
    internal void Announce()
    {
        if (Selected is { } project)
        {
            Chose?.Invoke(this, project);
        }
    }

    /// <summary>
    /// What a broker's answer means, in words.
    /// </summary>
    /// <param name="health">What it said, or null when it has not been asked yet.</param>
    /// <returns>One phrase.</returns>
    /// <remarks>
    /// <b>Three states rather than two, and the third is the one worth carrying.</b> A broker that
    /// answered with a version this build was not made against is neither fine nor down, and
    /// showing it as either sends somebody looking in the wrong place. Both numbers are named,
    /// because "incompatible" without them cannot be acted on — and a colour saying "something is
    /// wrong here" is that with even less information.
    /// </remarks>
    internal static string Describe(ProjectHealth? health) =>
        health switch
        {
            null => Asking,
            { Reachable: false } => NotAnswering,
            { Agrees: false } => $"speaks {Safe.Text(health.Speaks)}, this build speaks {ProtocolVersion.Current}",
            _ => Answering,
        };

    /// <summary>How one project reads.</summary>
    /// <param name="project">The project.</param>
    /// <param name="health">What each broker said.</param>
    /// <param name="figures">What is in each project, or null when nothing was counted.</param>
    /// <param name="names">The width the name column is padded to.</param>
    /// <param name="addresses">The width the address column is padded to.</param>
    /// <returns>Its name, where it answers, whether it does, what is in it, and its description.</returns>
    /// <remarks>
    /// <b>The facts a person wants are on the row, not in a message.</b> The owner's call of 2026-09-27:
    /// creating a project printed a paragraph about ports and stores and tunnels, and the one fact worth
    /// having -- where it answers -- scrolled away with it. The address is the project's own and is always
    /// shown; the counts need its broker, so a project that did not answer shows none rather than zeros,
    /// which would be a claim about a deployment nothing could ask.
    /// </remarks>
    private static string Line(
        ProjectSettings project,
        IReadOnlyDictionary<string, ProjectHealth> health,
        IReadOnlyDictionary<string, ProjectFigures>? figures,
        int names,
        int addresses)
    {
        var said = health.TryGetValue(project.Name.Value, out var found) ? found : null;

        var counted = figures is not null && figures.TryGetValue(project.Name.Value, out var these)
            ? $"  {these}"
            : string.Empty;

        var description = string.IsNullOrWhiteSpace(project.Description)
            ? string.Empty
            : $"  {Safe.Text(project.Description)}";

        return $"{Safe.Text(project.Name.Value).PadRight(names)}  {Address(project).PadRight(addresses)}"
               + $"  [{Describe(said)}]{counted}{description}";
    }

    /// <summary>Where a project answers, as a host and port.</summary>
    /// <param name="project">The project.</param>
    /// <returns><c>127.0.0.1:19100</c> for <c>http://127.0.0.1:19100</c>; the endpoint as written when it is not a URL.</returns>
    /// <remarks>
    /// The scheme is dropped because it is the same on every row and says nothing. What is left is the
    /// manifest's own text, which a person may have edited by hand, so it goes through <see cref="Safe"/>.
    /// </remarks>
    internal static string Address(ProjectSettings project) =>
        Safe.Text(
            Uri.TryCreate(project.Endpoint, UriKind.Absolute, out var uri) && uri.Port > 0
                ? $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}"
                : project.Endpoint);
}

/// <summary>What is in one project, counted from its broker.</summary>
/// <param name="Agents">Agents registered and not retired, not counting people holding a seat.</param>
/// <param name="Running">How many of those are attached now.</param>
/// <param name="Conversations">Conversations on the first page the broker returned, archived ones left out.</param>
/// <param name="More">Whether the broker had more conversations than that page.</param>
/// <param name="Open">How many of those conversations have work still open.</param>
internal sealed record ProjectFigures(int Agents, int Running, int Conversations, bool More, int Open)
{
    /// <summary>Counts what a broker said about its project.</summary>
    /// <param name="registry">Its registry.</param>
    /// <param name="page">The first page of its conversations.</param>
    /// <returns>The figures.</returns>
    /// <remarks>
    /// A seat is a person rather than an agent, and a retired agent is one somebody has said is finished,
    /// so neither is counted: a row claiming five agents in a project that routes to three would be the
    /// row disagreeing with the agents pane one Enter away.
    /// </remarks>
    internal static ProjectFigures Of(IReadOnlyList<AgentRecord> registry, ConversationPage page)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(page);

        var agents = registry
            .Where(agent => agent.Adapter != Seat.Adapter && agent.Status != AgentStatus.Retired)
            .ToList();

        return new ProjectFigures(
            agents.Count,
            agents.Count(agent => agent.Connected),
            page.Items.Count,
            page.NextPageToken is not null,
            page.Items.Count(conversation => conversation.HasOpenExchange));
    }

    /// <summary>The figures as a row says them.</summary>
    /// <returns>For example <c>4 agents, 3 running · 12 conversations, 2 open</c>.</returns>
    public override string ToString()
    {
        var conversations = $"{Conversations.ToString(CultureInfo.InvariantCulture)}{(More ? "+" : string.Empty)}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Agents} {(Agents == 1 ? "agent" : "agents")}, {Running} running"
            + $" · {conversations} {(Conversations == 1 && !More ? "conversation" : "conversations")}, {Open} open");
    }
}
