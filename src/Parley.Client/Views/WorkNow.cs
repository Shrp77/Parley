using Parley.Core;

namespace Parley.Client.Views;

/// <summary>One exchange an agent owes an answer on, as one project's broker holds it.</summary>
/// <param name="Exchange">The exchange.</param>
/// <param name="Owner">The agent that owes the answer.</param>
/// <param name="Conversation">The conversation it is in.</param>
/// <param name="Ask">What was asked, as written; empty when it could not be read.</param>
/// <param name="Sent">When the exchange's Request was sent, which is the order work arrived in.</param>
/// <param name="Collected">Whether the agent has collected the latest message on it addressed to it.</param>
/// <param name="AnswerWaits">Whether that latest message answers a question the agent asked, so the work was collected
/// before and an answer to its question is what waits.</param>
public sealed record OwedWork(
    long Exchange, AgentName Owner, string Conversation, string Ask, DateTimeOffset Sent, bool Collected, bool AnswerWaits = false);

/// <summary>What one project's broker returned, for the picture of work under way.</summary>
/// <param name="Project">The project.</param>
/// <param name="Agents">Its registry, or null when it could not be read.</param>
/// <param name="Owed">What its working agents owe.</param>
/// <param name="Problem">Why something could not be read, or null.</param>
public sealed record WorkInput(string Project, IReadOnlyList<AgentRecord>? Agents, IReadOnlyList<OwedWork> Owed, string? Problem);

/// <summary>One piece of work in the picture.</summary>
/// <param name="Project">The project it is in.</param>
/// <param name="Conversation">The conversation it is in.</param>
/// <param name="Exchange">The exchange.</param>
/// <param name="Ask">What was asked, on one line and cut to fit; empty when it could not be read.</param>
/// <param name="Whole">What was asked, for the tooltip and the list.</param>
/// <param name="Sent">When it arrived.</param>
/// <param name="AnswerWaits">Whether an answer to the agent's own question on it is what waits.</param>
public sealed record WorkItem(string Project, string Conversation, long Exchange, string Ask, string Whole, DateTimeOffset Sent, bool AnswerWaits);

/// <summary>Where one agent serves, for its Freeze and its page.</summary>
/// <param name="Project">The project.</param>
/// <param name="Name">Its name in that project, which may differ from its name in another.</param>
/// <param name="Frozen">Whether a person froze it there.</param>
public sealed record AgentPost(string Project, string Name, bool Frozen);

/// <summary>An agent at work, with what it holds, what waits for it and what it runs in the background.</summary>
/// <param name="Name">The agent's name where it is busiest.</param>
/// <param name="Now">Its standing, the busiest across the projects it serves.</param>
/// <param name="Current">Work it has collected and not yet answered.</param>
/// <param name="Queued">Work sent to it that it has not collected, oldest first.</param>
/// <param name="Background">How many background tasks its node last said it has running (SPEC.md §11.6).</param>
/// <param name="Posts">The projects it serves.</param>
/// <param name="Folded">Whether the person folded it, so nothing under it is drawn; the list still holds it all.</param>
/// <param name="Tasks">Each background task by name, as far as its node named them: the agent's words where it gave
/// them, else the runner's. Fewer than <paramref name="Background"/> when the node named fewer.</param>
/// <param name="TasksProject">The project whose broker named them, so a task's link to a conversation or a file is
/// opened there; a conversation's name is unique only within its project.</param>
public sealed record WorkingAgent(
    string Name,
    AgentNow Now,
    IReadOnlyList<WorkItem> Current,
    IReadOnlyList<WorkItem> Queued,
    int Background,
    IReadOnlyList<AgentPost> Posts,
    bool Folded = false,
    IReadOnlyList<BackgroundTask>? Tasks = null,
    string TasksProject = "")
{
    /// <summary>The task drawn as the background box at <paramref name="index"/>, or null when the node did not name one
    /// there and a surface draws a plain stand-in.</summary>
    /// <param name="index">Which box, from zero.</param>
    /// <returns>The task, or null.</returns>
    public BackgroundTask? Task(int index) => Tasks is { } tasks && index < tasks.Count ? tasks[index] : null;
}

/// <summary>What a box in the picture stands for.</summary>
public enum WorkBoxKind
{
    /// <summary>Parley itself, at the top.</summary>
    Root,

    /// <summary>An agent at work.</summary>
    Agent,

    /// <summary>Work the agent holds, or a box saying it holds none.</summary>
    Now,

    /// <summary>Work waiting for the agent.</summary>
    Queued,

    /// <summary>One background task.</summary>
    Task,

    /// <summary>Held work there was no room to draw, counted.</summary>
    MoreHeld,

    /// <summary>Queued work there was no room to draw, counted.</summary>
    MoreQueued,

    /// <summary>Background tasks there was no room to draw, counted.</summary>
    MoreTasks
}

/// <summary>A box in the drawn picture.</summary>
/// <param name="Kind">What it stands for.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Agent">The agent it belongs to; null for the root.</param>
/// <param name="Index">Which of the agent's items it is, in its kind; for a box that counts, how many it stands for.</param>
public sealed record WorkBox(WorkBoxKind Kind, int X, int Y, int Width, int Height, int? Agent, int Index);

/// <summary>A line between boxes, as an SVG path.</summary>
/// <param name="Kind"><c>agent</c> for the line joining agents, <c>now</c> for an agent's work, <c>task</c> for its
/// background tasks; the stylesheet draws each its own way.</param>
/// <param name="Path">The SVG path.</param>
public sealed record WorkEdge(string Kind, string Path);

/// <summary>A project that could not be read, or not in full.</summary>
/// <param name="Project">The project.</param>
/// <param name="Problem">Why, as the read said.</param>
/// <param name="Unreachable">Whether nothing of it could be read (its registry), rather than only part.</param>
public sealed record ProjectProblem(string Project, string Problem, bool Unreachable);

/// <summary>The picture of what is being worked on now.</summary>
/// <param name="Working">The agents at work.</param>
/// <param name="Hidden">How many agents are not at work, and are left out.</param>
/// <param name="Projects">Every project the console serves, for the filter.</param>
/// <param name="Agents">Every agent name that could be at work, for the filter.</param>
/// <param name="Problems">Each project that could not be read, or not in full.</param>
/// <param name="Boxes">Where each box is drawn.</param>
/// <param name="Edges">The lines between them.</param>
/// <param name="Width">The picture's width.</param>
/// <param name="Height">The picture's height.</param>
public sealed record WorkPicture(
    IReadOnlyList<WorkingAgent> Working,
    int Hidden,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Agents,
    IReadOnlyList<ProjectProblem> Problems,
    IReadOnlyList<WorkBox> Boxes,
    IReadOnlyList<WorkEdge> Edges,
    int Width,
    int Height);

/// <summary>Builds the picture of what is being worked on now from each project's registry and what its agents owe.</summary>
/// <remarks>
/// <para>
/// <b>Only work under way is shown.</b> The picture is for what Parley is doing now; which agents are in which project is
/// read better on the agents' pages. An agent is in it while a turn runs or its node says it has background work running;
/// every other agent is counted as hidden.
/// </para>
/// <para>
/// <b>What it holds is a reading, not a report.</b> A turn does not say which conversation it is for (SPEC.md §11.6), so
/// what an agent is shown holding is what it collected and has not answered, and what is queued is what was sent to it and
/// not collected, in the order it arrived. Parley does not decide the order an agent takes its work in.
/// </para>
/// <para>
/// <b>One agent across projects is one repository on one machine</b> (SPEC.md §4.1.1), not one name: two repositories
/// that share a name in two projects are two agents, and one repository named differently in two projects is one. A row
/// with no identity can only be matched by name. The rule is the one a freeze uses to find an agent in every project.
/// </para>
/// </remarks>
public static class WorkNow
{
    /// <summary>The picture's width with two columns under each agent.</summary>
    public const int Wide = 640;

    /// <summary>The picture's width with one column, for a panel as narrow as a phone.</summary>
    /// <remarks>Fitted to a phone's width, two columns would draw text too small to read.</remarks>
    public const int Narrow = 340;

    /// <summary>The most held items drawn under one agent; the rest are counted.</summary>
    public const int MostHeld = 3;

    /// <summary>The most queued items drawn under one agent; the rest are counted.</summary>
    public const int MostQueued = 5;

    /// <summary>The most background tasks drawn under one agent; the rest are counted in a box that opens the agent's page,
    /// where they are listed in full.</summary>
    /// <remarks>Five, as the person asked (exchange #58): a picture the person drags cannot scroll a list inside a box, so
    /// the long list belongs on the page.</remarks>
    public const int MostTasks = 5;

    /// <summary>The most characters of an ask drawn on a box.</summary>
    public const int AskShown = 46;

    /// <summary>The most characters of a name drawn on a box.</summary>
    public const int NameShown = 34;

    /// <summary>The most characters of an ask kept for the tooltip and the list.</summary>
    public const int WholeShown = 2000;

    private const int Column = 290;
    private const int Left = 20;
    private const int Right = Wide - Left - Column;

    /// <summary>Builds the picture.</summary>
    /// <param name="inputs">What each project's broker returned.</param>
    /// <param name="now">Now.</param>
    /// <param name="projects">The projects chosen in the filter; none chosen is every project.</param>
    /// <param name="agents">The agent names chosen in the filter; none chosen is every agent.</param>
    /// <param name="narrow">Whether to draw one column, for a narrow panel.</param>
    /// <param name="folded">The agent names the person folded, so only their own box is drawn.</param>
    /// <returns>The picture.</returns>
    public static WorkPicture Build(
        IReadOnlyList<WorkInput> inputs,
        DateTimeOffset now,
        IReadOnlyCollection<string>? projects = null,
        IReadOnlyCollection<string>? agents = null,
        bool narrow = false,
        IReadOnlyCollection<string>? folded = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var allProjects = inputs.Select(i => i.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var allAgents = inputs
            .SelectMany(i => i.Agents ?? [])
            .Where(Counts)
            .Select(a => a.Name.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var problems = inputs
            .Where(i => i.Problem is not null)
            .Select(i => new ProjectProblem(i.Project, i.Problem!, i.Agents is null))
            .ToList();

        var rows = inputs
            .Where(i => projects is not { Count: > 0 } || projects.Contains(i.Project))
            .SelectMany(i => (i.Agents ?? []).Where(Counts).Select(a => (Input: i, Agent: a)))
            .Where(r => agents is not { Count: > 0 } || agents.Contains(r.Agent.Name.Value))
            .GroupBy(r => Key(r.Agent));

        var working = new List<WorkingAgent>();
        var hidden = 0;

        foreach (var group in rows)
        {
            var standings = group.Select(r => (r.Input, r.Agent, Now: Activities.Now(r.Agent, now))).ToList();

            if (!standings.Any(s => AtWork(s.Now)))
            {
                hidden++;

                continue;
            }

            var best = standings.OrderBy(s => s.Now.Working ? 0 : 1).ThenBy(s => s.Now.Rank).First();
            var owed = standings
                .SelectMany(s => s.Input.Owed.Where(o => o.Owner == s.Agent.Name).Select(o => (s.Input.Project, Owed: o)))
                .ToList();

            // One process, so the same tasks in every project that serves it; the agent's own words are in whichever
            // project it said them through, so the project naming the most, and saying most about them, is drawn.
            var named = standings
                .OrderByDescending(s => s.Agent.Turns?.Named.Count ?? 0)
                .ThenByDescending(s => s.Agent.Turns?.Named.Count(t => t.Description.Length > 0 || t.Link is not null) ?? 0)
                .ThenBy(s => s.Input.Project, StringComparer.Ordinal)
                .First();

            working.Add(new WorkingAgent(
                best.Agent.Name.Value,
                best.Now,
                [.. owed.Where(o => o.Owed.Collected).OrderBy(o => o.Owed.Sent).Select(o => Item(o.Project, o.Owed))],
                [.. owed.Where(o => !o.Owed.Collected).OrderBy(o => o.Owed.Sent).Select(o => Item(o.Project, o.Owed))],
                standings.Max(s => s.Agent.Turns?.BackgroundTasks ?? 0),
                [.. standings
                    .Select(s => new AgentPost(s.Input.Project, s.Agent.Name.Value, s.Agent.Frozen))
                    .OrderBy(p => string.Equals(p.Project, best.Input.Project, StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(p => p.Project, StringComparer.Ordinal)],
                folded is { Count: > 0 } && folded.Contains(best.Agent.Name.Value),
                named.Agent.Turns?.Named ?? [],
                named.Input.Project));
        }

        working.Sort((a, b) =>
        {
            var byWork = (a.Now.Working ? 0 : 1).CompareTo(b.Now.Working ? 0 : 1);

            return byWork != 0 ? byWork : string.CompareOrdinal(a.Name, b.Name);
        });

        var (boxes, edges, height) = narrow ? LayNarrow(working) : LayWide(working);

        return new WorkPicture(working, hidden, allProjects, allAgents, problems, boxes, edges, narrow ? Narrow : Wide, height);
    }

    /// <summary>Cuts what was asked to one line that fits a box.</summary>
    /// <param name="ask">What was asked.</param>
    /// <returns>Its first line, each unprintable character a space, cut at <see cref="AskShown"/> characters with an ellipsis.</returns>
    public static string AskLine(string ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        return Shorten(HistoryText.Neutralised(ask.Split('\n', 2)[0]).Trim(), AskShown);
    }

    /// <summary>Cuts a name to what fits on a box.</summary>
    /// <param name="name">An agent's or a conversation's name.</param>
    /// <returns>The name, cut at <see cref="NameShown"/> characters with an ellipsis.</returns>
    /// <remarks>The whole name is in the box's tooltip and the list.</remarks>
    public static string NameLine(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Shorten(name, NameShown);
    }

    /// <summary>Whether an agent is at work: a turn runs, or its node says background work runs.</summary>
    /// <param name="agent">The agent's registry row.</param>
    /// <param name="now">Now.</param>
    /// <returns>True when the picture shows it.</returns>
    /// <remarks>Lets a reader ask a broker what an agent owes only for the agents the picture will show.</remarks>
    public static bool IsAtWork(AgentRecord agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return Counts(agent) && AtWork(Activities.Now(agent, now));
    }

    private static string Shorten(string text, int most)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var cut = 0;
        var count = 0;

        while (e.MoveNext())
        {
            if (++count > most - 1)
            {
                return string.Concat(text.AsSpan(0, cut), "…");
            }

            cut = e.ElementIndex + e.GetTextElement().Length;
        }

        return text;
    }

    /// <summary>What makes two rows one agent: its identity and machine, or its name when it presented no identity.</summary>
    private static (AgentIdentity Identity, string Who) Key(AgentRecord agent) =>
        agent.Identity.Known ? (agent.Identity, agent.Name.Machine) : (default, agent.Name.Value);

    private static bool Counts(AgentRecord agent) =>
        !agent.Facilitator && !PersonSeat.Is(agent.Name) && agent.Status != AgentStatus.Retired;

    private static bool AtWork(AgentNow now) =>
        now.Working || string.Equals(now.Mark, Activities.BackgroundMark, StringComparison.Ordinal);

    private static WorkItem Item(string project, OwedWork owed) =>
        new(project, owed.Conversation, owed.Exchange, AskLine(owed.Ask), Whole(owed.Ask), owed.Sent, owed.AnswerWaits);

    /// <summary>What was asked, for the tooltip and the list: neutralised, and cut where a message is long, since every
    /// refresh of every open panel draws it.</summary>
    private static string Whole(string ask)
    {
        var cut = HistoryText.Cut(ask, WholeShown);

        return HistoryText.Neutralised(cut.Length < ask.Length ? cut + "…" : cut);
    }

    /// <summary>The boxes under one agent, in order, with how tall each is: what it holds, what waits, then its background
    /// tasks, each kind bounded and the rest counted.</summary>
    private static IEnumerable<(WorkBoxKind Kind, int Height, int Index)> Under(WorkingAgent agent, bool tasks)
    {
        if (agent.Folded)
        {
            yield break;
        }

        if (!tasks)
        {
            for (var i = 0; i < Math.Max(1, Math.Min(agent.Current.Count, MostHeld)); i++)
            {
                yield return (WorkBoxKind.Now, 70, i);
            }

            if (agent.Current.Count > MostHeld)
            {
                yield return (WorkBoxKind.MoreHeld, 30, agent.Current.Count - MostHeld);
            }

            for (var i = 0; i < Math.Min(agent.Queued.Count, MostQueued); i++)
            {
                yield return (WorkBoxKind.Queued, 66, i);
            }

            if (agent.Queued.Count > MostQueued)
            {
                yield return (WorkBoxKind.MoreQueued, 30, agent.Queued.Count - MostQueued);
            }

            yield break;
        }

        for (var i = 0; i < Math.Min(agent.Background, MostTasks); i++)
        {
            yield return (WorkBoxKind.Task, 44, i);
        }

        if (agent.Background > MostTasks)
        {
            yield return (WorkBoxKind.MoreTasks, 30, agent.Background - MostTasks);
        }
    }

    /// <summary>Lays the picture out in one column: Parley, then each agent with its work and then its background tasks
    /// under it, joined by a line down the left margin.</summary>
    private static (List<WorkBox> Boxes, List<WorkEdge> Edges, int Height) LayNarrow(List<WorkingAgent> working)
    {
        var boxes = new List<WorkBox>();
        var edges = new List<WorkEdge>();
        const int rail = 32;
        const int itemX = 44;
        const int itemW = Narrow - itemX - 12;

        boxes.Add(new WorkBox(WorkBoxKind.Root, 20, 16, 240, 52, null, 0));
        var y = 100;
        var from = 42;

        for (var a = 0; a < working.Count; a++)
        {
            var agent = working[a];

            edges.Add(new WorkEdge("agent", $"M 20 {from} L 10 {from} L 10 {y + 30} L 20 {y + 30}"));
            boxes.Add(new WorkBox(WorkBoxKind.Agent, 20, y, Narrow - 32, 60, a, 0));

            var iy = y + 60 + 16;

            foreach (var tasks in new[] { false, true })
            {
                var path = new System.Text.StringBuilder(tasks ? $"M {rail} {iy - 12}" : $"M {rail} {y + 60}");
                var any = false;

                foreach (var (kind, height, index) in Under(agent, tasks))
                {
                    boxes.Add(new WorkBox(kind, itemX, iy, itemW, height, a, index));
                    path.Append($" L {rail} {iy + 20} L {itemX} {iy + 20} M {rail} {iy + 20}");
                    iy += height + 12;
                    any = true;
                }

                if (any)
                {
                    edges.Add(new WorkEdge(tasks ? "task" : "now", path.ToString()));
                }
            }

            from = y + 30;
            y = agent.Folded ? y + 60 + 24 : iy + 28;
        }

        return (boxes, edges, Math.Max(y - 16, 100));
    }

    /// <summary>Lays the picture out top to bottom: Parley, then each agent with its work down the left and its background
    /// tasks down the right.</summary>
    private static (List<WorkBox> Boxes, List<WorkEdge> Edges, int Height) LayWide(List<WorkingAgent> working)
    {
        var boxes = new List<WorkBox>();
        var edges = new List<WorkEdge>();
        const int rootW = 240;
        const int agentW = 320;
        const int mid = Wide / 2;

        boxes.Add(new WorkBox(WorkBoxKind.Root, mid - (rootW / 2), 16, rootW, 52, null, 0));
        var y = 116;
        var spineFrom = 68;

        for (var a = 0; a < working.Count; a++)
        {
            var agent = working[a];

            edges.Add(new WorkEdge("agent", $"M {mid} {spineFrom} L {mid} {y}"));
            boxes.Add(new WorkBox(WorkBoxKind.Agent, mid - (agentW / 2), y, agentW, 60, a, 0));

            var top = y + 60 + 36;
            var bottoms = new int[2];

            // Down the left what it holds and what waits for it; down the right its background tasks.
            foreach (var tasks in new[] { false, true })
            {
                var x = tasks ? Right : Left;
                var gap = tasks ? 12 : 14;
                var at = top;

                foreach (var (kind, height, index) in Under(agent, tasks))
                {
                    boxes.Add(new WorkBox(kind, x, at, Column, height, a, index));
                    at += height + gap;
                }

                if (at > top)
                {
                    var turn = tasks ? mid + 60 : mid - 60;
                    var down = x + 24;
                    edges.Add(new WorkEdge(tasks ? "task" : "now", $"M {turn} {y + 60} L {turn} {y + 78} L {down} {y + 78} L {down} {at - gap}"));
                }

                bottoms[tasks ? 1 : 0] = at;
            }

            spineFrom = y + 60;
            // A folded agent has nothing under it, so the next one follows close.
            y = agent.Folded ? y + 60 + 36 : Math.Max(bottoms[0], bottoms[1]) + 40;
        }

        return (boxes, edges, Math.Max(y - 20, 100));
    }
}
