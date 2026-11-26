using Parley.Client.Views;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>What the picture of work under way says: which agents are at work, what each holds, what waits for it and what
/// it runs in the background.</summary>
/// <remarks>
/// <para>
/// Feature 042, the person's feedback on the graph: it shows what is being worked on now, and an agent not at work is left
/// out and counted.
/// </para>
/// <para>
/// <b>It claims no more than the broker said.</b> A turn does not name a conversation (SPEC.md §11.6), so what an agent holds
/// is what it collected and has not answered, and the queue is what it has not collected, in the order it arrived.
/// </para>
/// </remarks>
[Unit]
public sealed class WorkNowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static TurnStanding Working => new(true, Now.AddMinutes(-4), null, true, 0);

    private static TurnStanding Idle => new(true, null, Now.AddMinutes(-30), true, 0);

    private static AgentRecord Agent(string name, TurnStanding? turns = null, string adapter = "claude-code",
        AgentStatus status = AgentStatus.Active, DateTimeOffset? frozen = null) =>
        new(AgentName.Create(name), adapter, AgentTier.Full, null, null, null, true, status,
            Presence: AgentPresence.Running, Turns: turns, FrozenUtc: frozen);

    private static OwedWork Owed(long exchange, string owner, string conversation, bool collected, int minutesAgo, string ask = "Price the order") =>
        new(exchange, AgentName.Create(owner), conversation, ask, Now.AddMinutes(-minutesAgo), collected);

    [Fact]
    public void Only_agents_at_work_are_shown_and_the_rest_are_counted()
    {
        var picture = WorkNow.Build(
            [new WorkInput("shop", [Agent("billing@pc", Working), Agent("orders@pc", Idle), Agent("stock@pc")], [], null)],
            Now);

        Assert.Equal("billing@pc", Assert.Single(picture.Working).Name);
        Assert.Equal(2, picture.Hidden);
        Assert.Equal(["billing@pc", "orders@pc", "stock@pc"], picture.Agents);
    }

    [Fact]
    public void Background_work_is_being_at_work_and_each_task_is_listed()
    {
        var picture = WorkNow.Build(
            [new WorkInput("shop", [Agent("orders@pc", Idle with { BackgroundTasks = 3 })], [], null)],
            Now);

        var agent = Assert.Single(picture.Working);
        Assert.Equal(3, agent.Background);
        Assert.Equal(3, picture.Boxes.Count(b => b.Kind == WorkBoxKind.Task));
    }

    [Fact]
    public void Each_background_task_carries_its_name_and_five_are_drawn_before_the_rest_are_counted()
    {
        var named = Enumerable.Range(1, 7)
            .Select(i => new BackgroundTask($"t{i}", TaskKind.Agent, $"Task {i}", i == 1 ? "Reads the VAT rules." : ""))
            .ToList();
        var picture = WorkNow.Build(
            [new WorkInput("shop", [Agent("orders@pc", Idle with { BackgroundTasks = 8, Tasks = named })], [], null)],
            Now);

        var agent = Assert.Single(picture.Working);

        Assert.Equal(8, agent.Background);
        Assert.Equal("shop", agent.TasksProject);
        Assert.Equal("Task 1", agent.Task(0)!.Name);
        Assert.Null(agent.Task(7));
        Assert.Equal(WorkNow.MostTasks, picture.Boxes.Count(b => b.Kind == WorkBoxKind.Task));
        Assert.Equal(8 - WorkNow.MostTasks, Assert.Single(picture.Boxes, b => b.Kind == WorkBoxKind.MoreTasks).Index);
    }

    [Fact]
    public void Across_projects_the_one_that_says_most_about_the_tasks_names_them()
    {
        var bare = new[] { new BackgroundTask("t1", TaskKind.Agent, "Count to three") };
        var said = new[] { new BackgroundTask("t1", TaskKind.Agent, "Checking VAT", "Reads the rules.") };
        var picture = WorkNow.Build(
            [
                new WorkInput("shop", [Agent("orders@pc", Idle with { BackgroundTasks = 1, Tasks = bare })], [], null),
                new WorkInput("store", [Agent("orders@pc", Idle with { BackgroundTasks = 1, Tasks = said })], [], null),
            ],
            Now);

        var agent = Assert.Single(picture.Working);

        Assert.Equal("store", agent.TasksProject);
        Assert.Equal("Checking VAT", agent.Task(0)!.Name);
    }

    [Fact]
    public void What_it_collected_is_what_it_holds_and_what_it_has_not_is_queued_oldest_first()
    {
        var picture = WorkNow.Build(
            [
                new WorkInput("shop", [Agent("billing@pc", Working)], [Owed(5, "billing@pc", "gift-cards", collected: true, 3)], null),
                new WorkInput("storefront", [Agent("billing@pc", Idle)], [Owed(2, "billing@pc", "returns", false, 10), Owed(9, "billing@pc", "late", false, 1)], null),
                new WorkInput("warehouse", [Agent("billing@pc", Idle)], [Owed(7, "billing@pc", "stock-sync", false, 20)], null),
            ],
            Now);

        var agent = Assert.Single(picture.Working);
        Assert.Equal(5, Assert.Single(agent.Current).Exchange);
        Assert.Equal([7L, 2L, 9L], agent.Queued.Select(q => q.Exchange));
        Assert.Equal(["warehouse", "storefront", "storefront"], agent.Queued.Select(q => q.Project));
        Assert.Equal(["shop", "storefront", "warehouse"], agent.Posts.Select(p => p.Project));
    }

    [Fact]
    public void One_agent_across_projects_is_one_repository_on_one_machine_not_one_name()
    {
        var one = AgentIdentity.Mint();
        var other = AgentIdentity.Mint();
        var picture = WorkNow.Build(
            [
                new WorkInput("shop", [Agent("orders@pc", Working) with { Identity = one }], [Owed(1, "orders@pc", "a", false, 2)], null),
                new WorkInput("warehouse", [Agent("orders@pc", Working) with { Identity = other }], [Owed(2, "orders@pc", "b", false, 1)], null),
                new WorkInput("storefront", [Agent("checkout@pc", Idle) with { Identity = one }], [], null),
            ],
            Now);

        // Two repositories sharing a name are two agents, each with only its own work.
        Assert.Equal(2, picture.Working.Count);
        Assert.All(picture.Working, a => Assert.Single(a.Queued));

        // One repository named differently in another project is the same agent, freezable there by that name.
        var shop = Assert.Single(picture.Working, a => a.Posts.Any(p => p.Project == "shop"));
        Assert.Equal(["shop", "storefront"], shop.Posts.Select(p => p.Project));
        Assert.Equal("checkout@pc", shop.Posts[1].Name);
        Assert.Equal(0, picture.Hidden);
    }

    [Fact]
    public void A_long_name_is_cut_to_fit_a_box()
    {
        var name = "orders@" + new string('m', 60);

        Assert.Equal(WorkNow.NameShown, WorkNow.NameLine(name).Length);
        Assert.EndsWith("…", WorkNow.NameLine(name), StringComparison.Ordinal);
        Assert.Equal("orders@pc", WorkNow.NameLine("orders@pc"));
    }

    [Fact]
    public void Neither_the_person_nor_Parley_nor_a_retired_agent_is_ever_in_it()
    {
        var picture = WorkNow.Build(
            [new WorkInput("shop",
                [
                    Agent("human-user@shop", Working, adapter: PersonSeat.Adapter),
                    Agent("parley-facilitator@shop", Working, adapter: Facilitation.Adapter),
                    Agent("old@pc", Working, status: AgentStatus.Retired),
                ],
                [], null)],
            Now);

        Assert.Empty(picture.Working);
        Assert.Equal(0, picture.Hidden);
        Assert.Empty(picture.Agents);
    }

    [Fact]
    public void The_filter_keeps_only_the_projects_and_agents_chosen()
    {
        var inputs = new[]
        {
            new WorkInput("shop", [Agent("billing@pc", Working), Agent("orders@pc", Working)], [Owed(1, "billing@pc", "a", false, 1)], null),
            new WorkInput("storefront", [Agent("billing@pc", Idle)], [Owed(2, "billing@pc", "b", false, 2)], null),
        };

        var byAgent = WorkNow.Build(inputs, Now, agents: ["billing@pc"]);
        Assert.Equal("billing@pc", Assert.Single(byAgent.Working).Name);

        var byProject = WorkNow.Build(inputs, Now, projects: ["storefront"]);
        Assert.Empty(byProject.Working);
        Assert.Equal(1, byProject.Hidden);

        // Both lists offered stay whole, so a choice can always be taken back.
        Assert.Equal(["shop", "storefront"], byProject.Projects);
        Assert.Equal(["billing@pc", "orders@pc"], byProject.Agents);
    }

    [Fact]
    public void An_ask_is_drawn_on_one_line_cut_to_fit_with_nothing_unprintable_in_it()
    {
        var line = WorkNow.AskLine("How should gift\u0007cards be taxed when the order\u001b[2J is split across three countries?\nSecond line");

        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.Equal(WorkNow.AskShown, line.Length);
        Assert.DoesNotContain('\u0007', line);
        Assert.DoesNotContain('\u001b', line);
        Assert.Equal("Short ask", WorkNow.AskLine("Short ask\nmore"));
    }

    [Fact]
    public void The_whole_ask_for_the_tooltip_and_the_list_is_neutralised_and_cut()
    {
        var ask = "Pay ‮evil\u001b now " + new string('x', WorkNow.WholeShown * 2);
        var picture = WorkNow.Build(
            [new WorkInput("shop", [Agent("billing@pc", Working)], [Owed(1, "billing@pc", "a", false, 1, ask)], null)],
            Now);

        var whole = Assert.Single(Assert.Single(picture.Working).Queued).Whole;
        Assert.DoesNotContain('‮', whole);
        Assert.DoesNotContain('\u001b', whole);
        Assert.Equal(WorkNow.WholeShown + 1, whole.Length);
        Assert.EndsWith("…", whole, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_the_picture_has_room_for_is_counted_not_dropped()
    {
        var owed = Enumerable.Range(1, WorkNow.MostQueued + 3).Select(n => Owed(n, "billing@pc", $"c{n}", false, n)).ToList();
        var picture = WorkNow.Build(
            [new WorkInput("shop", [Agent("billing@pc", Working with { BackgroundTasks = WorkNow.MostTasks + 2 })], owed, null)],
            Now);

        var agent = Assert.Single(picture.Working);
        Assert.Equal(WorkNow.MostQueued + 3, agent.Queued.Count);
        Assert.Equal(WorkNow.MostQueued, picture.Boxes.Count(b => b.Kind == WorkBoxKind.Queued));
        Assert.Equal(WorkNow.MostTasks, picture.Boxes.Count(b => b.Kind == WorkBoxKind.Task));
        Assert.Equal(3, Assert.Single(picture.Boxes, b => b.Kind == WorkBoxKind.MoreQueued).Index);
        Assert.Equal(2, Assert.Single(picture.Boxes, b => b.Kind == WorkBoxKind.MoreTasks).Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_folded_agent_has_nothing_drawn_under_it_and_keeps_what_it_has(bool narrow)
    {
        var inputs = new[]
        {
            new WorkInput("shop",
                [Agent("billing@pc", Working with { BackgroundTasks = 2 }), Agent("orders@pc", Working)],
                [Owed(1, "billing@pc", "returns", collected: false, 5), Owed(2, "orders@pc", "price-feed", collected: false, 3)],
                null),
        };

        var open = WorkNow.Build(inputs, Now, narrow: narrow);
        var picture = WorkNow.Build(inputs, Now, narrow: narrow, folded: ["billing@pc"]);

        var billing = picture.Working.Single(a => a.Name == "billing@pc");
        Assert.True(billing.Folded);
        Assert.False(picture.Working.Single(a => a.Name == "orders@pc").Folded);

        // Only its own box is drawn; what it has is still held for the list.
        var at = picture.Working.ToList().IndexOf(billing);
        Assert.Equal([WorkBoxKind.Agent], picture.Boxes.Where(b => b.Agent == at).Select(b => b.Kind));
        Assert.Single(billing.Queued);
        Assert.Equal(2, billing.Background);

        // The other agent's work is drawn, and the picture is shorter for the fold.
        Assert.Contains(picture.Boxes, b => b.Agent != at && b.Kind == WorkBoxKind.Queued);
        Assert.True(picture.Height < open.Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_two_boxes_overlap_and_every_box_is_inside_the_picture(bool narrow)
    {
        var owed = Enumerable.Range(10, WorkNow.MostQueued + 2).Select(n => Owed(n, "billing@pc", $"q{n}", false, n)).ToList();
        owed.AddRange([Owed(1, "billing@pc", "a", true, 1), Owed(3, "orders@pc", "c", false, 3)]);

        var picture = WorkNow.Build(
            [new WorkInput("shop",
                [Agent("billing@pc", Working with { BackgroundTasks = WorkNow.MostTasks + 1 }), Agent("orders@pc", Idle with { BackgroundTasks = 2 })],
                owed,
                null)],
            Now,
            narrow: narrow);

        Assert.Equal(narrow ? WorkNow.Narrow : WorkNow.Wide, picture.Width);

        foreach (var box in picture.Boxes)
        {
            Assert.InRange(box.X, 0, picture.Width - box.Width);
            Assert.InRange(box.Y, 0, picture.Height - box.Height);
        }

        for (var i = 0; i < picture.Boxes.Count; i++)
        {
            for (var j = i + 1; j < picture.Boxes.Count; j++)
            {
                var a = picture.Boxes[i];
                var b = picture.Boxes[j];
                var apart = a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y;

                Assert.True(apart, $"{a} overlaps {b}");
            }
        }
    }
}
