using Xunit;

namespace Parley.Core.Tests;

/// <summary>
/// That "accepted but not yet read by peers" is a comparison of two facts and never a flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>research.md R5, and the reason it is not a third column.</b> <c>agents.owns</c> is filled from an
/// agent's launch environment when it attaches (SPEC.md §12.3) and no operation writes it;
/// <c>agent_ownership_accepted.statement</c> is what a person agreed to. The line is pending exactly
/// while those differ, so there is no state to maintain and nothing to clear.
/// </para>
/// <para>
/// <b>Must fail against a flag.</b> The probe is the last test here: change the live line underneath,
/// as an agent restarting does, and the mark has to go away <em>without anybody being told</em>. A
/// boolean set at acceptance passes every other test in this file and fails that one — and the failure
/// it stands for is the one that reaches a person, a surface saying a line is pending for ever after
/// the agent came back carrying it.
/// </para>
/// <para>
/// In <c>Parley.Core.Tests</c> because the rule is a property of the record and this project reaches no
/// store — which is also the point: if the comparison needed a store it could not be made by the two
/// surfaces, and R5 exists because they reach none.
/// </para>
/// </remarks>
public sealed class PendingUntilItRestartsTests
{
    private static AgentRecord Agent(string? owns, string? accepted) =>
        new(
            AgentName.Create("orders@test"),
            "mcp",
            AgentTier.Pull,
            "/src/orders",
            null,
            null,
            false,
            AgentStatus.Active,
            owns,
            AgentPresence.Startable,
            accepted);

    [Fact]
    public void An_agent_nobody_has_accepted_a_line_for_is_not_pending()
    {
        // The ordinary case, and it must not be marked: every agent in a deployment where nobody has
        // used this feature would otherwise carry the mark.
        Assert.False(Agent(owns: "Orders and invoicing.", accepted: null).OwnsPending);
        Assert.False(Agent(owns: null, accepted: null).OwnsPending);
    }

    [Fact]
    public void A_line_accepted_for_an_agent_that_has_stated_nothing_is_pending()
    {
        // The commonest way this state is reached -- an agent enrolled without a line, asked, and
        // answered. Must fail against treating an absent live line as "nothing to compare".
        Assert.True(Agent(owns: null, accepted: "Orders and invoicing.").OwnsPending);
    }

    [Fact]
    public void A_line_accepted_over_a_different_one_is_pending()
    {
        Assert.True(Agent(owns: "Everything.", accepted: "Orders and invoicing.").OwnsPending);
    }

    [Fact]
    public void A_blank_live_line_is_the_same_as_none()
    {
        // `OwnershipStatement.Read` collapses blank to absent, so a launch variable set to an empty
        // string and one never set say the same thing. Must fail against comparing raw strings, which
        // would make an empty line and an absent one two different states.
        Assert.True(Agent(owns: "   ", accepted: "Orders and invoicing.").OwnsPending);
    }

    [Fact]
    public void Whitespace_around_the_two_does_not_make_them_differ()
    {
        // A launch configuration edited by hand carries whatever spacing somebody left. Two lines that
        // differ only in that are the same line, and marking them pending would show a mark that no
        // restart could ever clear.
        Assert.False(Agent(owns: " Orders and invoicing. ", accepted: "Orders and invoicing.").OwnsPending);
    }

    [Fact]
    public void The_mark_clears_itself_when_the_agent_comes_back_carrying_the_line()
    {
        // **THE PROBE.** This is what a restart does: the agent attaches, its adapter states the line
        // from its launch configuration, and `owns` becomes what was accepted. Nothing tells the record
        // that the acceptance has been honoured -- and nothing needs to, which is the property.
        //
        // **Must fail against a flag set at acceptance**, which is the implementation this exists to
        // forbid: it would pass every other test above and leave this one showing a line as pending
        // after peers had begun reading it.
        var accepted = Agent(owns: null, accepted: "Orders and invoicing.");

        Assert.True(accepted.OwnsPending);

        // The same acceptance, and the only thing that changed is what the agent itself now reports.
        var restarted = accepted with { Owns = "Orders and invoicing." };

        Assert.False(restarted.OwnsPending);
        Assert.Equal("Orders and invoicing.", restarted.OwnsAccepted);
    }

    [Fact]
    public void An_agent_that_comes_back_with_a_different_line_is_pending_again()
    {
        // Somebody edited the launch configuration by hand between the acceptance and the restart, or
        // the write went to a checkout that is not the one running. The mark returning is correct: what
        // peers read is still not what was agreed, and this is the only thing that would say so.
        var accepted = Agent(owns: null, accepted: "Orders and invoicing.");
        var restarted = accepted with { Owns = "Orders, invoicing and stock." };

        Assert.True(restarted.OwnsPending);
    }
}
