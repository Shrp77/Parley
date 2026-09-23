using Parley.Core;

namespace Parley.Client.Views;

/// <summary>
/// What the projects view shows for the highlighted project, without leaving it.
/// </summary>
/// <param name="Agents">How many agents the project has, or null when the broker did not answer.</param>
/// <param name="Running">How many of them are attached right now.</param>
/// <param name="Conversations">How many conversations it holds, or null when unreadable.</param>
/// <param name="Archived">How many of those are archived and therefore not in the ordinary list.</param>
/// <param name="Problem">Why the project could not be read, when it could not.</param>
/// <remarks>
/// <para>
/// <b>Counts rather than lists.</b> This answers "which project do I want?" and is read while the
/// highlight moves, so it must be one broker round trip and a few lines. The lists themselves are
/// the project view, one keystroke away.
/// </para>
/// <para>
/// <b>Null is not zero and the distinction is the whole point.</b> A project whose broker is not
/// answering has an unknown number of agents; showing it as none would read as a project somebody
/// has emptied, which is the containment rule 005 wrote and this must not lose in the move.
/// </para>
/// </remarks>
public sealed record ProjectSummary(
    int? Agents,
    int Running,
    int? Conversations,
    int Archived,
    string? Problem)
{
    /// <summary>Whether the project answered at all.</summary>
    public bool Reachable => Problem is null;

    /// <summary>A summary for a project whose broker did not answer.</summary>
    /// <param name="problem">What went wrong, in the broker's own words.</param>
    /// <returns>The summary.</returns>
    public static ProjectSummary Unreachable(string problem) => new(null, 0, null, 0, problem);

    /// <summary>What the summary says, in one line.</summary>
    /// <returns>The line.</returns>
    /// <remarks>
    /// Composed here rather than by each surface, because two surfaces composing it separately is
    /// how one of them ends up saying "0 agents" for a broker that never answered.
    /// </remarks>
    public string Line()
    {
        if (Problem is { } problem)
        {
            return problem;
        }

        var agents = $"{Agents ?? 0} agent{(Agents == 1 ? string.Empty : "s")}";
        var running = Running > 0 ? $", {Running} running" : string.Empty;
        var conversations =
            $"{Conversations ?? 0} conversation{(Conversations == 1 ? string.Empty : "s")}";
        var archived = Archived > 0 ? $" ({Archived} archived)" : string.Empty;

        return $"{agents}{running} · {conversations}{archived}";
    }
}

/// <summary>One project, as the projects view lists it.</summary>
/// <param name="Settings">What the manifest says about it.</param>
/// <param name="Health">Whether its broker answers, and what it speaks.</param>
/// <remarks>
/// The summary is not here. It belongs to whichever project is highlighted and is read when the
/// highlight lands, because reading every project's agents and conversations to draw a list of
/// names would make opening the surface cost one round trip per project on the machine.
/// </remarks>
public sealed record ProjectRow(ProjectSettings Settings, ProjectHealth Health)
{
    /// <summary>The project's name.</summary>
    public string Name => Settings.Name.Value;

    /// <summary>What the row says after the name.</summary>
    public string Note =>
        Health.Problem is { } problem
            ? problem
            : Health.Agrees
                ? Settings.Description ?? string.Empty
                : $"speaks {Health.Speaks}";
}

/// <summary>
/// The projects view: every project this machine names, and a summary of the one in front of you.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole of User Story 1.</b> The question a person opens this surface with is which project
/// they want, and 005 answered it by showing four regions at once and asking them to read all of
/// them. This answers it with one list and one line beside it that follows the highlight.
/// </para>
/// <para>
/// <b>Derived here rather than in either surface</b>, for the reason seating and transcripts are:
/// two implementations of "what does this project look like from outside" would disagree about the
/// empty cases, and the empty cases are the ones that matter — a machine with no projects, a broker
/// that does not answer, a project with a catalogue and no registrations yet.
/// </para>
/// </remarks>
public static class ProjectsView
{
    /// <summary>What to say to somebody whose machine names no projects.</summary>
    /// <remarks>
    /// Names the one action that changes it. An empty list that only says it is empty leaves
    /// somebody who has just installed the tool with nothing to do and no idea that there is
    /// anything to do.
    /// </remarks>
    public const string Nothing =
        "This machine names no projects yet. Create one, or name the deployment already running.";

    /// <summary>What to say when this machine's projects could not be read.</summary>
    /// <remarks>
    /// <para>
    /// A different fact from an empty machine, and 005 recorded why: "this machine names no projects" is
    /// a machine waiting to be set up, and "they could not be read" is somebody's edit thirty seconds ago
    /// that they can undo. Showing the first for the second hides the edit that caused it.
    /// </para>
    /// <para>
    /// <b>It said "the manifest" until 019, and there is no manifest.</b> Prose left describing a file
    /// that has gone is the shape this repository keeps recording — a comment standing over a rule that
    /// was removed, read by the next person as evidence the rule is still there.
    /// </para>
    /// </remarks>
    public const string Unreadable = "This machine's projects could not be read: ";

    /// <summary>Builds the rows from the manifest and what each broker said.</summary>
    /// <param name="projects">This machine's projects.</param>
    /// <param name="health">What each broker answered, keyed by project name.</param>
    /// <returns>One row per project, in name order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <remarks>
    /// The manifest's order rather than sorted, because it is a file a person edits and the order
    /// they put things in is information. Sorting would silently discard it.
    /// </remarks>
    public static IReadOnlyList<ProjectRow> Rows(
        IReadOnlyList<ProjectSettings> projects,
        IReadOnlyDictionary<string, ProjectHealth> health)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(health);

        return
        [
            .. projects.Select(project => new ProjectRow(
                project,
                health.TryGetValue(project.Name.Value, out var known)
                    ? known
                    : new ProjectHealth(null, "not read yet")))
        ];
    }

    /// <summary>Builds the summary beside the highlighted project.</summary>
    /// <param name="agents">What its registry said, or null when it could not be asked.</param>
    /// <param name="conversations">How many conversations it holds, or null when unreadable.</param>
    /// <param name="archived">How many of those are archived.</param>
    /// <param name="problem">Why it could not be read, when it could not.</param>
    /// <returns>The summary.</returns>
    public static ProjectSummary Summarise(
        IReadOnlyList<AgentRecord>? agents,
        int? conversations,
        int archived,
        string? problem)
    {
        if (problem is not null)
        {
            return ProjectSummary.Unreachable(problem);
        }

        return new ProjectSummary(
            agents?.Count,
            agents?.Count(a => a.Presence == AgentPresence.Running) ?? 0,
            conversations,
            archived,
            null);
    }
}
