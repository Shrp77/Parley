using Parley.Cli.Top;
using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Cli.Tests.Top;

/// <summary>Lens 2 run 2.4: taking a seat under a name that reads like an agent's.</summary>
public sealed class ProbeTests
{
    /// <summary>Probe.</summary>
    [Fact]
    public void Seating_under_a_name_that_looks_taken()
    {
        // The registry holds a real agent. SeatBook refuses a name it holds for anything whose
        // adapter is not the console's -- so the question is whether a name that *renders* like
        // this one gets past a comparison that is exact.
        var registry = new List<AgentRecord>
        {
            new(
                AgentName.Create("orders@here"),
                "mcp",
                AgentTier.Pull,
                null,
                null,
                null,
                true,
                AgentStatus.Active,
                null,
                AgentPresence.Running),
        };

        var problems = new List<string>();

        foreach (var typed in new[]
                 {
                     "orders@here",       // the control: must be refused
                     " orders@here",      // leading space
                     "orders@here ",      // trailing space
                     "orders@here​", // zero-width space
                     "Orders@here",       // case
                     "orders@here.",      // trailing dot
                     "оrders@here",       // Cyrillic o
                 })
        {
            if (!Seat.TryRead(typed, out var seat, out var why))
            {
                // Refused before it ever reaches the registry check, which is the safest outcome.
                continue;
            }

            var drawn = Safe.Text(seat.Name.Value);
            var collides = drawn.Trim() == "orders@here"
                || drawn.Replace("​", string.Empty, StringComparison.Ordinal) == "orders@here";

            if (collides && !string.Equals(seat.Name.Value, "orders@here", StringComparison.Ordinal))
            {
                problems.Add(
                    $"'{typed}' was read as the seat name '{seat.Name.Value}', which draws as "
                    + $"'{drawn}' -- indistinguishable from the registered agent");
            }

            _ = why;
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
