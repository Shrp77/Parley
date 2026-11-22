using Parley.Client.Rendering;
using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>
/// What the graph says: which agent is in which conversation of which project, which was spun off from which, which is frozen.
/// </summary>
/// <remarks>
/// <para>
/// The picture is drawn from this derivation, so what is frozen here is what a person reads from it. Positions are checked only
/// for what a reader depends on: nothing overlaps, and every edge reaches nodes that exist.
/// </para>
/// <para>
/// <b>It claims no more than the broker said.</b> A turn report does not name a conversation (SPEC.md §11.6), so an agent is
/// joined to the conversations it is a participant in and the graph never says what it is working on.
/// </para>
/// </remarks>
[Unit]
public sealed class AgentGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static AgentRecord Agent(string name, TurnStanding? turns = null, DateTimeOffset? frozen = null,
        AgentStatus status = AgentStatus.Active, string adapter = "mcp") =>
        new(AgentName.Create(name), adapter, AgentTier.Full, null, null, null, true, status,
            Presence: AgentPresence.Running, Turns: turns, FrozenUtc: frozen);

    private static ConversationSummary Conversation(string id, string[] who, bool open = false, bool closed = false,
        bool archived = false, string? parent = null, int minutesAgo = 5) =>
        new(id, [.. who.Select(AgentName.Create)], 3, Now.AddHours(-1), Now.AddMinutes(-minutesAgo), open, archived,
            Parent: parent, Closed: closed);

    private static AgentGraph Graph(params GraphInput[] inputs) => AgentGraphBuilder.Build(inputs);

    [Fact]
    public void A_project_an_agent_and_the_conversation_it_is_in_are_joined()
    {
        var graph = Graph(new GraphInput(
            "alpha",
            [Agent("orders@pc"), Agent("billing@pc")],
            [Conversation("pricing", ["orders@pc"], open: true)],
            null));

        Assert.Equal(1, graph.Nodes.Count(n => n.Kind == GraphKind.Project));
        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == GraphKind.Agent));
        Assert.Equal(1, graph.Nodes.Count(n => n.Kind == GraphKind.Conversation));
        Assert.Contains(graph.Edges, e => e is { Kind: "in", From: "p:alpha", To: "a:alpha:orders@pc" });
        Assert.Contains(graph.Edges, e => e is { Kind: "party", From: "a:alpha:orders@pc", To: "c:alpha:pricing" });
        Assert.DoesNotContain(graph.Edges, e => e is { Kind: "party", From: "a:alpha:billing@pc" });
    }

    [Fact]
    public void A_spin_off_is_joined_to_its_parent_and_only_when_the_parent_is_drawn()
    {
        var graph = Graph(new GraphInput(
            "alpha",
            [Agent("orders@pc")],
            [Conversation("parent", ["orders@pc"]), Conversation("child", ["orders@pc"], parent: "parent"),
             Conversation("orphan", ["orders@pc"], parent: "elsewhere")],
            null));

        Assert.Contains(graph.Edges, e => e is { Kind: "spinoff", From: "c:alpha:child", To: "c:alpha:parent" });
        Assert.Equal(1, graph.Edges.Count(e => e.Kind == "spinoff"));
    }

    [Fact]
    public void An_agent_carries_its_standing_and_its_background_tasks_and_a_freeze_outranks_both()
    {
        var turns = new TurnStanding(true, null, null, true, 0, 3);

        var graph = Graph(new GraphInput(
            "alpha",
            [Agent("waits@pc", turns), Agent("frozen@pc", turns, frozen: Now)],
            [],
            null));

        var waits = graph.Nodes.Single(n => n.Full == "waits@pc");
        var frozen = graph.Nodes.Single(n => n.Full == "frozen@pc");

        Assert.Equal(3, waits.Background);
        Assert.False(waits.Frozen);
        Assert.Contains("background", waits.State, StringComparison.Ordinal);
        Assert.True(frozen.Frozen);
        Assert.Equal(Standing.Freezing, frozen.State);
    }

    [Theory]
    [InlineData(AgentPresence.Startable, Standing.Idle)]
    [InlineData(AgentPresence.Stranded, Standing.NoMachine)]
    public void A_stopped_agent_reads_whether_a_node_can_start_it(AgentPresence presence, string phrase)
    {
        // With no catalogue the graph read every stopped agent as "only runs when somebody opens it", untrue of one a node
        // can start and of one whose machine is away (review, lens 1 run 3 on 6fde4ef3).
        var stopped = new AgentRecord(AgentName.Create("orders@pc"), "mcp", AgentTier.Full, null, null, null, false,
            AgentStatus.Active, Presence: presence);

        var graph = Graph(new GraphInput("alpha", [stopped], [], null));

        Assert.Equal(phrase, graph.Nodes.Single(n => n.Kind == GraphKind.Agent).State);
    }

    [Fact]
    public void A_frozen_agent_that_is_no_longer_running_reads_frozen()
    {
        var stopped = new AgentRecord(AgentName.Create("frozen@pc"), "mcp", AgentTier.Full, null, null, null, false,
            AgentStatus.Active, Presence: AgentPresence.Stranded, FrozenUtc: Now);

        var graph = Graph(new GraphInput("alpha", [stopped], [], null));

        Assert.Equal(Standing.Frozen, graph.Nodes.Single(n => n.Kind == GraphKind.Agent).State);
    }

    [Fact]
    public void Freeze_is_offered_for_an_agent_and_never_for_the_persons_seat_or_a_retired_agent_and_the_facilitator_is_not_drawn()
    {
        var seat = Agent($"{PersonSeat.Role}@alpha", adapter: PersonSeat.Adapter);
        var retired = Agent("old@pc", status: AgentStatus.Retired);
        var facilitator = Agent($"{Facilitation.Role}@alpha", adapter: Facilitation.Adapter);

        var graph = Graph(new GraphInput("alpha", [Agent("orders@pc"), seat, retired, facilitator], [], null));

        Assert.True(graph.Nodes.Single(n => n.Full == "orders@pc").Actionable);
        Assert.False(graph.Nodes.Single(n => n.Full == seat.Name.Value).Actionable);
        Assert.False(graph.Nodes.Single(n => n.Full == "old@pc").Actionable);
        Assert.DoesNotContain(graph.Nodes, n => n.Full == facilitator.Name.Value);
    }

    [Fact]
    public void Closed_and_archived_conversations_are_drawn_muted_and_labelled_in_words()
    {
        var graph = Graph(new GraphInput(
            "alpha",
            [Agent("orders@pc")],
            [Conversation("done", ["orders@pc"], closed: true), Conversation("filed", ["orders@pc"], archived: true),
             Conversation("live", ["orders@pc"], open: true)],
            null));

        var done = graph.Nodes.Single(n => n.Full == "done");
        var filed = graph.Nodes.Single(n => n.Full == "filed");
        var live = graph.Nodes.Single(n => n.Full == "live");

        Assert.True(done.Closed && done.Muted);
        Assert.Contains("closed", done.State, StringComparison.Ordinal);
        Assert.True(filed.Closed && filed.Muted);
        Assert.Contains("archived", filed.State, StringComparison.Ordinal);
        Assert.False(live.Closed || live.Muted);
        Assert.Contains("awaits", live.State, StringComparison.Ordinal);
    }

    [Fact]
    public void A_busy_project_draws_open_conversations_first_and_says_how_many_it_left_out()
    {
        var many = Enumerable.Range(0, AgentGraphBuilder.MostConversations + 25)
            .Select(i => Conversation($"c{i:000}", ["orders@pc"], minutesAgo: i + 1))
            .Append(Conversation("old-but-open", ["orders@pc"], open: true, minutesAgo: 60 * 24 * 90))
            .ToList();

        var graph = Graph(new GraphInput("alpha", [Agent("orders@pc")], many, null));

        Assert.Equal(AgentGraphBuilder.MostConversations, graph.Nodes.Count(n => n.Kind == GraphKind.Conversation));
        Assert.Contains(graph.Nodes, n => n.Full == "old-but-open");
        Assert.Equal(26, Assert.Single(graph.Bands).NotDrawn);
    }

    [Fact]
    public void An_unreachable_project_is_drawn_and_says_so_while_the_others_are_drawn()
    {
        var graph = Graph(
            new GraphInput("down", null, null, "it did not answer"),
            new GraphInput("up", [Agent("orders@pc")], [], null));

        Assert.Equal("it did not answer", graph.Bands[0].Problem);
        Assert.Contains("unreachable", graph.Nodes.Single(n => n.Full == "down").State, StringComparison.Ordinal);
        Assert.Single(graph.Nodes, n => n.Kind == GraphKind.Agent);
    }

    [Fact]
    public void Two_projects_are_stacked_and_no_node_overlaps_another_and_every_edge_reaches_a_node()
    {
        var graph = Graph(
            new GraphInput("alpha", [Agent("a@pc"), Agent("b@pc")], [Conversation("x", ["a@pc", "b@pc"], open: true)], null),
            new GraphInput("beta", [Agent("a@pc")], [Conversation("x", ["a@pc"]), Conversation("y", ["a@pc"], parent: "x")], null));

        var ids = graph.Nodes.Select(n => n.Id).ToHashSet();

        Assert.Equal(ids.Count, graph.Nodes.Count);
        Assert.All(graph.Edges, e => Assert.True(ids.Contains(e.From) && ids.Contains(e.To)));
        Assert.True(graph.Bands[1].Top >= graph.Bands[0].Top + graph.Bands[0].Height);

        foreach (var node in graph.Nodes)
        {
            Assert.DoesNotContain(
                graph.Nodes.Where(other => other.Id != node.Id),
                other => node.X < other.X + other.Width && other.X < node.X + node.Width
                      && node.Y < other.Y + other.Height && other.Y < node.Y + node.Height);
            Assert.InRange(node.Y + node.Height, 0, graph.Height);
        }
    }

    [Fact]
    public void The_same_conversation_name_in_two_projects_is_two_nodes()
    {
        var graph = Graph(
            new GraphInput("alpha", [], [Conversation("same", [])], null),
            new GraphInput("beta", [], [Conversation("same", [])], null));

        Assert.Equal(2, graph.Nodes.Count(n => n.Full == "same"));
    }

    [Fact]
    public void A_long_name_is_shortened_on_the_node_and_whole_in_its_full_name()
    {
        var name = new string('a', 80);
        var graph = Graph(new GraphInput("alpha", [], [Conversation(name, [])], null));
        var node = graph.Nodes.Single(n => n.Kind == GraphKind.Conversation);

        Assert.Equal(name, node.Full);
        Assert.True(node.Label.Length < name.Length);
    }

    [Fact]
    public void An_empty_deployment_draws_an_empty_graph()
    {
        var graph = Graph();

        Assert.Empty(graph.Nodes);
        Assert.Equal(0, graph.Height);
    }

    [Fact]
    public void Freezing_words_name_what_is_kept_and_what_stays_open()
    {
        Assert.Contains("stays open", FreezingWords.Freezing("orders@pc"), StringComparison.Ordinal);
        Assert.Contains("restart", FreezingWords.Freezing("orders@pc"), StringComparison.Ordinal);
        Assert.Contains("other projects", FreezingWords.FreezingAll("alpha"), StringComparison.Ordinal);
        Assert.Contains("unfreezes it", FreezingWords.Freezing("orders@pc"), StringComparison.Ordinal);
        Assert.Contains("stays frozen after a logon", FreezingWords.Freezing("orders@pc", keepFrozen: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Describing_a_freeze_names_each_project_and_says_what_became_of_the_process()
    {
        var outcome = new FreezeOutcome(
            AgentName.Create("orders@pc"),
            [new FrozenIn("alpha", true, FreezeProcess.Stopping, null), new FrozenIn("beta", false, null, "it did not answer")]);

        var said = AgentFreezing.Describe(outcome);

        Assert.Contains("alpha: changed", said, StringComparison.Ordinal);
        Assert.Contains("node was asked to stop it", said, StringComparison.Ordinal);
        Assert.Contains("beta: not done. it did not answer", said, StringComparison.Ordinal);
        Assert.False(outcome.Complete);
        Assert.True(outcome.AnyChanged);
    }

    [Fact]
    public void Describing_a_freeze_where_no_project_lists_the_agent_says_there_was_nothing_to_do()
    {
        var said = AgentFreezing.Describe(new FreezeOutcome(AgentName.Create("orders@pc"), []));

        Assert.Contains("nothing to do", said, StringComparison.Ordinal);
    }
}
