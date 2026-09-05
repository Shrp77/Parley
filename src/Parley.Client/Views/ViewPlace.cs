using Parley.Core;

namespace Parley.Client.Views;

/// <summary>Which of the three views a person is looking at.</summary>
public enum Viewing
{
    /// <summary>The projects this machine names.</summary>
    /// <remarks>
    /// Zero, so a default-constructed place is the top of the tree. There is always somewhere to
    /// go back to from anywhere else, and nowhere to go back to from here.
    /// </remarks>
    Projects = 0,

    /// <summary>One project: its agents and its conversations.</summary>
    Project = 1,

    /// <summary>One conversation, read.</summary>
    Conversation = 2,
}

/// <summary>
/// Where a person is, and what they are looking at there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three views, one thing each, and a defined way back.</b> This replaces 004 and 005's four
/// regions with Tab between them, which the owner used and found "confusing and too busy" — a
/// verdict on the arrangement rather than on anything underneath it.
/// </para>
/// <para>
/// <b>Derived once because both surfaces need the same answer to "what does back mean".</b> The
/// console and the terminal implemented seating and transcripts separately in 004 and disagreed
/// about who was in a conversation twice in one week; navigation is the same kind of thing, and
/// the second surface to implement it would have got the empty cases subtly different.
/// </para>
/// <para>
/// <b>Going back keeps the place, not just the level.</b> Coming out of a project lands on that
/// project still selected, and out of a conversation on that conversation. A back that returned to
/// the top of a list would make reading three conversations in turn cost three descents.
/// </para>
/// </remarks>
/// <param name="Viewing">Which view.</param>
/// <param name="Project">The project this is about, or null on the projects view.</param>
/// <param name="Conversation">The conversation this is about, or null above it.</param>
public sealed record ViewPlace(
    Viewing Viewing,
    ProjectName? Project = null,
    string? Conversation = null)
{
    /// <summary>The top of the tree.</summary>
    public static ViewPlace Top { get; } = new(Views.Viewing.Projects);

    /// <summary>Opens a project.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The place inside it.</returns>
    public static ViewPlace Into(ProjectName project) => new(Views.Viewing.Project, project);

    /// <summary>Opens a conversation within the project this place is already about.</summary>
    /// <param name="conversation">The conversation's identifier, which is its name.</param>
    /// <returns>The place inside it.</returns>
    /// <exception cref="InvalidOperationException">This place names no project.</exception>
    /// <remarks>
    /// Refuses rather than inventing a project, because a conversation view with no project cannot
    /// read anything: every broker call is per project, and a place that allowed it would fail
    /// later, at a call site with less to say about why.
    /// </remarks>
    public ViewPlace Into(string conversation) =>
        Project is { } project
            ? new ViewPlace(Views.Viewing.Conversation, project, conversation)
            : throw new InvalidOperationException(
                "A conversation can only be opened from within a project.");

    /// <summary>
    /// Where going back leads.
    /// </summary>
    /// <returns>The place one level out, or this one when already at the top.</returns>
    /// <remarks>
    /// The project survives the step out of a conversation, which is what lets the surface put the
    /// highlight back where it was. Back from the top is the top rather than an error: a person
    /// pressing it twice has not done anything wrong.
    /// </remarks>
    public ViewPlace Back() => Viewing switch
    {
        Views.Viewing.Conversation when Project is { } project => Into(project),
        Views.Viewing.Project => Top,
        _ => this,
    };

    /// <summary>Whether there is anywhere to go back to.</summary>
    public bool CanGoBack => Viewing != Views.Viewing.Projects;

    /// <summary>What to call this view on screen.</summary>
    public string Title => Viewing switch
    {
        Views.Viewing.Project => Project?.Value ?? "Project",
        Views.Viewing.Conversation => Conversation ?? "Conversation",
        _ => "Projects",
    };
}
