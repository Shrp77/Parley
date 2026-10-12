using Parley.Core;

namespace Parley.Web.Pages.Shared;

/// <summary>What the roadmap summary is drawn from.</summary>
/// <remarks>
/// A view of its own because two pages show it, the project's and the roadmaps list, and a roadmap summary that read one
/// way on arrival and another a click later would be the drift the roster partial was made to end.
/// </remarks>
/// <param name="Project">The project.</param>
/// <param name="Waiting">The proposals nobody has decided.</param>
/// <param name="Roadmaps">The project's roadmaps.</param>
public sealed record RoadmapSummaryView(
    string Project, IReadOnlyList<RoadmapProposal> Waiting, IReadOnlyList<RoadmapSummary> Roadmaps);
