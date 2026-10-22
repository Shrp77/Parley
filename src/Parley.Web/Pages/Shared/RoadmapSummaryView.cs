using Parley.Core;

namespace Parley.Web.Pages.Shared;

/// <summary>What the roadmap summary is drawn from.</summary>
/// <remarks>
/// A view of its own so that whatever shows the roadmap summary reads it one way.
/// </remarks>
/// <param name="Project">The project.</param>
/// <param name="Waiting">The proposals nobody has decided.</param>
/// <param name="Roadmaps">The project's roadmaps.</param>
public sealed record RoadmapSummaryView(
    string Project, IReadOnlyList<RoadmapProposal> Waiting, IReadOnlyList<RoadmapSummary> Roadmaps);
