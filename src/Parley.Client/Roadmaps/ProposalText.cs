using System.Globalization;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// What a waiting proposal says to the person who has to decide it, in the words every surface uses (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// One description for the terminal's dialog, the command line's listing and the web page's summary, so that a person who
/// reads a proposal in one place has read what they would in another. The words of the proposal are the agent's, so a
/// surface draws them as an agent's words are drawn everywhere: escaped, on a line of their own, never as something the
/// surface itself said.
/// </para>
/// </remarks>
public static class ProposalText
{
    /// <summary>One line naming a proposal, for a list of them.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <returns>The title, who proposed it and how many items.</returns>
    public static string Row(RoadmapProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var count = proposal.Content.Items.Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{proposal.Content.Title} — {proposal.Proposer.Role}, {count} item{(count == 1 ? string.Empty : "s")}");
    }

    /// <summary>Describes a proposal whole.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="sameTitle">The title of an existing roadmap the proposal's title hint matches, or <see langword="null"/>.</param>
    /// <returns>Several lines: where it came from, what it is, and every item.</returns>
    /// <remarks>
    /// Says plainly what is not true of a proposal: it is not a roadmap and assigns nobody. A person deciding it should
    /// not have to remember that.
    /// </remarks>
    public static string Describe(RoadmapProposal proposal, string? sameTitle = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var lines = new List<string>
        {
            proposal.Content.Title,
            string.Empty,
            $"Proposed by {proposal.Proposer.Value}" + (proposal.SourceConversation is { Length: > 0 } source
                ? $", from the conversation {source}."
                : "."),
        };

        if (proposal.TargetTitle is { Length: > 0 } target)
        {
            lines.Add(sameTitle is null
                ? $"It says it would change a roadmap called '{target}'; there is none by that name."
                : $"It says it would change the roadmap '{sameTitle}'. You choose whether it does.");
        }

        lines.Add(string.Empty);

        for (var at = 0; at < proposal.Content.Items.Count; at++)
        {
            var item = proposal.Content.Items[at];
            var line = string.Create(CultureInfo.InvariantCulture, $"{at + 1}. {item.Text}");

            if (item.IntendedAgent is { } agent)
            {
                line += $"  (for {agent.Role})";
            }

            if (item.Needs.Count > 0)
            {
                line += "  (needs " + string.Join(", ", item.Needs.Order().Select(n => n.ToString(CultureInfo.InvariantCulture))) + ")";
            }

            lines.Add(line);
        }

        lines.Add(string.Empty);
        lines.Add("Nothing is a roadmap until you confirm it, and it assigns nobody: work still goes to an agent as a request from you.");

        return string.Join('\n', lines);
    }

    /// <summary>The roadmap a proposal's title hint matches, if any.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="roadmaps">The project's roadmaps.</param>
    /// <returns>The match, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A hint and nothing more (spec FR-009b): shown so a person can choose, never used to decide for them.
    /// </remarks>
    public static RoadmapSummary? Matching(RoadmapProposal proposal, IReadOnlyList<RoadmapSummary> roadmaps)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(roadmaps);

        return proposal.TargetTitle is { Length: > 0 } target
            ? roadmaps.FirstOrDefault(r => string.Equals(r.Title, target, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
