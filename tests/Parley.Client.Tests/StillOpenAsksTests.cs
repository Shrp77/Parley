using Parley.Client.Seating;
using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>What a surface last asked about which questions are still open, kept per project.</summary>
/// <remarks>
/// The web console asks in every project at once and the terminal in the one it shows, so a press in one project replaces
/// that project's asks and keeps every other's.
/// </remarks>
[Unit]
public sealed class StillOpenAsksTests
{
    private static StillOpenAsk Ask(string project, string agent, long exchange) =>
        new(project, AgentName.Create($"{agent}@{project}"), "room", [1], exchange, "Running");

    [Fact]
    public void Replacing_one_projects_asks_keeps_another_projects()
    {
        // Without the guard a press in beta forgot what was asked in alpha. Nobody was asked twice, since the next press
        // there finds the ask in the broker's record, but alpha's asks were gone from the page (review, lens 3 round 36).
        var asks = new StillOpenAsks();
        var alpha = Ask("alpha", "orders", 10);
        var oldBeta = Ask("beta", "billing", 20);
        var newBeta = Ask("beta", "inventory", 30);

        asks.Replace(["alpha", "beta"], [alpha, oldBeta]);
        asks.Replace(["beta"], [newBeta]);

        Assert.Equal([alpha, newBeta], asks.Latest());
    }
}
