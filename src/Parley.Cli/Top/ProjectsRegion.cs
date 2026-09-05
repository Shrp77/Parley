using Parley.Client;
using Parley.Core;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Parley.Cli.Top;

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

    private readonly ListView _list = new() { Width = Dim.Fill(), Height = Dim.Fill() };

    private List<ProjectSettings> _projects = [];

    internal ProjectsRegion()
    {
        Title = RegionTitle;
        CanFocus = true;
        Add(_list);

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
    /// <param name="projects">The manifest's projects, in its order.</param>
    /// <param name="health">What each broker said, by project name.</param>
    /// <param name="manifestProblem">Why the manifest could not be read, or null when it could.</param>
    /// <remarks>
    /// A manifest that could not be read empties the list rather than leaving the last good one on
    /// screen. Somebody who has just broken their manifest needs to be told; a surface that went
    /// on showing what it read a minute ago would hide the edit that caused it.
    /// </remarks>
    internal void Show(
        IReadOnlyList<ProjectSettings> projects,
        IReadOnlyDictionary<string, ProjectHealth> health,
        string? manifestProblem)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(health);

        var keep = Selected?.Name.Value;

        _projects = manifestProblem is null ? [.. projects] : [];

        var lines = manifestProblem is not null
            ? [$"The manifest could not be read. {manifestProblem}"]
            : _projects.Count == 0
                ? new List<string> { Nothing }
                : [.. _projects.Select(project => Line(project, health))];

        _list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(lines));

        var at = keep is null ? -1 : _projects.FindIndex(p => p.Name.Value == keep);

        _list.SelectedItem = at >= 0 ? at : 0;
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
    /// <returns>Its name, whether it answers, and its description when it has one.</returns>
    private static string Line(
        ProjectSettings project,
        IReadOnlyDictionary<string, ProjectHealth> health)
    {
        var said = health.TryGetValue(project.Name.Value, out var found) ? found : null;

        var description = string.IsNullOrWhiteSpace(project.Description)
            ? string.Empty
            : $"  {Safe.Text(project.Description)}";

        return $"{Safe.Text(project.Name.Value)}  [{Describe(said)}]{description}";
    }
}
