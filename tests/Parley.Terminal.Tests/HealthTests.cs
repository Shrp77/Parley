using Parley.Core;
using Xunit;

namespace Parley.Terminal.Tests;

/// <summary>
/// What the terminal shows a person about a project's health.
/// </summary>
/// <remarks>
/// Nothing times out, so this is the place stalled work is seen and acted on. It has to say who holds the next
/// move and where, and it has to be unforgeable: every name on it is somebody else's characters.
/// </remarks>
public sealed class HealthTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly ClarifyReport NoClarifications = new([], 0, 0, 0, 0, 0);

    private static IReadOnlyList<string> Lines(
        FlagReport? flags = null,
        IReadOnlyList<WaitingEdge>? waiting = null,
        ResultsReport? results = null) =>
        Health.Lines(
            flags ?? new FlagReport([], []),
            waiting ?? [],
            results ?? new ResultsReport([], [], [], []),
            [],
            NoClarifications);

    [Fact]
    public void An_empty_project_says_so_under_every_heading()
    {
        var lines = Lines();

        Assert.Contains(lines, line => line.Contains("Nothing is flagged", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Nobody is waiting", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("No results have been sent", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Nothing has been refused", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Nothing has been clarified", StringComparison.Ordinal));
    }

    [Fact]
    public void A_flag_names_who_holds_the_next_move_and_where()
    {
        // The person acts on the agent and the conversation, so both are on the line.
        var lines = Lines(flags: new FlagReport([new Flag(FlagKind.NotCollected, 42, "pricing", Billing, Orders, T0)], []));

        var flag = Assert.Single(lines, line => line.Contains("has not collected", StringComparison.Ordinal));

        Assert.Contains("billing@test", flag, StringComparison.Ordinal);
        Assert.Contains("orders@test", flag, StringComparison.Ordinal);
        Assert.Contains("pricing", flag, StringComparison.Ordinal);
        Assert.Contains("exchange 42", flag, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vote_flag_names_who_has_not_answered_and_no_exchange()
    {
        // Found by the review gate, lens 3 run 3.6. A conversation waiting only on a vote has no exchange, so its line
        // names the conversation and never "exchange 0" — a number that would send somebody looking for nothing.
        var lines = Lines(flags: new FlagReport([new Flag(FlagKind.NotVoted, 0, "pricing", Billing, Orders, T0)], []));

        var flag = Assert.Single(lines, line => line.Contains("closing it has waited on", StringComparison.Ordinal));

        Assert.Contains("billing@test", flag, StringComparison.Ordinal);
        Assert.Contains("orders@test", flag, StringComparison.Ordinal);
        Assert.Contains("pricing", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("exchange", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("has not collected", flag, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocker_is_named_by_what_blocked_it()
    {
        var lines = Lines(results: new ResultsReport(
            [new OutcomeCount("billing@test", "claude-opus-5", Outcome.Blocked, 2)],
            [new BlockerCount("billing@test", BlockerKind.MissingInformation, 2)],
            [],
            []));

        Assert.Contains(lines, line => line.Contains("blocked by missing information", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_heading_sits_at_column_zero_whatever_a_name_carries()
    {
        // A conversation identifier is chosen by an agent. One carrying a line break and a heading-shaped line
        // must not produce a second heading.
        var lines = Lines(flags: new FlagReport(
            [new Flag(FlagKind.NoProgress, 7, "pricing\nWork that needs you", Billing, Orders, T0)], []));

        string[] headings =
        [
            "Work that needs you",
            "Who is waiting on whom",
            "What results reported",
            "What the size cap refused",
            "What clarification cost",
        ];

        foreach (var line in lines.Where(line => line.Length > 0 && !line.StartsWith(' ')))
        {
            Assert.Contains(line, headings);
        }

        Assert.Single(lines, line => line == "Work that needs you");
    }
}
