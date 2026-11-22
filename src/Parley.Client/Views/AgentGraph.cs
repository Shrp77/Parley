using Parley.Core;

namespace Parley.Client.Views;

/// <summary>What a graph node stands for.</summary>
public enum GraphKind
{
    /// <summary>A project this console serves.</summary>
    Project,

    /// <summary>An agent registered in a project, or the person's seat.</summary>
    Agent,

    /// <summary>A conversation in a project.</summary>
    Conversation
}

/// <summary>One node of the graph, with where it is drawn.</summary>
/// <param name="Id">Unique across the graph: the project, then the name.</param>
/// <param name="Kind">What it stands for.</param>
/// <param name="Label">The name shown, shortened to fit.</param>
/// <param name="Full">The whole name, for the tooltip and the screen reader.</param>
/// <param name="State">What is true of it, in words: an agent's standing, a conversation's open, closed or archived.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Project">The project it belongs to.</param>
/// <param name="Muted">Whether it is drawn quietly: a closed or archived conversation, a retired agent.</param>
/// <param name="Frozen">Whether a person has frozen the agent.</param>
/// <param name="Closed">Whether a conversation has closed or been archived, so a control can hide it.</param>
/// <param name="Background">For an agent: how many background tasks its node last said it has running.</param>
/// <param name="Actionable">For an agent: whether Freeze or Unfreeze is offered (never for the person's seat or a retired agent).</param>
public sealed record GraphNode(
    string Id,
    GraphKind Kind,
    string Label,
    string Full,
    string State,
    int X,
    int Y,
    int Width,
    int Height,
    string Project,
    bool Muted = false,
    bool Frozen = false,
    bool Closed = false,
    int Background = 0,
    bool Actionable = false);

/// <summary>A line between two nodes.</summary>
/// <param name="From">The node it leaves.</param>
/// <param name="To">The node it reaches.</param>
/// <param name="Kind"><c>in</c> for a project's agent, <c>party</c> for an agent in a conversation, <c>spinoff</c> from a child to its parent.</param>
public sealed record GraphEdge(string From, string To, string Kind);

/// <summary>One project's band of the graph.</summary>
/// <param name="Name">The project.</param>
/// <param name="Top">Where the band starts.</param>
/// <param name="Height">How tall it is.</param>
/// <param name="Problem">Why it could not be read, or null.</param>
/// <param name="NotDrawn">How many conversations it has that are not drawn.</param>
public sealed record GraphBand(string Name, int Top, int Height, string? Problem, int NotDrawn);

/// <summary>What a project's broker returned, for the graph to draw.</summary>
/// <param name="Project">The project.</param>
/// <param name="Agents">Its registry, or null when it could not be read.</param>
/// <param name="Conversations">Its conversations including archived ones, or null when they could not be read.</param>
/// <param name="Problem">Why something could not be read, or null.</param>
public sealed record GraphInput(
    string Project,
    IReadOnlyList<AgentRecord>? Agents,
    IReadOnlyList<ConversationSummary>? Conversations,
    string? Problem);

/// <summary>The whole graph: nodes, edges, bands and the size to draw it at.</summary>
/// <param name="Bands">One per project.</param>
/// <param name="Nodes">Every node.</param>
/// <param name="Edges">Every line.</param>
/// <param name="Width">Total width.</param>
/// <param name="Height">Total height.</param>
public sealed record AgentGraph(
    IReadOnlyList<GraphBand> Bands,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    int Width,
    int Height);

/// <summary>Derives the graph of projects, agents and conversations from what the brokers returned (SPEC.md §10.2).</summary>
/// <remarks>
/// <para>
/// <b>Derived here and drawn in the page</b>, so what the graph says is a function a test can call: which agent is in which
/// conversation, which conversation was spun off from which, which agent is frozen.
/// </para>
/// <para>
/// <b>An agent is joined to the conversations it is a participant in, never to what it is working on.</b> A turn report does not
/// say which conversation the turn is for (SPEC.md §11.6), so "working" is said of the agent and the graph never claims more.
/// </para>
/// <para>
/// <b>Nothing is dropped silently.</b> A project draws at most <see cref="MostConversations"/> conversations, open ones first and
/// then the most recently active, and says how many it did not draw.
/// </para>
/// </remarks>
public static class AgentGraphBuilder
{
    /// <summary>The most conversations drawn for one project.</summary>
    public const int MostConversations = 60;

    private const int Row = 46;
    private const int Gap = 10;
    private const int Left = 12;
    private const int ProjectWidth = 150;
    private const int AgentX = 200;
    private const int AgentWidth = 210;
    private const int ConversationX = 470;
    private const int ConversationWidth = 220;
    private const int BandPadding = 18;
    private const int BandGap = 16;
    private const int Longest = 26;

    /// <summary>Builds the graph.</summary>
    /// <param name="inputs">What each project's broker returned, in the order to draw them.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is null.</exception>
    public static AgentGraph Build(IReadOnlyList<GraphInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var bands = new List<GraphBand>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var top = 0;

        foreach (var input in inputs)
        {
            var agents = (input.Agents ?? [])
                .Where(a => !a.Facilitator)
                .OrderBy(a => PersonSeat.Is(a.Name) ? 1 : 0)
                .ThenBy(a => a.Name.Value, StringComparer.Ordinal)
                .ToList();

            var all = input.Conversations ?? [];

            var drawn = all
                .OrderByDescending(c => c.HasOpenExchange)
                .ThenByDescending(c => c.LastActivityUtc)
                .Take(MostConversations)
                .OrderByDescending(c => c.LastActivityUtc)
                .ToList();

            var rows = Math.Max(Math.Max(agents.Count, drawn.Count), 1);
            var height = (rows * Row) + (BandPadding * 2);
            var projectId = "p:" + input.Project;

            nodes.Add(new GraphNode(
                projectId, GraphKind.Project, Shorten(input.Project), input.Project,
                input.Problem is null ? "project" : "project, unreachable",
                Left, top + BandPadding, ProjectWidth, Row - Gap + 4, input.Project, Muted: input.Problem is not null));

            var agentIds = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                var id = $"a:{input.Project}:{agent.Name.Value}";
                agentIds[agent.Name.Value] = id;

                var seat = PersonSeat.Is(agent.Name);
                var retired = agent.Status == AgentStatus.Retired;
                // Its own role as catalogued: the graph has no catalogue, and with none every stopped agent read "only runs when
                // somebody opens it", untrue of one a node can start (review, lens 1 run 3 on 6fde4ef3). Presence then says
                // whether a node can start it or its machine is not connected.
                var state = Standing.Join([agent], new HashSet<string>(StringComparer.Ordinal) { agent.Name.Role }, PersonSeat.Adapter, agent.Name.Machine ?? string.Empty)
                    .FirstOrDefault()?.Standing ?? "unknown";

                nodes.Add(new GraphNode(
                    id, GraphKind.Agent, Shorten(agent.Name.Value), agent.Name.Value, state,
                    AgentX, top + BandPadding + (i * Row), AgentWidth, Row - Gap + 4, input.Project,
                    Muted: retired,
                    Frozen: agent.Frozen,
                    Background: Math.Max(0, agent.Turns?.BackgroundTasks ?? 0),
                    Actionable: !seat && !retired));

                edges.Add(new GraphEdge(projectId, id, "in"));
            }

            var conversationIds = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < drawn.Count; i++)
            {
                var conversation = drawn[i];
                var id = $"c:{input.Project}:{conversation.Id}";
                conversationIds[conversation.Id] = id;

                var closed = conversation.Closed || conversation.Archived;

                nodes.Add(new GraphNode(
                    id, GraphKind.Conversation, Shorten(conversation.Id), conversation.Id,
                    ConversationState(conversation),
                    ConversationX, top + BandPadding + (i * Row), ConversationWidth, Row - Gap + 4, input.Project,
                    Muted: closed, Closed: closed));
            }

            foreach (var conversation in drawn)
            {
                var id = conversationIds[conversation.Id];

                foreach (var participant in conversation.Participants)
                {
                    if (agentIds.TryGetValue(participant.Value, out var from))
                    {
                        edges.Add(new GraphEdge(from, id, "party"));
                    }
                }

                if (conversation.Parent is { } parent && conversationIds.TryGetValue(parent, out var parentId))
                {
                    edges.Add(new GraphEdge(id, parentId, "spinoff"));
                }
            }

            bands.Add(new GraphBand(input.Project, top, height, input.Problem, all.Count - drawn.Count));
            top += height + BandGap;
        }

        return new AgentGraph(
            bands, nodes, edges, ConversationX + ConversationWidth + Left, Math.Max(top - BandGap, 0));
    }

    /// <summary>What is true of a conversation, in a word.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The words.</returns>
    public static string ConversationState(ConversationSummary conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var state = conversation.Closed ? "closed" : conversation.HasOpenExchange ? "open, something awaits an answer" : "open, nothing awaited";

        return conversation.Archived ? state + ", archived" : state;
    }

    private static string Shorten(string value) =>
        value.Length <= Longest ? value : string.Concat(value.AsSpan(0, Longest - 1), "…");
}
