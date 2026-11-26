using Parley.Configuration;
using Parley.Core;
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
                "   https://tickets.example.com/SHOP-12 (tickets.example.com)",
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
    }

    [Fact]
    public void An_agent_with_nothing_running_says_so()
    {
        Assert.Equal(["Nothing is running in the background."], BackgroundWorkView.Lines(TurnStanding.Unreported));
    }
}
