using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// The bound on one turn, which the node enforces and the broker states.
/// </summary>
public sealed class TurnBudgetTests
{
    [Fact]
    public void A_turn_is_allowed_long_enough_to_be_worth_waiting_for()
    {
        // Somebody shortening the turn budget to make a test run faster
        // would silently narrow what an agent may be asked to do.
        Assert.True(
            TurnBudget.Longest >= TimeSpan.FromMinutes(10),
            "a turn is a model working on a real repository, not a request");
    }
}
