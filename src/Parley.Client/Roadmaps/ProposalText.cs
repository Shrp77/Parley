using System.Globalization;
using Parley.Core;

namespace Parley.Client.Roadmaps;

/// <summary>
/// What a waiting proposal says to the person who has to decide it, in the words every surface uses (SPEC.md §5.7).
/// </summary>
/// <remarks>
/// <para>
/// One description of a proposal: the terminal's dialog draws all of it, and the web page's list takes its row and its
/// match against a roadmap from here, so the two never word a proposal differently. The words of the proposal are the agent's, so a
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

        var count = proposal.ItemCount ?? proposal.Content.Items.Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{proposal.Content.Title} — {proposal.Proposer.Role}, {count} item{(count == 1 ? string.Empty : "s")}");
    }

    /// <summary>What heads a proposal: who put it forward and from where, its title, and what it says it would change.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="sameTitle">The title of a live roadmap that matches the one it says it would change, if there is one.</param>
    /// <returns>The lines, which are an agent's words and are not safe to draw as they are.</returns>
    /// <remarks>
    /// Separate from the items and from the agents because a surface that shows them apart — the terminal draws the
    /// items on the left and who is named on the right — needs each block on its own. Who proposed it comes first, so a
    /// long title cannot push it out of view, and the title next, because it is what a confirm records; the roadmap it
    /// says it would change is last and quoted short, so it cannot push the title out of a heading of a few rows.
    /// </remarks>
    public static string Header(RoadmapProposal proposal, string? sameTitle = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var lines = new List<string>
        {
            $"Proposed by {proposal.Proposer.Value}" + (proposal.SourceConversation is { Length: > 0 } source
                ? $", from the conversation {source}."
                : "."),
        };

        lines.Add(proposal.Content.Title);

        if (proposal.TargetTitle is { Length: > 0 } target)
        {
            lines.Add(sameTitle is null
                ? $"It says it would change a roadmap called '{AgentWords.Shortened(target)}'; there is none by that name."
                : $"It says it would change the roadmap '{AgentWords.Shortened(sameTitle)}'. You choose whether it does.");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The items in order, each with the agent it is meant for and what it needs on a line of its own.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <returns>The text, which is an agent's words and is not safe to draw as it is.</returns>
    /// <remarks>
    /// The facts about an item sit under it rather than after it, so a long item wraps without carrying its agent off
    /// the screen: which agent is responsible for what is the thing this view is read for.
    /// </remarks>
    public static string Items(RoadmapProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var lines = new List<string>();

        for (var at = 0; at < proposal.Content.Items.Count; at++)
        {
            var item = proposal.Content.Items[at];

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{at + 1}. {item.Text}"));

            var facts = new List<string>();

            if (item.IntendedAgent is { } agent)
            {
                facts.Add($"for {agent.Value}");
            }

            if (item.Needs.Count > 0)
            {
                facts.Add("needs " + string.Join(", ", item.Needs.Order().Select(n => n.ToString(CultureInfo.InvariantCulture))));
            }

            if (facts.Count > 0)
            {
                lines.Add("     " + string.Join(" · ", facts));
            }

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines).TrimEnd();
    }

    /// <summary>Who the proposal names, and which items each is meant for.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <returns>The text, which is an agent's words and is not safe to draw as it is.</returns>
    /// <remarks>
    /// <para>
    /// Grouped by agent, so one agent meant for several items reads as one entry listing them, and the items beside it
    /// say the same thing from the other side. Items that name no agent are said to, never left out: an item nobody is
    /// named for is a fact about the proposal.
    /// </para>
    /// <para>
    /// A proposal names an agent for an item, nothing more. It assigns nobody, and the text says so where it is read.
    /// </para>
    /// </remarks>
    public static string Agents(RoadmapProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var named = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        var unnamed = new List<int>();

        for (var at = 0; at < proposal.Content.Items.Count; at++)
        {
            if (proposal.Content.Items[at].IntendedAgent is { } agent)
            {
                if (!named.TryGetValue(agent.Value, out var items))
                {
                    named[agent.Value] = items = [];
                }

                items.Add(at + 1);
            }
            else
            {
                unnamed.Add(at + 1);
            }
        }

        var lines = new List<string>();

        foreach (var (agent, items) in named)
        {
            lines.Add(agent);
            lines.Add("  " + Numbers(items));
            lines.Add(string.Empty);
        }

        if (unnamed.Count > 0)
        {
            lines.Add(named.Count == 0 ? "No agent is named." : "No agent named for");
            lines.Add("  " + Numbers(unnamed));
        }

        return string.Join('\n', lines).TrimEnd();
    }

    private static string Numbers(IReadOnlyList<int> items) =>
        (items.Count == 1 ? "item " : "items ")
        + string.Join(", ", items.Select(n => n.ToString(CultureInfo.InvariantCulture)));

    /// <summary>What a revision does to the roadmap's items, said wherever a person chooses one.</summary>
    /// <remarks>One sentence for both surfaces, so neither tells the person less: the codec carries a numbered item across.</remarks>
    public const string RevisionReplaces =
        "A revision replaces every item that roadmap has with these, except one whose line carries | item: n, where n is "
        + "an item that roadmap has, which keeps that item and its conversation; every other item it had is removed, and "
        + "its conversation stays. A new roadmap leaves it alone.";

    /// <summary>What confirming a proposal as a revision of a roadmap also does to the other proposals waiting on it.</summary>
    /// <param name="proposal">The proposal being confirmed.</param>
    /// <param name="roadmap">The roadmap it would revise.</param>
    /// <param name="waiting">The headings of the proposals awaiting a decision, or <see langword="null"/> when unread.</param>
    /// <returns>The sentence, or an empty string when no other proposal waits on it.</returns>
    /// <remarks>
    /// A revision supersedes every other waiting proposal whose whole hint names the roadmap, ignoring case (SPEC.md
    /// §5.7), and a superseded proposal is shown nowhere after, so the door says so before the person chooses. The
    /// headings are the newest <see cref="RoadmapProposal.HeadingLimit"/> with hints cut to
    /// <see cref="RoadmapProposal.HeadingLength"/>, so a number is given only when they are certainly all of them. When
    /// they were not read, or the title is at least as long as a cut hint (a longer hint cut could then equal it), the
    /// sentence says any such proposal goes. When the list is full it says at least how many, or any when it counted
    /// none. Otherwise it gives the number. Unread is not none, and neither is a count that could be short or long.
    /// </remarks>
    public static string Superseding(RoadmapProposal proposal, RoadmapSummary roadmap, IReadOnlyList<RoadmapProposal>? waiting)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(roadmap);

        const string Any = "A revision also settles any other proposal waiting to revise this roadmap, as superseded, and tells its proposer.";

        if (waiting is null || roadmap.Title.Length >= RoadmapProposal.HeadingLength)
        {
            return Any;
        }

        var others = waiting.Count(p =>
            p.Id != proposal.Id && string.Equals(p.TargetTitle, roadmap.Title, StringComparison.OrdinalIgnoreCase));
        var atLeast = waiting.Count >= RoadmapProposal.HeadingLimit;

        if (others == 0)
        {
            return atLeast ? Any : string.Empty;
        }

        return "A revision also settles " + (atLeast ? "at least " : string.Empty)
            + others.ToString(CultureInfo.InvariantCulture) + (others == 1 ? " other proposal" : " other proposals")
            + " waiting to revise this roadmap, as superseded, and tells "
            + (others == 1 && !atLeast ? "its proposer." : "their proposers.");
    }

    /// <summary>The roadmap a proposal's title hint matches, if any.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="roadmaps">The project's roadmaps.</param>
    /// <returns>The match, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A hint and nothing more: shown so a person can choose, never used to decide for them.
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
