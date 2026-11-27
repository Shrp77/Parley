using Grpc.Net.Client;
using Parley.Client;
using Parley.Client.Views;
using Parley.Configuration;
using Parley.Core;
using Terminal.Gui.Input;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>Each background task by name on the terminal: the agent's row and its Background work list (SPEC.md §11.6).</summary>
/// <remarks>
/// The row says the first task and how many more, so it says what, not only how many; Alt+B lists them all. A task the node
/// did not name is still listed, as what kind of work it is. Every word is an agent's or a runner's, made inert before it is
/// drawn.
/// </remarks>
[Integration]
public sealed class BackgroundWorkTests
{
    private static AgentRecord Agent(string name, TurnStanding turns) =>
        new AgentRecord(AgentName.Create(name), "mcp", AgentTier.Pull, "/srv", null, null, true, AgentStatus.Active,
            null, AgentPresence.Running) with { Turns = turns };

    private static TurnStanding Waiting(int count, params BackgroundTask[] named) =>
        new(true, null, DateTimeOffset.UtcNow.AddMinutes(-1), true, 0, count, named);

    [Fact]
    public void The_row_names_the_first_task_and_how_many_more()
    {
        var region = new AgentsRegion();

        region.Show(
            [Agent("billing@test", Waiting(3, new BackgroundTask("a1", TaskKind.Agent, "Checking VAT on gift cards")))],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List).Select(row => row.TrimEnd()).ToList();

        Assert.Contains(rows, row => row.StartsWith("  billing@test  — Checking VAT on gift cards, +2 more", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_with_two_tasks_names_the_first_and_one_more_and_a_row_with_one_names_only_it()
    {
        var region = new AgentsRegion();

        region.Show(
            [
                Agent("billing@test", Waiting(2, new BackgroundTask("a1", TaskKind.Agent, "Checking VAT on gift cards"))),
                Agent("orders@test", Waiting(1, new BackgroundTask("b1", TaskKind.Agent, "Running the tax tests"))),
            ],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List).Select(row => row.TrimEnd()).ToList();

        Assert.Contains(rows, row => row.StartsWith("  billing@test  — Checking VAT on gift cards, +1 more", StringComparison.Ordinal));
        var orders = Assert.Single(rows, row => row.StartsWith("  orders@test  — Running the tax tests", StringComparison.Ordinal));
        Assert.DoesNotContain("more", orders, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_waiting_on_its_background_work_names_it_even_after_a_refused_stop()
    {
        // The heading puts waiting on background work before a refused stop (AgentStanding); the row agrees, so the agent
        // under "waiting on its background work" names what it waits on.
        var region = new AgentsRegion();
        var turns = new TurnStanding(true, null, DateTimeOffset.UtcNow.AddMinutes(-3), false, 1, 1,
            [new BackgroundTask("a1", TaskKind.Agent, "Checking VAT")]);

        region.Show([Agent("billing@test", turns)], NodeCatalogue.Empty, null);

        var rows = Drawn.Rows(region.List).Select(row => row.TrimEnd()).ToList();

        Assert.Contains(rows, row => row.StartsWith("  billing@test  — Checking VAT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Alt_B_on_the_agents_row_opens_every_task_by_name_and_is_offered_only_while_there_is_work()
    {
        using var driving = Driving.Open();
        using var directory = new BrokerDirectory(
            [new ProjectSettings(ProjectName.Create("alpha"), string.Empty, "http://127.0.0.1:1", null)],
            _ => GrpcChannel.ForAddress("http://127.0.0.1:1"));
        using var shell = new Shell(directory);

        driving.Show(shell);
        await Walking.GoAsync(shell, Viewing.Project);

        shell.Agents.Show([Agent("billing@test", Waiting(0))], NodeCatalogue.Empty, null);
        shell.Focus(2);
        shell.Agents.List.SelectedItem = 1;

        Assert.DoesNotContain(shell.Actions(), a => a.Label == BackgroundWorkView.Heading);

        shell.Agents.Show(
            [Agent("billing@test", Waiting(3, new BackgroundTask("a1", TaskKind.Agent, "Checking VAT", "Reads the rules.")))],
            NodeCatalogue.Empty,
            null);
        shell.Agents.List.SelectedItem = 1;

        Assert.Contains(shell.Actions(), a => a.Label == BackgroundWorkView.Heading);

        driving.Press(Key.B.WithAlt);

        var view = Assert.IsType<BackgroundWorkView>(shell.BackgroundShown);

        Assert.Equal("Background work of billing@test: 3 tasks", view.Title);
        Assert.Equal(["1. Checking VAT (sub-agent)", "   Reads the rules.", "2. Background task", "3. Background task"], view.Shown);

        Prompting.Close(driving.App, shell.BackgroundSession);
    }

    [Fact]
    public void A_row_whose_task_has_no_name_says_what_kind_it_is()
    {
        var region = new AgentsRegion();

        region.Show([Agent("billing@test", Waiting(1))], NodeCatalogue.Empty, null);

        var rows = Drawn.Rows(region.List).Select(row => row.TrimEnd()).ToList();

        Assert.Contains(rows, row => row.StartsWith("  billing@test  — Background task", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_names_every_task_with_its_description_and_where_its_link_goes()
    {
        var lines = BackgroundWorkView.Lines(Waiting(
            4,
            new BackgroundTask("a1", TaskKind.Agent, "Checking VAT", "Reads the VAT rules.", BackgroundTaskText.Link("exchange #57")),
            new BackgroundTask("b2", TaskKind.Command, "Running the tax tests", Link: BackgroundTaskText.Link("https://tickets.example.com/SHOP-12")),
            new BackgroundTask("c3", TaskKind.Command, string.Empty)));

        Assert.Equal(
            [
                "1. Checking VAT (sub-agent)",
                "   Reads the VAT rules.",
                "   exchange #57",
                "2. Running the tax tests (command)",
                "   tickets.example.com: https://tickets.example.com/SHOP-12",
                "3. Background command",
                "4. Background task",
            ],
            lines);
    }

    [Fact]
    public void Words_that_could_break_or_move_a_line_are_made_inert()
    {
        // Held by the broker already; the terminal does not lean on it.
        var lines = BackgroundWorkView.Lines(Waiting(
            1,
            new BackgroundTask("a1", TaskKind.Agent, "Name" + (char)0x1B + "[2J", "Line" + (char)0x0A + "break")));

        Assert.All(lines, line => Assert.DoesNotContain(line, c => char.IsControl(c)));

        // The agent's row draws the first task's name too, and is made inert itself: what it is handed here has not been
        // through the client, so nothing before the row took the escape, the line feed or the line separator out.
        var region = new AgentsRegion();

        region.Show(
            [Agent("billing@test", Waiting(2, new BackgroundTask(
                "a1", TaskKind.Agent, "Name" + (char)0x1B + "[2J" + (char)0x0A + "next" + (char)0x2028 + "line")))],
            NodeCatalogue.Empty,
            null);

        var rows = Drawn.Rows(region.List);
        var billing = Assert.Single(rows, row => row.StartsWith("  billing@test  — Name", StringComparison.Ordinal));

        Assert.DoesNotContain(billing, c => char.IsControl(c) || c == (char)0x2028);
        Assert.Contains("next", billing, StringComparison.Ordinal);
        Assert.Contains("line, +1 more", billing, StringComparison.Ordinal);
    }

    [Fact]
    public void One_tasks_list_is_titled_one_task()
    {
        using var driving = Driving.Open();
        using var view = new BackgroundWorkView("billing@test", Waiting(1, new BackgroundTask("a1", TaskKind.Agent, "Checking VAT")));

        Assert.Equal("Background work of billing@test: 1 task", view.Title);
    }

    [Fact]
    public void An_agent_with_nothing_running_says_so()
    {
        Assert.Equal(["Nothing is running in the background."], BackgroundWorkView.Lines(TurnStanding.Unreported));
    }
}
