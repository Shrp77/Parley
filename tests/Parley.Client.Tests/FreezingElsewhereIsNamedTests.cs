using Parley.Core;
using Xunit;

namespace Parley.Client.Tests;

/// <summary>That a freeze or unfreeze of every agent in one project names each agent it also changed in another project.</summary>
/// <remarks>
/// One process serves every project an agent is in, so Unfreeze all in one project released an agent frozen for another
/// project's sake, and the person was never told (review, lens 2 run B on 44c8e1e6).
/// </remarks>
[Unit]
public sealed class FreezingElsewhereIsNamedTests
{
    private static readonly AgentName Orders = AgentName.Create("orders@test");
    private static readonly AgentName Billing = AgentName.Create("billing@test");

    [Fact]
    public void An_agent_changed_in_another_project_is_named_with_it_and_one_changed_only_here_is_not()
    {
        var all = new FreezeAllOutcome(
            [
                new FreezeOutcome(Orders, [new FrozenIn("beta", true, null, null), new FrozenIn("alpha", true, null, null)]),
                new FreezeOutcome(Billing, [new FrozenIn("beta", true, null, null), new FrozenIn("alpha", false, null, null)]),
            ],
            null);

        Assert.Equal(" 'orders@test' was unfrozen in 'alpha' too.", AgentFreezing.Elsewhere(all, "beta", freeze: false));
        Assert.Equal(" 'orders@test' was frozen in 'alpha' too.", AgentFreezing.Elsewhere(all, "beta", freeze: true));
    }

    [Fact]
    public void A_project_that_could_not_be_reached_is_not_named_as_changed()
    {
        var all = new FreezeAllOutcome(
            [new FreezeOutcome(Orders, [new FrozenIn("beta", true, null, null), new FrozenIn("alpha", false, null, "down")])],
            null);

        Assert.Equal(string.Empty, AgentFreezing.Elsewhere(all, "beta", freeze: false));
    }
}
